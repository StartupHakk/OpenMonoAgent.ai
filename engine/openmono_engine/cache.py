"""Adaptive hot-expert cache (VRAM stand-in).

Strata fills leftover VRAM with the experts used most often and updates the
set as the conversation runs. This CPU object uses a byte budget and a
frequency + recency ranking. It holds *copies* of arena payloads; it never
reduces the RAM arena (matching Strata: the GPU cache does not free RAM).
"""

from __future__ import annotations

from collections import OrderedDict

from .types import ExpertId


class ExpertCache:
    def __init__(self, capacity_bytes: int):
        if capacity_bytes < 0:
            raise ValueError("capacity_bytes must be >= 0")
        self.capacity_bytes = capacity_bytes
        self._data: OrderedDict[tuple[int, int], bytes] = OrderedDict()
        self._freq: dict[tuple[int, int], int] = {}
        self._used = 0
        self.hits = 0
        self.misses = 0
        self.evictions = 0

    def __len__(self) -> int:
        return len(self._data)

    @property
    def used_bytes(self) -> int:
        return self._used

    def contains(self, expert_id: ExpertId | tuple[int, int]) -> bool:
        key = _key(expert_id)
        return key in self._data

    def get(self, expert_id: ExpertId | tuple[int, int]) -> bytes | None:
        key = _key(expert_id)
        blob = self._data.get(key)
        if blob is None:
            self.misses += 1
            return None
        self.hits += 1
        self._freq[key] = self._freq.get(key, 0) + 1
        self._data.move_to_end(key)
        return blob

    def admit(self, expert_id: ExpertId | tuple[int, int], payload: bytes) -> bool:
        """Insert if the payload fits the budget. Evict coldest experts first."""
        key = _key(expert_id)
        size = len(payload)
        if size > self.capacity_bytes:
            return False
        if key in self._data:
            self._data.move_to_end(key)
            self._freq[key] = self._freq.get(key, 0) + 1
            return True
        while self._data and self._used + size > self.capacity_bytes:
            self._evict_one()
        if self._used + size > self.capacity_bytes:
            return False
        self._data[key] = payload
        self._used += size
        self._freq[key] = self._freq.get(key, 0) + 1
        return True

    def _evict_one(self) -> None:
        # Coldest = lowest frequency, then least recently used (OrderedDict front).
        if not self._data:
            return
        coldest = min(self._data.keys(), key=lambda k: (self._freq.get(k, 0), list(self._data).index(k)))
        blob = self._data.pop(coldest)
        self._used -= len(blob)
        self._freq.pop(coldest, None)
        self.evictions += 1

    @property
    def hit_rate(self) -> float:
        total = self.hits + self.misses
        return self.hits / total if total else 0.0


def _key(expert_id: ExpertId | tuple[int, int]) -> tuple[int, int]:
    if isinstance(expert_id, ExpertId):
        return (expert_id.layer, expert_id.expert)
    return expert_id
