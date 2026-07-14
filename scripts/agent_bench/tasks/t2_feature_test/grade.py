"""Hidden grader for t2_feature_test."""
import glob
import json
import subprocess
import sys

sys.path.insert(0, "/workspace")

VALID = [("90s", 90), ("1h30m", 5400), ("2d", 172800), ("1h 30m", 5400),
         ("3m20s", 200), ("1d2h3m4s", 93784), ("0s", 0), ("45m", 2700)]
INVALID = ["", "abc", "5", "1x", "-5m", "1.5h"]

checks, fails = [], []
try:
    from durations import parse_duration
except Exception as e:
    print(json.dumps({"passed": False, "score": 0,
                      "detail": f"cannot import durations.parse_duration: {e}"}))
    raise SystemExit

for text, want in VALID:
    try:
        got = parse_duration(text)
    except Exception as e:
        checks.append(False)
        fails.append(f"parse_duration({text!r}) raised {type(e).__name__}")
        continue
    checks.append(got == want)
    if got != want:
        fails.append(f"parse_duration({text!r}) = {got!r}, want {want}")

for text in INVALID:
    try:
        got = parse_duration(text)
        checks.append(False)
        fails.append(f"parse_duration({text!r}) = {got!r}, want ValueError")
    except ValueError:
        checks.append(True)
    except Exception as e:
        checks.append(False)
        fails.append(f"parse_duration({text!r}) raised {type(e).__name__}, want ValueError")

# The task also asks for tests. Did the agent write any, and does the suite pass?
wrote_tests = any("parse_duration" in open(p, encoding="utf-8", errors="replace").read()
                  for p in glob.glob("/workspace/test*.py") + glob.glob("/workspace/tests/test*.py"))
checks.append(wrote_tests)
if not wrote_tests:
    fails.append("no unit tests for parse_duration")

suite = subprocess.run([sys.executable, "-m", "unittest", "discover", "-v"],
                       cwd="/workspace", capture_output=True, text=True, timeout=120)
suite_ok = suite.returncode == 0
checks.append(suite_ok)
if not suite_ok:
    fails.append("`unittest discover` fails: " + suite.stderr.strip().splitlines()[-1][:120])

behaviour_ok = all(checks[:len(VALID) + len(INVALID)])
print(json.dumps({
    "passed": behaviour_ok and wrote_tests and suite_ok,
    "score": round(sum(checks) / len(checks), 3),
    "detail": "; ".join(fails) if fails else f"{sum(checks)}/{len(checks)} checks",
}))
