"""Shared decode runtime: arena + cache + doorbell + family adapter.

CPU-only. Real attention kernels and CUDA doorbells are a later milestone.
The invariant this class enforces: after ``load_pack``, expert payloads come
from the RAM arena (and optional VRAM cache copies), never from the pack file.
"""

from __future__ import annotations

import struct
from array import array
from pathlib import Path

from .adapters.base import FamilyAdapter, LayerWeights, RoutingDecision
from .adapters.registry import AdapterRegistry
from .arena import ExpertArena
from .cache import ExpertCache
from .doorbell import Doorbell, HostDispatcher
from .packer import PackFile
from .tensor import add, from_list, rmsnorm, zeros
from .types import ModelConfig


class HotPathDiskRead(RuntimeError):
    pass


class MosaicRuntime:
    def __init__(
        self,
        adapter: FamilyAdapter,
        config: ModelConfig,
        *,
        cache_bytes: int = 4096,
    ):
        self.adapter = adapter
        self.config = config
        self.arena = ExpertArena()
        self.cache = ExpertCache(cache_bytes)
        self.dispatcher = HostDispatcher(self.arena, self.cache)
        self._pack: PackFile | None = None
        self.layer_weights: dict[int, LayerWeights] = {}
        self.embed: dict[int, array] = {}
        self.unembed: dict[int, array] = {}

    @classmethod
    def for_family(cls, family_id: str, config: ModelConfig | None = None, **kwargs) -> "MosaicRuntime":
        adapter = AdapterRegistry().get(family_id)
        cfg = config or adapter.model_config(hidden=8, n_layers=2, n_experts=8, vocab=16)
        cfg = ModelConfig(
            family=adapter.spec.family_id,
            n_layers=cfg.n_layers,
            n_experts=cfg.n_experts,
            top_k=min(adapter.spec.top_k, cfg.n_experts),
            n_shared=adapter.spec.n_shared,
            hidden_size=cfg.hidden_size,
            expert_dim=cfg.expert_dim,
            first_k_dense=min(adapter.spec.first_k_dense, cfg.n_layers - 1) if cfg.n_layers else 0,
            vocab_size=cfg.vocab_size,
            has_mtp=adapter.spec.has_mtp,
            has_ple=adapter.spec.has_ple,
        )
        return cls(adapter, cfg, **kwargs)

    def load_pack(self, path: str | Path) -> None:
        pack = PackFile(path)
        pack.load_into_arena(self.arena)
        self._pack = pack
        if pack.disk_reads != 1:
            raise HotPathDiskRead(f"expected 1 pack read at load, got {pack.disk_reads}")

    def assert_no_ssd_hot_path(self) -> None:
        if self._pack is not None and self._pack.disk_reads > 1:
            raise HotPathDiskRead(
                f"pack was read {self._pack.disk_reads} times; per-token SSD reads are forbidden"
            )

    def install_synthetic_weights(self, seed: int = 1) -> None:
        """Fill tiny CPU weights so tests can run a forward pass."""
        h = self.config.hidden_size
        d = self.config.expert_dim
        n = self.config.n_experts
        rng = _Lcg(seed)
        for layer in range(self.config.n_layers):
            router = _rand_array(rng, n * h, scale=0.2)
            experts_w1: dict[int, array] = {}
            experts_w2: dict[int, array] = {}
            for eid in range(n):
                experts_w1[eid] = _rand_array(rng, d * h, scale=0.15)
                experts_w2[eid] = _rand_array(rng, h * d, scale=0.15)
            shared_w1 = {0: _rand_array(rng, d * h, scale=0.1)} if self.config.n_shared else {}
            shared_w2 = {0: _rand_array(rng, h * d, scale=0.1)} if self.config.n_shared else {}
            self.layer_weights[layer] = LayerWeights(
                router=router,
                experts_w1=experts_w1,
                experts_w2=experts_w2,
                shared_w1=shared_w1,
                shared_w2=shared_w2,
            )
        v = self.config.vocab_size
        for tok in range(v):
            self.embed[tok] = _rand_array(rng, h, scale=0.3)
            self.unembed[tok] = _rand_array(rng, h, scale=0.3)

    def forward_token(self, token_id: int) -> tuple[int, list[RoutingDecision]]:
        """One decode step. Returns (next_id, per-layer routing)."""
        self.assert_no_ssd_hot_path()
        hidden = array("f", self.embed.get(token_id, zeros(self.config.hidden_size)))
        decisions: list[RoutingDecision] = []
        for layer in range(self.config.n_layers):
            hidden = self.adapter.attention(rmsnorm(hidden), layer)
            lw = self.layer_weights[layer]
            if layer < self.config.first_k_dense:
                # Dense stand-in: shared expert 0 only.
                hidden = add(hidden, self.adapter.expert_forward(
                    hidden, lw.shared_w1[0], lw.shared_w2[0], self.config.expert_dim
                ))
                continue
            logits = _router_logits(lw.router, hidden, self.config.n_experts)
            decision = self.adapter.route(list(hidden), logits)
            decisions.append(decision)
            bell = Doorbell(layer=layer, expert_ids=decision.expert_ids)
            dispatched = self.dispatcher.dispatch(bell)
            # Touch arena payloads so a missing expert fails loudly; compute on CPU.
            expert_outs: dict[int, array] = {}
            for eid in decision.expert_ids:
                _ = dispatched.payloads_gpu.get((layer, eid)) or dispatched.payloads_cpu[(layer, eid)]
                expert_outs[eid] = self.adapter.expert_forward(
                    hidden, lw.experts_w1[eid], lw.experts_w2[eid], self.config.expert_dim
                )
            hidden = add(hidden, self.adapter.mix_experts(hidden, decision, lw, expert_outs))
        next_id = _greedy_unembed(hidden, self.unembed, self.config.vocab_size)
        self.assert_no_ssd_hot_path()
        return next_id, decisions

    def generate(self, prompt_ids: list[int], n_new: int) -> list[int]:
        ids = list(prompt_ids)
        if not ids:
            ids = [0]
        for _ in range(n_new):
            nxt, _ = self.forward_token(ids[-1])
            ids.append(nxt)
            extra = self.adapter.draft(ids, n_predict=1)
            # Draft tokens are verified by another forward; accept if they match.
            for guess in extra:
                verify, _ = self.forward_token(ids[-1])
                if verify == guess:
                    ids.append(verify)
                else:
                    ids.append(verify)
                    break
        return ids


def _router_logits(router: array, hidden: array, n_experts: int) -> list[float]:
    h = len(hidden)
    out = []
    for r in range(n_experts):
        acc = 0.0
        base = r * h
        for c in range(h):
            acc += router[base + c] * hidden[c]
        out.append(acc)
    return out


def _greedy_unembed(hidden: array, unembed: dict[int, array], vocab: int) -> int:
    best_i = 0
    best = float("-inf")
    for i in range(vocab):
        row = unembed.get(i)
        if row is None:
            continue
        acc = 0.0
        for a, b in zip(hidden, row):
            acc += a * b
        if acc > best:
            best = acc
            best_i = i
    return best_i


class _Lcg:
    def __init__(self, seed: int):
        self.state = seed & 0xFFFFFFFF

    def next(self) -> int:
        self.state = (1664525 * self.state + 1013904223) & 0xFFFFFFFF
        return self.state


def _rand_array(rng: _Lcg, n: int, scale: float) -> array:
    xs = []
    for _ in range(n):
        xs.append(((rng.next() / 0xFFFFFFFF) * 2.0 - 1.0) * scale)
    return from_list(xs)


def floats_from_bytes(payload: bytes, n: int) -> array:
    """Interpret the start of a pack payload as float32s (synthetic packs use bytes)."""
    take = min(len(payload), n * 4)
    padded = payload[:take] + b"\x00" * (n * 4 - take)
    return array("f", struct.unpack("<" + "f" * n, padded[: n * 4]))
