"""Hidden grader for t5_cli_feature: drive the CLI end-to-end in a scratch DB."""
import json
import os
import subprocess
import sys
import tempfile

db = os.path.join(tempfile.mkdtemp(), "todo.json")
env = dict(os.environ, TODO_DB=db)


def run(*args):
    return subprocess.run([sys.executable, "main.py", *args], cwd="/workspace",
                          env=env, capture_output=True, text=True, timeout=60)


checks, fails = [], []


def expect(args, want, why):
    r = run(*args)
    got = r.stdout.strip()
    ok = r.returncode == 0 and got == want
    checks.append(ok)
    if not ok:
        fails.append(f"`main.py {' '.join(args)}` -> rc={r.returncode} {got!r} "
                     f"{r.stderr.strip()[-80:]}, want {want!r} [{why}]")


expect(("stats",), "total=0 open=0 done=0", "empty db")
expect(("add", "buy milk"), "added 1", "add still works")
expect(("add", "write report"), "added 2", "add still works")
expect(("stats",), "total=2 open=2 done=0", "two open")
expect(("done", "1"), "done 1", "done still works")
expect(("stats",), "total=2 open=1 done=1", "one done")
expect(("list",), "[x] 1 buy milk\n[ ] 2 write report", "list format unchanged")

print(json.dumps({
    "passed": all(checks),
    "score": round(sum(checks) / len(checks), 3),
    "detail": "; ".join(fails) if fails else f"{sum(checks)}/{len(checks)} checks",
}))
