"""Qwen3.8-Flash-Next family notes (not the first Mosaic adapter).

Strata's kernels and pack format are tied to this architecture. The Mosaic
runtime treats these bits as adapter-owned, not as engine invariants:

- 48 layers x 512 routed experts, 10 routed + 1 shared per token
- Gated DeltaNet mixers interleaved with gated attention
- 51B-class n-gram per-layer token embedding (PLE), ~26.8-28.8 GB, SSD by
  default in upstream Strata (``--ple-io direct``)
- MTP draft head (~6 GB extra)
- ISTA-DASLab GSQ-RCO 2 to 3.3 bpw experts, Strata pack (``tools/strata_pack.py``)

OpenMono keeps using upstream Strata for this family. Mosaic does not reimplement
those kernels in this milestone.
"""

from __future__ import annotations

from typing import Sequence

from .base import FamilyAdapter, FamilySpec, RoutingDecision, softmax_topk_route


class Qwen38FlashNextAdapter(FamilyAdapter):
    spec = FamilySpec(
        family_id="qwen38_flash_next",
        display_name="Qwen3.8-Flash-Next",
        n_layers=48,
        n_experts=512,
        top_k=10,
        n_shared=1,
        hidden_size=0,
        expert_dim=0,
        has_mtp=True,
        has_ple=True,
        attention_kind="gated_deltanet_hybrid",
        router_kind="softmax_topk",
        notes="Served by upstream Strata, not by Mosaic in this milestone.",
        model_specific=(
            "51B n-gram PLE table (~26.8 GiB shard 2); --ple-io ram|mmap|direct",
            "Gated DeltaNet mixers + gated-residual weights",
            "Strata native pack (experts.bin) + GPU doorbell",
            "GSQ-RCO i-quants (Q2_0 / IQ2_XS / IQ3_XXS / IQ3_S / Coder IQ1_M)",
        ),
    )

    def route(self, hidden: Sequence[float], router_logits: Sequence[float] | None = None) -> RoutingDecision:
        logits = list(router_logits) if router_logits is not None else list(hidden)
        n = len(logits)
        top_k = min(self.spec.top_k, n)
        return softmax_topk_route(logits, top_k, self.spec.n_shared, norm_topk=True)
