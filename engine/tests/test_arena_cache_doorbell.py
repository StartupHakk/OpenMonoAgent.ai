import unittest

from openmono_engine.arena import ArenaMiss, ExpertArena
from openmono_engine.cache import ExpertCache
from openmono_engine.doorbell import Doorbell, HostDispatcher
from openmono_engine.types import ExpertId


class ArenaCacheDoorbellTests(unittest.TestCase):
    def test_arena_miss_is_loud(self) -> None:
        arena = ExpertArena()
        arena.load_all({(0, 0): b"abc"})
        with self.assertRaises(ArenaMiss):
            arena.get(ExpertId(0, 1))

    def test_cache_evicts_to_budget(self) -> None:
        cache = ExpertCache(capacity_bytes=10)
        self.assertTrue(cache.admit(ExpertId(0, 0), b"12345"))
        self.assertTrue(cache.admit(ExpertId(0, 1), b"12345"))
        self.assertEqual(len(cache), 2)
        self.assertTrue(cache.admit(ExpertId(0, 2), b"12345"))
        self.assertEqual(len(cache), 2)
        self.assertGreaterEqual(cache.evictions, 1)
        self.assertFalse(cache.admit(ExpertId(0, 3), b"x" * 20))

    def test_doorbell_splits_hits_and_misses(self) -> None:
        arena = ExpertArena()
        arena.load_all({(1, 0): b"e0", (1, 1): b"e1", (1, 2): b"e2"})
        cache = ExpertCache(capacity_bytes=8)
        cache.admit(ExpertId(1, 0), b"e0")
        disp = HostDispatcher(arena, cache)
        result = disp.dispatch(Doorbell(layer=1, expert_ids=(0, 1, 2)))
        self.assertEqual([e.expert for e in result.gpu_hits], [0])
        self.assertEqual([e.expert for e in result.cpu_misses], [1, 2])
        # Misses are admitted, so a second token hits more.
        result2 = disp.dispatch(Doorbell(layer=1, expert_ids=(0, 1, 2)))
        self.assertEqual(len(result2.gpu_hits), 3)
        self.assertEqual(len(result2.cpu_misses), 0)


if __name__ == "__main__":
    unittest.main()
