import unittest

from openmono_engine.adapters.base import softmax_topk_route
from openmono_engine.adapters.glm45_air import Glm45AirAdapter
from openmono_engine.adapters.qwen38 import Qwen38FlashNextAdapter


class RouterTests(unittest.TestCase):
    def test_softmax_topk_picks_largest_and_normalizes(self) -> None:
        logits = [0.1, 5.0, 0.2, 4.0, 0.0, 0.0, 0.0, 0.0]
        d = softmax_topk_route(logits, top_k=2, n_shared=1, norm_topk=True)
        self.assertEqual(d.expert_ids, (1, 3))
        self.assertAlmostEqual(sum(d.weights), 1.0, places=6)
        self.assertEqual(d.shared_ids, (0,))
        self.assertGreater(d.weights[0], d.weights[1])

    def test_glm45_air_matches_config_top_k_on_tiny_logits(self) -> None:
        adapter = Glm45AirAdapter()
        logits = [float(i) for i in range(8)]
        d = adapter.route([0.0] * 8, logits)
        self.assertEqual(len(d.expert_ids), min(8, adapter.spec.top_k))
        self.assertEqual(set(d.expert_ids), {7, 6, 5, 4, 3, 2, 1, 0})

    def test_qwen38_top_k_ten_on_wide_logits(self) -> None:
        adapter = Qwen38FlashNextAdapter()
        logits = list(range(32))
        d = adapter.route(logits, logits)
        self.assertEqual(len(d.expert_ids), 10)
        self.assertEqual(d.expert_ids[0], 31)


if __name__ == "__main__":
    unittest.main()
