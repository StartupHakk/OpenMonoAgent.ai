import unittest

from openmono_engine.hardware import ROWS, best_fit_24_64, fits_target
from openmono_engine.strata_plan import memtotal_gb, pick_plan


class HardwareTests(unittest.TestCase):
    def test_best_fit_is_glm45_air(self) -> None:
        row = best_fit_24_64()
        self.assertEqual(row.family, "glm45_air")
        self.assertLessEqual(row.ram_resident_gb, 64.0)
        self.assertLessEqual(row.vram_attn_gb, 24.0)
        self.assertTrue(fits_target(row))
        self.assertAlmostEqual(row.file_gb, 39.75, places=2)

    def test_no_nemotron_rows(self) -> None:
        blob = " ".join(r.name.lower() + r.hf_repo.lower() for r in ROWS)
        self.assertNotIn("nemotron", blob)

    def test_flash_next_ple_ram_does_not_fit_64(self) -> None:
        qwen = next(r for r in ROWS if r.quant == "IQ2_XS" and "Flash-Next" in r.name)
        self.assertGreater(qwen.ram_resident_gb, 64.0)
        coder = next(r for r in ROWS if "Coder" in r.name)
        self.assertLessEqual(coder.ram_resident_gb, 64.0)


class StrataPlanTests(unittest.TestCase):
    def test_memtotal_parses_proc_text(self) -> None:
        text = "MemTotal:       65378412 kB\nMemFree:        1000 kB\n"
        # 65378412 / 1048576 = 62
        self.assertEqual(memtotal_gb(text), 62)

    def test_64gb_class_picks_coder_ram(self) -> None:
        plan = pick_plan(62)
        self.assertEqual(plan.family, "coder")
        self.assertEqual(plan.model, "IQ1_M")
        self.assertEqual(plan.ple_io, "ram")

    def test_96gb_class_picks_iq3_s_ram(self) -> None:
        plan = pick_plan(93)
        self.assertEqual(plan.family, "qwen")
        self.assertEqual(plan.model, "IQ3_S")
        self.assertEqual(plan.ple_io, "ram")

    def test_48gb_class_does_not_force_ple_ram(self) -> None:
        plan = pick_plan(46)
        self.assertEqual(plan.ple_io, "direct")
        self.assertEqual(plan.family, "qwen")
        self.assertEqual(plan.model, "")

    def test_explicit_iq2_xs_on_64gb_keeps_family_skips_ram(self) -> None:
        plan = pick_plan(62, family="qwen", model="IQ2_XS")
        self.assertEqual(plan.family, "qwen")
        self.assertEqual(plan.model, "IQ2_XS")
        self.assertEqual(plan.ple_io, "direct")

    def test_family_qwen_without_model_on_64gb_does_not_switch_to_coder(self) -> None:
        plan = pick_plan(62, family="qwen")
        self.assertEqual(plan.family, "qwen")
        self.assertEqual(plan.model, "IQ2_XS")
        self.assertEqual(plan.ple_io, "direct")

    def test_explicit_override_ple_io(self) -> None:
        plan = pick_plan(62, family="coder", model="IQ1_M", ple_io="mmap")
        self.assertEqual(plan.ple_io, "mmap")


if __name__ == "__main__":
    unittest.main()
