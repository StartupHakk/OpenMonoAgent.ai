#!/usr/bin/env python3
"""Agentic coding benchmark: drives the openmono ACP agent headlessly per model.

For each (model, task): switch llama-server to the model, seed a fresh workspace,
run the agent over ACP until `done`, then grade the workspace with a hidden grader
the agent never sees.

Usage:
  run_agent_bench.py --models A,B --tasks t1,t2 --out results.jsonl
  run_agent_bench.py --smoke            # 1 task, current model, no model switch
"""

import argparse
import json
import os
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
TASKS_DIR = HERE / "tasks"
IMAGE = os.environ.get("OPENMONO_IMAGE", "openmono-agent:latest")
ENDPOINT = os.environ.get("OPENMONO_ENDPOINT", "http://host.docker.internal:7474")

# Agent pauses the turn on every uncached (tool, capability) pair; each resume
# costs one extra LLM call. Cap it so a permission-thrashing model can't hang.
MAX_RESUMES = 300


def log(msg):
    print(f"[bench] {msg}", flush=True)


def free_port():
    s = socket.socket()
    s.bind(("", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def http(url, method="GET", body=None, timeout=30):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read()
    return json.loads(raw) if raw else None


def sse(url, body, timeout):
    """POST and yield (event, data) pairs from the SSE stream."""
    req = urllib.request.Request(
        url, data=json.dumps(body).encode(), method="POST",
        headers={"Content-Type": "application/json", "Accept": "text/event-stream"})
    event = None
    with urllib.request.urlopen(req, timeout=timeout) as r:
        for raw in r:
            line = raw.decode("utf-8", "replace").rstrip("\n")
            if line.startswith("event: "):
                event = line[7:].strip()
            elif line.startswith("data: "):
                try:
                    payload = json.loads(line[6:])
                except json.JSONDecodeError:
                    payload = {}
                yield event, payload
                event = None


# --------------------------------------------------------------------------- model

def model_use(name):
    log(f"switching llama-server to {name}")
    t0 = time.time()
    r = subprocess.run([str(REPO / "openmono"), "model", "use", name],
                       cwd=REPO, capture_output=True, text=True, timeout=900)
    if r.returncode != 0:
        raise RuntimeError(f"model use {name} failed: {r.stdout[-2000:]}{r.stderr[-2000:]}")
    wait_healthy()
    log(f"model ready in {time.time() - t0:.0f}s")


def wait_healthy(timeout=600):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen("http://localhost:7474/health", timeout=5) as r:
                if r.status == 200:
                    return
        except Exception:
            pass
        time.sleep(3)
    raise RuntimeError("llama-server did not become healthy")


def loaded_model():
    try:
        props = http("http://localhost:7474/props", timeout=10)
        return Path(props.get("model_path", "")).stem or "unknown"
    except Exception:
        return "unknown"


def llama_metrics():
    """Cumulative llama-server counters. Deltas across a task give the real token cost.

    The agent's own `usage` event is useless here: the ACP turn runner builds a fresh
    TokenTracker on every permission resume, so it only ever reports the last segment.
    """
    want = ("llamacpp:prompt_tokens_total", "llamacpp:tokens_predicted_total",
            "llamacpp:tokens_predicted_seconds_total")
    out = dict.fromkeys(want, 0.0)
    try:
        with urllib.request.urlopen("http://localhost:7474/metrics", timeout=10) as r:
            for line in r.read().decode().splitlines():
                name, _, val = line.partition(" ")
                if name in out:
                    out[name] = float(val)
    except Exception:
        pass
    return out


# --------------------------------------------------------------------------- run

def seed_workspace(task, ws):
    # The agent runs as root inside the container, so it leaves root-owned files
    # behind; never reuse (and never try to delete) a workspace — always a fresh dir.
    shutil.copytree(TASKS_DIR / task / "seed", ws)
    shutil.copy(TASKS_DIR / "OPENMONO.md", ws / "OPENMONO.md")
    subprocess.run(["git", "init", "-q"], cwd=ws, check=True)
    subprocess.run(["git", "add", "-A"], cwd=ws, check=True)
    subprocess.run(["git", "-c", "user.email=b@b", "-c", "user.name=bench",
                    "commit", "-qm", "seed"], cwd=ws, check=True)


def start_agent(ws, port, name):
    subprocess.run(["docker", "rm", "-f", name], capture_output=True)
    subprocess.run([
        "docker", "run", "-d", "--rm", "--name", name,
        "--add-host", "host.docker.internal:host-gateway",
        "-v", f"{ws}:/workspace",
        "-v", f"{Path.home()}/.openmono:/root/.openmono",
        "-p", f"127.0.0.1:{port}:7475",
        "-e", "OPENMONO_ACP_ONLY=1",
        "-e", f"OPENMONO_ENDPOINT={ENDPOINT}",
        "-e", f"HOST_ACP_PORT={port}",
        "-e", f"HOST_WORKSPACE_PATH={ws}",
        IMAGE,
    ], check=True, capture_output=True)

    base = f"http://127.0.0.1:{port}/api/v1"
    for _ in range(120):
        try:
            http(f"{base}/discovery", timeout=3)
            return base
        except Exception:
            time.sleep(0.5)
    raise RuntimeError("agent did not come up")


def drive(base, prompt, budget):
    """Run one turn to completion, auto-allowing every permission pause."""
    sid = http(f"{base}/sessions", "POST", {})["session_id"]
    m = dict(llm_calls=0, tool_calls=0, tool_fail=0, permissions=0, user_inputs=0,
             in_tokens=0, out_tokens=0, tools={}, failed_tools={}, resumes=0,
             status="done", assistant_chars=0)
    pending = None          # (kind, id) the agent is blocked on
    body = {"message": prompt}
    t0 = time.time()

    for _ in range(MAX_RESUMES):
        saw_done = saw_error = False
        pending = None
        left = budget - (time.time() - t0)
        if left <= 0:
            m["status"] = "timeout"
            break
        try:
            for ev, d in sse(f"{base}/sessions/{sid}/turn", body, timeout=left):
                if ev == "usage":
                    m["llm_calls"] += 1
                    m["in_tokens"] += d.get("input_tokens", 0)
                    m["out_tokens"] += d.get("output_tokens", 0)
                elif ev == "tool_start":
                    m["tool_calls"] += 1
                    n = d.get("name", "?")
                    m["tools"][n] = m["tools"].get(n, 0) + 1
                elif ev == "tool_end":
                    if not d.get("ok", True):
                        m["tool_fail"] += 1
                        n = d.get("name", "?")
                        m["failed_tools"][n] = m["failed_tools"].get(n, 0) + 1
                elif ev == "text_delta":
                    m["assistant_chars"] += len(d.get("content", ""))
                elif ev == "permission_request":
                    m["permissions"] += 1
                    pending = ("permission", d["id"])
                elif ev == "user_input_request":
                    m["user_inputs"] += 1
                    pending = ("user_input", d["id"])
                elif ev == "done":
                    saw_done = True
                elif ev == "error":
                    saw_error = True
                    m["error"] = d.get("message", "")[:300]
        except (urllib.error.URLError, socket.timeout, TimeoutError) as e:
            m["status"] = "timeout" if isinstance(e, (socket.timeout, TimeoutError)) else "stream_error"
            m.setdefault("error", str(e)[:300])
            break

        if saw_done:
            m["status"] = "done"
            break
        if saw_error and not pending:
            m["status"] = "error"
            break
        if not pending:
            m["status"] = "stalled"
            break

        kind, pid = pending
        m["resumes"] += 1
        body = ({"permission": {"id": pid, "decision": "allow"}} if kind == "permission"
                else {"user_input": {"id": pid, "value": "Proceed with your best judgement."}})
    else:
        m["status"] = "resume_cap"

    m["wall_s"] = round(time.time() - t0, 1)

    # Exact LLM round-trips: one assistant message per completion. The `usage` event
    # can't give us this (see llama_metrics).
    try:
        msgs = http(f"{base}/sessions/{sid}/messages", timeout=30)["messages"]
        m["assistant_msgs"] = sum(1 for x in msgs if x["role"] == "assistant")
    except Exception:
        m["assistant_msgs"] = 0
    return m


def grade(task, ws):
    r = subprocess.run([
        "docker", "run", "--rm",
        "-v", f"{ws}:/workspace",
        "-v", f"{TASKS_DIR / task / 'grade.py'}:/grade.py:ro",
        "-w", "/workspace", "--entrypoint", "python3", IMAGE, "/grade.py",
    ], capture_output=True, text=True, timeout=300)
    try:
        return json.loads(r.stdout.strip().splitlines()[-1])
    except Exception:
        return {"passed": False, "score": 0, "detail": f"grader crashed: {(r.stdout + r.stderr)[-300:]}"}


def run_one(model, task, bench_root, budget):
    ws = bench_root / model / task
    seed_workspace(task, ws)
    prompt = (TASKS_DIR / task / "prompt.txt").read_text().strip()
    port, name = free_port(), f"ombench_{os.getpid()}_{port_tag(task)}"
    log(f"{model} · {task} · agent on :{port}")
    before = llama_metrics()
    try:
        base = start_agent(ws, port, name)
        m = drive(base, prompt, budget)
    finally:
        subprocess.run(["docker", "rm", "-f", name], capture_output=True)
    after = llama_metrics()

    def delta(k):
        return round(after[k] - before[k], 2)

    m["prompt_tokens"] = int(delta("llamacpp:prompt_tokens_total"))
    m["gen_tokens"] = int(delta("llamacpp:tokens_predicted_total"))
    gen_s = delta("llamacpp:tokens_predicted_seconds_total")
    m["gen_tok_s"] = round(m["gen_tokens"] / gen_s, 1) if gen_s > 0 else 0

    g = grade(task, ws)
    ctx7 = sum(v for k, v in m["tools"].items() if "context7" in k.lower())
    row = dict(model=model, task=task, **m, passed=bool(g.get("passed")),
               score=g.get("score", 0), grade_detail=g.get("detail", ""),
               context7_used=ctx7, workspace=str(ws))
    log(f"{model} · {task} · {'PASS' if row['passed'] else 'FAIL'} "
        f"({m['status']}, {m['wall_s']}s, {m['assistant_msgs']} llm-calls, {m['tool_calls']} tools, "
        f"{m['tool_fail']} tool-fail, {m['permissions']} perms, {m['gen_tokens']} gen-tok)")
    return row


def port_tag(task):
    return "".join(c for c in task if c.isalnum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default="")
    ap.add_argument("--tasks", default="")
    ap.add_argument("--out", default=str(REPO / "docs" / "agent-bench-results.jsonl"))
    ap.add_argument("--workdir", default="/tmp/claude-1000/agent-bench")
    ap.add_argument("--budget", type=int, default=1200, help="per-task seconds")
    ap.add_argument("--restore", default="", help="model to switch back to at the end")
    ap.add_argument("--smoke", action="store_true", help="no model switch, current model")
    args = ap.parse_args()

    all_tasks = sorted(p.name for p in TASKS_DIR.iterdir() if (p / "prompt.txt").exists())
    tasks = [t.strip() for t in args.tasks.split(",") if t.strip()] or all_tasks
    bench_root = Path(args.workdir) / time.strftime("%Y%m%d-%H%M%S")
    bench_root.mkdir(parents=True, exist_ok=True)
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)

    models = ([loaded_model()] if args.smoke
              else [m.strip() for m in args.models.split(",") if m.strip()])
    if not models:
        sys.exit("need --models or --smoke")

    log(f"models={models} tasks={tasks} budget={args.budget}s -> {out}")
    with out.open("a") as fh:
        for model in models:
            if not args.smoke:
                model_use(model)
            for task in tasks:
                try:
                    row = run_one(model, task, bench_root, args.budget)
                except Exception as e:
                    log(f"{model} · {task} · HARNESS ERROR: {e}")
                    row = dict(model=model, task=task, status="harness_error",
                               error=str(e)[:300], passed=False, score=0)
                fh.write(json.dumps(row) + "\n")
                fh.flush()

    if args.restore:
        model_use(args.restore)
    log("done")


if __name__ == "__main__":
    main()
