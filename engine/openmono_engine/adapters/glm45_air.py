"""GLM-4.5-Air family adapter (first non-Qwen3.8 adapter).

Architecture from Hugging Face ``zai-org/GLM-4.5-Air`` config.json:
46 layers, 128 routed experts, 8 active per token, 1 shared, first layer dense,
softmax top-k with ``norm_topk_prob``, one MTP (next-n) layer.

This is the best large-MoE fit for 24 GB VRAM + 64 GB RAM with no SSD hot path:
Unsloth ``GLM-4.5-Air-UD-IQ2_XXS.gguf`` is 39.75 GB (Hugging Face siblings,
fetched 2026-10-17). The whole file is too big for 24 GB VRAM, so experts
belong in a RAM arena; attention and a hot-expert cache fit in 24 GB VRAM.
There is no 29 GB n-gram PLE table.
"""

from __future__ import annotations

from typing import Sequence

from .base import FamilyAdapter, FamilySpec, RoutingDecision, softmax_topk_route


class Glm45AirAdapter(FamilyAdapter):
    spec = FamilySpec(
        family_id="glm45_air",
        display_name="GLM-4.5-Air",
        n_layers=46,
        n_experts=128,
        top_k=8,
        n_shared=1,
        hidden_size=4096,
        expert_dim=1408,
        first_k_dense=1,
        has_mtp=True,
        has_ple=False,
        attention_kind="standard",
        router_kind="softmax_topk",
        notes=(
            "glm4_moe. Standard attention + MoE (not Gated DeltaNet). "
            "Best 24 GB / 64 GB large-MoE target at UD-IQ2_XXS (39.75 GB)."
        ),
        model_specific=(
            "first_k_dense_replace=1 (layer 0 is a dense MLP, not MoE)",
            "partial_rotary_factor=0.5",
            "MTP: num_nextn_predict_layers=1",
            "routed_scaling_factor=1.0, n_group=1, topk_group=1",
        ),
    )

    def route(self, hidden: Sequence[float], router_logits: Sequence[float] | None = None) -> RoutingDecision:
        logits = list(router_logits) if router_logits is not None else list(hidden)
        n = len(logits)
        top_k = min(self.spec.top_k, n)
        return softmax_topk_route(logits, top_k, self.spec.n_shared, norm_topk=True)

    def draft(self, token_ids: Sequence[int], n_predict: int = 1) -> list[int]:
        # Tiny speculative stand-in: copy the last token n_predict times.
        # A real MTP head lives on GPU next to the output head.
        if not token_ids or n_predict <= 0:
            return []
        return [int(token_ids[-1])] * n_predict
