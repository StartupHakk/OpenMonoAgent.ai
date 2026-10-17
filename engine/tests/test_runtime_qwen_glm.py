import os
import tempfile
import unittest
from pathlib import Path

from openmono_engine.adapters.registry import AdapterRegistry
from openmono_engine.packer import pack_synthetic
from openmono_engine.runtime import HotPathDiskRead, MosaicRuntime


class RuntimeTests(unittest.TestCase):
    def test_registry_default_is_glm45_air(self) -> None:
        reg = AdapterRegistry()
        self.assertEqual(reg.default_family, "glm45_air")
        self.assertIn("glm45_air", reg.families())
        self.assertIn("qwen38_flash_next", reg.families())
        spec = reg.get("glm45_air").spec
        self.assertEqual(spec.n_experts, 128)
        self.assertEqual(spec.top_k, 8)
        self.assertEqual(spec.n_shared, 1)
        self.assertEqual(spec.first_k_dense, 1)
        self.assertFalse(spec.has_ple)

    def test_glm45_cpu_generate_with_synthetic_pack(self) -> None:
        rt = MosaicRuntime.for_family("glm45_air", cache_bytes=128)
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "toy.omos"
            pack_synthetic(path, rt.config, nbytes=8)
            rt.load_pack(path)
            rt.install_synthetic_weights(seed=7)
            out = rt.generate([1, 2], n_new=3)
            self.assertGreaterEqual(len(out), 5)
            self.assertEqual(rt._pack.disk_reads, 1)  # noqa: SLF001
            # Unlink the pack: hot path must not need the file.
            os.unlink(path)
            more = rt.generate([0], n_new=2)
            self.assertGreaterEqual(len(more), 3)
            rt.assert_no_ssd_hot_path()

    def test_hot_path_disk_read_raises(self) -> None:
        rt = MosaicRuntime.for_family("glm45_air")
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "toy.omos"
            pack_synthetic(path, rt.config, nbytes=4)
            rt.load_pack(path)
            rt._pack.load_blob()  # noqa: SLF001
            with self.assertRaises(HotPathDiskRead):
                rt.assert_no_ssd_hot_path()


if __name__ == "__main__":
    unittest.main()
