"""Hidden grader for t3_refactor: behaviour must be byte-identical, duplication must be gone."""
import json
import subprocess
import sys

sys.path.insert(0, "/workspace")

ROWS = [
    {"name": "widget", "price": 9.5, "in_stock": True, "note": None},
    {"name": "bolt, hex", "price": 0.25, "in_stock": False, "note": "a|b\tc"},
    {"name": "x", "price": 3, "in_stock": None, "note": "plain"},
    {"name": "gap"},  # missing keys -> empty cells
]
COLUMNS = ["name", "price", "in_stock", "note"]


def reference(rows, columns, sep):
    """Verbatim semantics of the seed code."""
    out = sep.join(c.upper() for c in columns) + "\n"
    for row in rows:
        cells = []
        for col in columns:
            v = row.get(col)
            if v is None:
                cells.append("")
            elif isinstance(v, bool):
                cells.append("yes" if v else "no")
            elif isinstance(v, float):
                cells.append("%.2f" % v)
            else:
                s = str(v)
                if sep in s:
                    s = '"' + s + '"'
                cells.append(s)
        out += sep.join(cells) + "\n"
    return out


checks, fails = [], []
try:
    import report
except Exception as e:
    print(json.dumps({"passed": False, "score": 0, "detail": f"cannot import report: {e}"}))
    raise SystemExit

for func, sep in [("to_csv", ","), ("to_tsv", "\t"), ("to_psv", "|")]:
    want = reference(ROWS, COLUMNS, sep)
    try:
        got = getattr(report, func)(ROWS, COLUMNS)
    except Exception as e:
        checks.append(False)
        fails.append(f"{func} raised {type(e).__name__}: {e}")
        continue
    checks.append(got == want)
    if got != want:
        fails.append(f"{func} output differs: got {got!r} want {want!r}")

# callers must still work
for fmt in ("csv", "tsv", "psv"):
    r = subprocess.run([sys.executable, "export_cli.py", fmt], cwd="/workspace",
                       capture_output=True, text=True, timeout=60)
    checks.append(r.returncode == 0)
    if r.returncode != 0:
        fails.append(f"export_cli.py {fmt} failed: {r.stderr.strip()[-100:]}")

# duplication actually removed? seed report.py is 64 code lines with 3 copies of the loop.
code = [ln for ln in open("/workspace/report.py", encoding="utf-8").read().splitlines()
        if ln.strip() and not ln.strip().startswith("#")]
deduped = len(code) <= 32
checks.append(deduped)
if not deduped:
    fails.append(f"still duplicated: report.py has {len(code)} code lines (want <= 32)")

behaviour_ok = all(checks[:6])
print(json.dumps({
    "passed": behaviour_ok and deduped,
    "score": round(sum(checks) / len(checks), 3),
    "detail": "; ".join(fails) if fails else f"{sum(checks)}/{len(checks)} checks, report.py {len(code)} code lines",
}))
