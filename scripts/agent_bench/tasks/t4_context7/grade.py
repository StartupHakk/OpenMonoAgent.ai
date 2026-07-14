"""Hidden grader for t4_context7: does the ported config.py run on pydantic v2 with behaviour intact?"""
import json
import re
import sys

sys.path.insert(0, "/workspace")

checks, fails = [], []
try:
    from pydantic import ValidationError

    import config
    checks.append(True)
except Exception as e:
    print(json.dumps({"passed": False, "score": 0,
                      "detail": f"config.py still does not import: {type(e).__name__}: {str(e)[:160]}"}))
    raise SystemExit

VALID = [
    ({"host": "api.example.com", "port": 8443, "tls": True},
     {"host": "api.example.com", "port": 8443, "tls": True}),
    ({"host": "api.example.com", "port": 8080},
     {"host": "api.example.com", "port": 8080, "tls": False}),
]
INVALID = [
    ({"host": "api.example.com", "port": 80}, "privileged port"),
    ({"host": "BAD_HOST!", "port": 8080}, "host pattern"),
    ({"host": "api.example.com", "port": 2000, "tls": True}, "tls needs port >= 8443"),
]

for data, want in VALID:
    try:
        got = config.load(dict(data))
    except Exception as e:
        checks.append(False)
        fails.append(f"load({data}) raised {type(e).__name__}: {str(e)[:80]}")
        continue
    ok = isinstance(got, dict) and all(got.get(k) == v for k, v in want.items())
    checks.append(ok)
    if not ok:
        fails.append(f"load({data}) = {got!r}, want {want!r}")

for data, why in INVALID:
    try:
        got = config.load(dict(data))
        checks.append(False)
        fails.append(f"load({data}) = {got!r}, want ValidationError [{why}]")
    except ValidationError:
        checks.append(True)
    except Exception as e:
        checks.append(False)
        fails.append(f"load({data}) raised {type(e).__name__}, want ValidationError [{why}]")

# no v1 leftovers
src = open("/workspace/config.py", encoding="utf-8").read()
leftovers = [p for p in (r"\bregex\s*=", r"@validator", r"@root_validator",
                         r"\.parse_obj\(", r"\.dict\(\)")
             if re.search(p, src)]
clean = not leftovers
checks.append(clean)
if not clean:
    fails.append("v1 API left in config.py: " + ", ".join(leftovers))

behaviour_ok = all(checks[1:len(VALID) + len(INVALID) + 1])
print(json.dumps({
    "passed": behaviour_ok and clean,
    "score": round(sum(checks) / len(checks), 3),
    "detail": "; ".join(fails) if fails else f"{sum(checks)}/{len(checks)} checks",
}))
