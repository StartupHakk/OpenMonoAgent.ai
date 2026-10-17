"""GPU doorbell: chosen expert ids handed to the host.

Strata's GPU router writes the routed expert ids into a host-visible buffer
(the doorbell). The host then:

- runs cached experts on the GPU
- DMA-copies or computes misses on the CPU in place from the RAM arena

This module is the CPU-testable contract. No CUDA here.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .arena import ExpertArena
from .cache import ExpertCache
from .types import ExpertId


@dataclass(frozen=True)
class Doorbell:
    layer: int
    expert_ids: tuple[int, ...]


@dataclass
class DispatchResult:
    gpu_hits: list[ExpertId] = field(default_factory=list)
    cpu_misses: list[ExpertId] = field(default_factory=list)
    payloads_cpu: dict[tuple[int, int], bytes] = field(default_factory=dict)
    payloads_gpu: dict[tuple[int, int], bytes] = field(default_factory=dict)


class HostDispatcher:
    def __init__(self, arena: ExpertArena, cache: ExpertCache):
        self.arena = arena
        self.cache = cache

    def dispatch(self, bell: Doorbell, *, admit_misses: bool = True) -> DispatchResult:
        result = DispatchResult()
        for eid in bell.expert_ids:
            expert = ExpertId(layer=bell.layer, expert=eid)
            cached = self.cache.get(expert)
            if cached is not None:
                result.gpu_hits.append(expert)
                result.payloads_gpu[(bell.layer, eid)] = cached
                continue
            payload = self.arena.get(expert)
            result.cpu_misses.append(expert)
            result.payloads_cpu[(bell.layer, eid)] = payload
            if admit_misses:
                self.cache.admit(expert, payload)
        return result
