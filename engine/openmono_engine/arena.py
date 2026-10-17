"""Pinned-style RAM expert arena.

All routed experts live in process memory after ``load_all``. Lookups never
touch the pack file. This is the OpenMono equivalent of Strata's RAM arena.
"""

from __future__ import annotations

from .types import ExpertId


class ArenaMiss(KeyError):
    pass


class ExpertArena:
    def __init__(self) -> None:
        self._store: dict[tuple[int, int], bytes] = {}
        self._loads = 0
        self._lookups = 0

    def load_all(self, payloads: dict[tuple[int, int], bytes]) -> None:
        self._store = dict(payloads)
        self._loads += 1

    def get(self, expert_id: ExpertId | tuple[int, int]) -> bytes:
        self._lookups += 1
        key = (expert_id.layer, expert_id.expert) if isinstance(expert_id, ExpertId) else expert_id
        try:
            return self._store[key]
        except KeyError as exc:
            raise ArenaMiss(f"expert {key} is not in the RAM arena") from exc

    def contains(self, expert_id: ExpertId | tuple[int, int]) -> bool:
        key = (expert_id.layer, expert_id.expert) if isinstance(expert_id, ExpertId) else expert_id
        return key in self._store

    def __len__(self) -> int:
        return len(self._store)

    @property
    def loads(self) -> int:
        return self._loads

    @property
    def lookups(self) -> int:
        return self._lookups

    @property
    def bytes_resident(self) -> int:
        return sum(len(v) for v in self._store.values())
