"""Pick a Strata family/quant + --ple-io so 64 GB boxes avoid SSD hot path.

Expert RAM figures are from upstream Strata docs/MODELS.md (GiB):
  Coder IQ1_M 23, Q2_0 34, IQ2_XS 36, IQ3_XXS 43, IQ3_S 50.
PLE (n-gram table) is shard 2: 26.82 GB on Hugging Face, treated as 27 GiB
with a 29 GiB budget so page-lock overhead still fits. OS/runtime headroom
is 8 GiB.

IQ2_XS + PLE-in-RAM is ~36+29+8 = 73 GiB, so a 64 GB box cannot do it.
The 64 GB RAM-resident plan is Coder + --ple-io ram.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .types import StrataRamPlan

OS_GIB = 8
PLE_GIB = 29

# family, model, expert GiB (Strata MODELS.md)
_PLANS: tuple[tuple[str, str, float], ...] = (
    ("qwen", "IQ3_S", 50.0),
    ("qwen", "IQ3_XXS", 43.0),
    ("qwen", "IQ2_XS", 36.0),
    ("qwen", "Q2_0", 34.0),
    ("coder", "IQ1_M", 23.0),
)

_EXPERTS = {(fam, model): gb for fam, model, gb in _PLANS}


def memtotal_gb(meminfo: str | None = None) -> int:
    """GiB of MemTotal. 64 GB DIMMs usually report 62-63 here."""
    text = meminfo
    if text is None:
        path = Path("/proc/meminfo")
        if not path.exists():
            return 0
        text = path.read_text()
    for line in text.splitlines():
        if line.startswith("MemTotal:"):
            kb = int(line.split()[1])
            return kb // 1048576
    return 0


def experts_gb(family: str, model: str) -> float | None:
    fam = family.strip().lower()
    mod = model.strip()
    if fam == "swift":
        fam = "qwen"
    if fam == "coder":
        return _EXPERTS.get(("coder", "IQ1_M"))
    return _EXPERTS.get((fam, mod))


def ple_fits(mem_gb: int, expert_gb: float) -> bool:
    return mem_gb >= int(expert_gb + PLE_GIB + OS_GIB)


def pick_plan(
    mem_gb: int,
    *,
    family: str | None = None,
    model: str | None = None,
    ple_io: str | None = None,
) -> StrataRamPlan:
    """Choose family/model/ple_io.

    Explicit family/model are kept. ple_io='ram' is applied only when the
    combination fits. When family/model are empty, pick the highest-quality
    plan that can keep both experts and the PLE table in RAM.
    """
    family = (family or "").strip() or None
    model = (model or "").strip() or None
    ple_override = (ple_io or "").strip() or None

    if family and not model:
        if family == "coder":
            model = "IQ1_M"
        elif family in ("qwen", "swift"):
            # Stay on this family. Prefer a size whose experts + PLE fit in RAM;
            # otherwise IQ2_XS (Strata's usual pick) without forcing PLE-in-RAM.
            for fam, mod, exp in _PLANS:
                if fam == "qwen" and ple_fits(mem_gb, exp):
                    model = mod
                    break
            if not model:
                model = "IQ2_XS"

    if family and model:
        exp = experts_gb(family, model)
        if exp is None:
            exp = 36.0
        if ple_override:
            chosen_ple = ple_override
            reason = f"user override family={family} model={model} ple_io={ple_override}"
        elif ple_fits(mem_gb, exp):
            chosen_ple = "ram"
            reason = (
                f"keep {family}/{model} (experts {exp:.0f} GiB) and load the "
                f"{PLE_GIB} GiB PLE table into RAM; MemTotal {mem_gb} GiB"
            )
        else:
            chosen_ple = "direct"
            reason = (
                f"keep {family}/{model} (experts {exp:.0f} GiB) but PLE-in-RAM "
                f"needs {exp:.0f}+{PLE_GIB}+{OS_GIB}={exp + PLE_GIB + OS_GIB:.0f} GiB; "
                f"MemTotal {mem_gb} GiB. Leaving --ple-io to Strata (SSD n-gram reads)."
            )
        return StrataRamPlan(family, model, chosen_ple, reason, exp, float(PLE_GIB), float(OS_GIB), mem_gb)

    # Auto: largest quality that fits experts+PLE+OS.
    for fam, mod, exp in _PLANS:
        if ple_fits(mem_gb, exp):
            reason = (
                f"RAM-resident auto: {fam}/{mod} experts {exp:.0f} GiB + PLE {PLE_GIB} GiB "
                f"+ OS {OS_GIB} GiB = {exp + PLE_GIB + OS_GIB:.0f} GiB, MemTotal {mem_gb} GiB"
            )
            return StrataRamPlan(fam, mod, "ram", reason, exp, float(PLE_GIB), float(OS_GIB), mem_gb)

    # Nothing fits with PLE in RAM. Do not force a family; Strata's setup picks
    # a size for expert-arena-only. PLE stays on SSD.
    fallback_fam = family or "qwen"
    fallback_model = model or ""
    reason = (
        f"MemTotal {mem_gb} GiB cannot hold experts + {PLE_GIB} GiB PLE + {OS_GIB} GiB OS "
        f"(Coder needs {23 + PLE_GIB + OS_GIB} GiB). Not forcing --ple-io ram."
    )
    return StrataRamPlan(
        fallback_fam,
        fallback_model,
        "direct",
        reason,
        0.0,
        float(PLE_GIB),
        float(OS_GIB),
        mem_gb,
    )


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="OpenMono Strata RAM-resident plan")
    p.add_argument("--mem-gb", type=int, default=None, help="MemTotal in GiB (default: /proc/meminfo)")
    p.add_argument("--family", default="", help="preset STRATA_FAMILY")
    p.add_argument("--model", default="", help="preset STRATA_MODEL")
    p.add_argument("--ple-io", default="", help="preset STRATA_PLE_IO")
    p.add_argument("--meminfo", default="", help="raw /proc/meminfo text (tests)")
    args = p.parse_args(argv)
    mem = args.mem_gb
    if mem is None:
        mem = memtotal_gb(args.meminfo or None)
    plan = pick_plan(
        mem,
        family=args.family or None,
        model=args.model or None,
        ple_io=args.ple_io or None,
    )
    json.dump(plan.as_dict(), sys.stdout)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
