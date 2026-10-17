"""Adapter registry: family_id -> FamilyAdapter."""

from __future__ import annotations

from .base import FamilyAdapter
from .glm45_air import Glm45AirAdapter
from .qwen38 import Qwen38FlashNextAdapter


class AdapterRegistry:
    def __init__(self) -> None:
        self._adapters: dict[str, FamilyAdapter] = {}
        self.register(Glm45AirAdapter())
        self.register(Qwen38FlashNextAdapter())

    def register(self, adapter: FamilyAdapter) -> None:
        self._adapters[adapter.spec.family_id] = adapter

    def get(self, family_id: str) -> FamilyAdapter:
        try:
            return self._adapters[family_id]
        except KeyError as exc:
            known = ", ".join(sorted(self._adapters)) or "(none)"
            raise KeyError(f"unknown family {family_id!r}; known: {known}") from exc

    def families(self) -> list[str]:
        return sorted(self._adapters)

    @property
    def default_family(self) -> str:
        return "glm45_air"
