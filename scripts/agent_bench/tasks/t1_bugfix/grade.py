"""Hidden grader for t1_bugfix. Runs in a container with the agent's workspace at /workspace."""
import json
import sys

sys.path.insert(0, "/workspace")

CASES = [
    # (module, func, args, expected, why)
    ("invoice", "invoice_total", ([(333, 1), (333, 1), (333, 1)], 10), 899, "reported symptom"),
    ("cart", "checkout_total", ([(333, 1), (333, 1), (333, 1)], 10), 899, "sibling caller (root cause)"),
    ("cart", "checkout_total", ([(105, 1)], 50), 53, "round half-up, not banker's"),
    ("invoice", "invoice_total", ([(999, 3)], 10), 2697, "single line"),
    ("cart", "checkout_total", ([(1000, 2)], 0), 2000, "no discount (regression)"),
    ("cart", "checkout_total", ([(200, 2), (301, 3)], 25), 977, "mixed lines"),  # 1303*.75=977.25 -> 977
]

ok, fails = 0, []
for mod, func, args, expected, why in CASES:
    try:
        m = __import__(mod)
        got = getattr(m, func)(*args)
    except Exception as e:
        fails.append(f"{func}{args}: raised {type(e).__name__}: {e} [{why}]")
        continue
    if got == expected:
        ok += 1
    else:
        fails.append(f"{func}{args} = {got}, want {expected} [{why}]")

print(json.dumps({
    "passed": ok == len(CASES),
    "score": round(ok / len(CASES), 3),
    "detail": "; ".join(fails) if fails else f"{ok}/{len(CASES)} cases",
}))
