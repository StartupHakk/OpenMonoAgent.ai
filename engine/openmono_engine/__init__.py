"""OpenMono Mosaic: a pluggable Strata-style MoE runtime (CPU-testable core).

This package is the first milestone of a generic Mixture-of-Experts inference
engine. The hot path is required to keep expert (and embedding) weights in RAM
or VRAM after startup. Reading model weights from NVMe/SSD per token is not
allowed.

The CUDA doorbell, real GPU kernels, and GGUF ingest of multi-gigabyte
checkpoints are not in this milestone. See docs/ENGINE-DESIGN.md.
"""

from .adapters.base import FamilyAdapter, FamilySpec, RoutingDecision
from .adapters.registry import AdapterRegistry
from .arena import ExpertArena
from .cache import ExpertCache
from .doorbell import Doorbell, HostDispatcher
from .packer import PackFile, pack_experts, unpack_header
from .runtime import MosaicRuntime
from .types import ExpertId, ModelConfig, PackedExpert

__all__ = [
    "AdapterRegistry",
    "Doorbell",
    "ExpertArena",
    "ExpertCache",
    "ExpertId",
    "FamilyAdapter",
    "FamilySpec",
    "HostDispatcher",
    "ModelConfig",
    "MosaicRuntime",
    "PackedExpert",
    "PackFile",
    "RoutingDecision",
    "pack_experts",
    "unpack_header",
]
