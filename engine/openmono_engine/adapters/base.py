"""Family adapter interface.

Each model family implements loader/packer/router/attention/drafter hooks.
The shared runtime owns the RAM arena, VRAM expert cache, and doorbell.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Sequence

from ..tensor import array, from_list, matvec, softmax, topk_indices
from ..types import ModelConfig, PackedExpert


@dataclass(frozen=True)
class FamilySpec:
    family_id: str
    display_name: str
    n_layers: int
    n_experts: int
    top_k: int
    n_shared: int
    hidden_size: int
    expert_dim: int
    first_k_dense: int = 0
    has_mtp: bool = False
    has_ple: bool = False
    attention_kind: str = "standard"
    router_kind: str = "softmax_topk"
    notes: str = ""
    # Bits that are family-specific and must not leak into the shared runtime.
    model_specific: tuple[str, ...] = ()


@dataclass(frozen=True)
class RoutingDecision:
    expert_ids: tuple[int, ...]
    weights: tuple[float, ...]
    shared_ids: tuple[int, ...] = ()


@dataclass
class LayerWeights:
    """CPU-resident tiny weights for one MoE layer (synthetic or test)."""

    router: array  # (n_experts, hidden)
    experts_w1: dict[int, array]  # expert_id -> (expert_dim, hidden)
    experts_w2: dict[int, array]  # expert_id -> (hidden, expert_dim)
    shared_w1: dict[int, array] = field(default_factory=dict)
    shared_w2: dict[int, array] = field(default_factory=dict)


class FamilyAdapter(ABC):
    spec: FamilySpec

    def model_config(self, *, hidden: int | None = None, n_layers: int | None = None,
                     n_experts: int | None = None, vocab: int | None = None) -> ModelConfig:
        s = self.spec
        return ModelConfig(
            family=s.family_id,
            n_layers=n_layers if n_layers is not None else s.n_layers,
            n_experts=n_experts if n_experts is not None else s.n_experts,
            top_k=s.top_k if n_experts is None else min(s.top_k, n_experts if n_experts is not None else s.top_k),
            n_shared=s.n_shared,
            hidden_size=hidden if hidden is not None else s.hidden_size,
            expert_dim=s.expert_dim if hidden is None else max(2, (hidden + 1) // 2),
            first_k_dense=s.first_k_dense,
            vocab_size=vocab if vocab is not None else 32,
            has_mtp=s.has_mtp,
            has_ple=s.has_ple,
        )

    @abstractmethod
    def route(self, hidden: Sequence[float], router_logits: Sequence[float] | None = None) -> RoutingDecision:
        """Pick routed experts for one token at one layer."""

    def pack_layer_experts(self, layer: int, payloads: dict[int, bytes]) -> list[PackedExpert]:
        from ..types import ExpertId

        return [
            PackedExpert(expert_id=ExpertId(layer=layer, expert=eid), payload=payloads[eid])
            for eid in sorted(payloads)
        ]

    def attention(self, hidden: array, _layer: int) -> array:
        """Default: identity attention (CPU stub). Families override as needed."""
        return hidden

    def draft(self, token_ids: Sequence[int], n_predict: int = 1) -> list[int]:
        """Optional MTP / speculative head. Default: no draft tokens."""
        del token_ids, n_predict
        return []

    def mix_experts(
        self,
        hidden: array,
        decision: RoutingDecision,
        layer_w: LayerWeights,
        expert_outputs: dict[int, array],
    ) -> array:
        """Weighted sum of routed expert outputs plus shared experts."""
        from ..tensor import add, scale, zeros

        out = zeros(len(hidden))
        for eid, w in zip(decision.expert_ids, decision.weights):
            if eid not in expert_outputs:
                continue
            out = add(out, scale(expert_outputs[eid], w))
        for sid in decision.shared_ids:
            w1 = layer_w.shared_w1.get(sid)
            w2 = layer_w.shared_w2.get(sid)
            if w1 is None or w2 is None:
                continue
            out = add(out, self._swiglu(hidden, w1, w2, layer_w))
        return out

    def expert_forward(self, hidden: array, w1: array, w2: array, expert_dim: int) -> array:
        from ..tensor import silu, zeros

        h = len(hidden)
        up = zeros(expert_dim)
        for r in range(expert_dim):
            acc = 0.0
            base = r * h
            for c in range(h):
                acc += w1[base + c] * hidden[c]
            up[r] = silu(acc)
        down = zeros(h)
        for r in range(h):
            acc = 0.0
            base = r * expert_dim
            for c in range(expert_dim):
                acc += w2[base + c] * up[c]
            down[r] = acc
        return down

    @staticmethod
    def _swiglu(hidden: array, w1: array, w2: array, layer_w: LayerWeights) -> array:
        del layer_w
        # Shared expert uses the same tiny SwiGLU as routed experts.
        expert_dim = len(w1) // len(hidden)
        adapter = _SwigluHelper
        return adapter.forward(hidden, w1, w2, expert_dim)


class _SwigluHelper:
    @staticmethod
    def forward(hidden: array, w1: array, w2: array, expert_dim: int) -> array:
        # Local import cycle avoidance: duplicate the tiny loop.
        from ..tensor import silu, zeros

        h = len(hidden)
        up = zeros(expert_dim)
        for r in range(expert_dim):
            acc = 0.0
            base = r * h
            for c in range(h):
                acc += w1[base + c] * hidden[c]
            up[r] = silu(acc)
        down = zeros(h)
        for r in range(h):
            acc = 0.0
            base = r * expert_dim
            for c in range(expert_dim):
                acc += w2[base + c] * up[c]
            down[r] = acc
        return down


def softmax_topk_route(
    logits: Sequence[float],
    top_k: int,
    n_shared: int,
    *,
    norm_topk: bool = True,
) -> RoutingDecision:
    scores = [float(x) for x in logits]
    probs = softmax(scores)
    ids = topk_indices(probs, top_k)
    weights = [probs[i] for i in ids]
    if norm_topk:
        s = sum(weights)
        if s > 0:
            weights = [w / s for w in weights]
    shared = tuple(range(n_shared)) if n_shared else ()
    return RoutingDecision(expert_ids=tuple(ids), weights=tuple(weights), shared_ids=shared)


def router_logits_from_hidden(hidden: Sequence[float], router: array, n_experts: int) -> list[float]:
    vec = from_list(list(hidden))
    out = matvec(router, vec, n_experts, len(vec))
    return list(out)
