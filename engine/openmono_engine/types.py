"""Shared dataclasses for the Mosaic engine."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Sequence


@dataclass(frozen=True)
class ExpertId:
    layer: int
    expert: int


@dataclass(frozen=True)
class PackedExpert:
    expert_id: ExpertId
    payload: bytes


@dataclass(frozen=True)
class ModelConfig:
    family: str
    n_layers: int
    n_experts: int
    top_k: int
    n_shared: int
    hidden_size: int
    expert_dim: int
    first_k_dense: int = 0
    vocab_size: int = 32
    has_mtp: bool = False
    has_ple: bool = False
    # Bytes on disk of one routed expert in the pack (synthetic or real).
    expert_bytes: int = 0
    extras: dict[str, str] = field(default_factory=dict)

    @property
    def n_moe_layers(self) -> int:
        return max(0, self.n_layers - self.first_k_dense)

    @property
    def n_routed_experts_total(self) -> int:
        return self.n_moe_layers * self.n_experts


@dataclass(frozen=True)
class FeasibilityRow:
    name: str
    family: str
    quant: str
    hf_repo: str
    hf_files: str
    file_gb: float
    total_params_b: float
    active_params_b: float
    bpw_experts: str
    n_layers: int
    n_experts: int
    top_k: int
    n_shared: int
    # RAM needed for a RAM-resident load (weights + small runtime), GiB.
    ram_resident_gb: float
    # VRAM for attention / KV / hot-expert cache at 32k ctx, GiB (estimate).
    vram_attn_gb: float
    ssd_hot_path: str
    fits_24_64: str
    notes: str


@dataclass(frozen=True)
class StrataRamPlan:
    family: str
    model: str
    ple_io: str
    reason: str
    experts_gb: float
    ple_gb: float
    os_gb: float
    mem_gb: int

    def as_dict(self) -> dict[str, object]:
        return {
            "family": self.family,
            "model": self.model,
            "ple_io": self.ple_io,
            "reason": self.reason,
            "experts_gb": self.experts_gb,
            "ple_gb": self.ple_gb,
            "os_gb": self.os_gb,
            "mem_gb": self.mem_gb,
        }


def as_floats(values: Sequence[float]) -> list[float]:
    return [float(v) for v in values]
