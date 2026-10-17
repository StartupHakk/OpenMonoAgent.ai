import tempfile
import unittest
from pathlib import Path

from openmono_engine.adapters.glm45_air import Glm45AirAdapter
from openmono_engine.arena import ExpertArena
from openmono_engine.packer import MAGIC, PackFile, pack_synthetic, unpack_header
from openmono_engine.types import ExpertId


class PackerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.cfg = Glm45AirAdapter().model_config(hidden=8, n_layers=2, n_experts=4, vocab=8)
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "tiny.omos"

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_roundtrip_header_and_payloads(self) -> None:
        pack_synthetic(self.path, self.cfg, nbytes=32)
        data = self.path.read_bytes()
        self.assertTrue(data.startswith(MAGIC))
        header = unpack_header(data)
        self.assertEqual(header.family, "glm45_air")
        self.assertEqual(header.n_experts, 4)
        self.assertGreater(header.n_entries, 0)

        pack = PackFile(self.path)
        self.assertEqual(pack.disk_reads, 0)
        arena = ExpertArena()
        pack.load_into_arena(arena)
        self.assertEqual(pack.disk_reads, 1)
        self.assertEqual(len(arena), header.n_entries)
        blob = arena.get(ExpertId(layer=self.cfg.first_k_dense, expert=0))
        self.assertEqual(len(blob), 32)

    def test_second_blob_read_is_visible(self) -> None:
        pack_synthetic(self.path, self.cfg, nbytes=16)
        pack = PackFile(self.path)
        pack.load_blob()
        pack.load_blob()
        self.assertEqual(pack.disk_reads, 2)


if __name__ == "__main__":
    unittest.main()
