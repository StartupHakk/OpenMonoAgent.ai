#!/usr/bin/env python3
"""HumanEval-subset quality bench against a llama-server OpenAI endpoint.

Scores pass@1 (greedy, temperature 0) on a fixed task subset by actually
executing the generated code against the reference tests.

Usage:
  quality_bench.py --tasks HumanEval.jsonl --model-label NAME \
      [--ids HumanEval/0,...] [--endpoint URL] [--max-tokens N] [--out results.csv]
"""

import argparse
import json
import re
import subprocess
import sys
import time
import urllib.request

DEFAULT_IDS = [
    "HumanEval/0",    # has_close_elements (easy)
    "HumanEval/26",   # remove_duplicates (medium)
    "HumanEval/31",   # is_prime (easy-medium)
    "HumanEval/36",   # fizz_buzz digit count (medium)
    "HumanEval/39",   # prime_fib (hard)
    "HumanEval/74",   # total_match (medium)
    "HumanEval/121",  # solution: odd elements at even positions (medium)
    "HumanEval/142",  # sum_squares with index rules (medium)
]

PRELUDE = (
    "from typing import List, Tuple, Dict, Optional, Any\n"
    "import math\n\n"
)

SYSTEM = (
    "You are a precise Python programmer. Reply with ONLY one ```python code "
    "block containing the complete, self-contained implementation of the "
    "requested function (including its signature and any imports it needs). "
    "No tests, no usage examples, no explanations."
)


def chat(endpoint: str, prompt: str, max_tokens: int, timeout: int):
    body = json.dumps({
        "messages": [
            {"role": "system", "content": SYSTEM},
            {"role": "user", "content": "Complete this Python function:\n\n```python\n" + prompt + "\n```"},
        ],
        "temperature": 0,
        "top_p": 1,
        "max_tokens": max_tokens,
    }).encode()
    req = urllib.request.Request(
        endpoint.rstrip("/") + "/v1/chat/completions",
        data=body, headers={"Content-Type": "application/json"})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read())
    dt = time.time() - t0
    choice = data["choices"][0]
    usage = data.get("usage", {})
    return choice["message"]["content"] or "", choice.get("finish_reason"), usage.get("completion_tokens", 0), dt


def strip_think(text: str) -> str:
    if "</think>" in text:
        return text.rsplit("</think>", 1)[1]
    if text.lstrip().startswith("<think>"):
        return ""  # truncated inside thinking
    return text


def extract_code(text: str) -> str:
    blocks = re.findall(r"```(?:python|py)?\s*\n(.*?)```", text, re.DOTALL)
    for block in reversed(blocks):
        if "def " in block:
            return block
    if blocks:
        return blocks[-1]
    return text if "def " in text else ""


def run_candidate(code: str, task: dict) -> tuple[bool, str]:
    program = PRELUDE + code + "\n\n" + task["test"] + f"\ncheck({task['entry_point']})\n"
    try:
        proc = subprocess.run(
            [sys.executable, "-"], input=program, capture_output=True,
            text=True, timeout=20)
    except subprocess.TimeoutExpired:
        return False, "timeout"
    if proc.returncode == 0:
        return True, "pass"
    return False, (proc.stderr.strip().splitlines() or ["fail"])[-1][:120]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tasks", required=True)
    ap.add_argument("--model-label", required=True)
    ap.add_argument("--ids", default=",".join(DEFAULT_IDS))
    ap.add_argument("--endpoint", default="http://localhost:7474")
    ap.add_argument("--max-tokens", type=int, default=640)
    ap.add_argument("--request-timeout", type=int, default=1200)
    ap.add_argument("--out", default="")
    args = ap.parse_args()

    wanted = args.ids.split(",")
    tasks = {}
    with open(args.tasks) as fh:
        for line in fh:
            t = json.loads(line)
            if t["task_id"] in wanted:
                tasks[t["task_id"]] = t

    passed = 0
    truncated = 0
    total_tokens = 0
    total_time = 0.0
    details = []
    for tid in wanted:
        task = tasks[tid]
        try:
            text, finish, ntok, dt = chat(args.endpoint, task["prompt"],
                                          args.max_tokens, args.request_timeout)
        except Exception as exc:  # server error / timeout
            details.append({"task": tid, "result": f"request_error: {exc}"})
            continue
        total_tokens += ntok
        total_time += dt
        if finish == "length":
            truncated += 1
        code = extract_code(strip_think(text))
        if not code:
            details.append({"task": tid, "result": "no_code", "finish": finish})
            continue
        ok, msg = run_candidate(code, task)
        passed += ok
        details.append({"task": tid, "result": msg, "finish": finish,
                        "tokens": ntok, "seconds": round(dt, 1)})
        print(f"  {tid}: {'PASS' if ok else 'FAIL'} ({msg}, {ntok} tok, {dt:.0f}s)",
              flush=True)

    total = len(wanted)
    rate = round(100.0 * passed / total, 1)
    tps = round(total_tokens / total_time, 1) if total_time else 0
    row = f"{args.model_label};{passed};{total};{rate};{truncated};{tps};{round(total_time, 1)}"
    print(row, flush=True)
    if args.out:
        with open(args.out, "a") as fh:
            fh.write(row + "\n")
        with open(args.out.replace(".csv", ".details.jsonl"), "a") as fh:
            fh.write(json.dumps({"model": args.model_label, "details": details}) + "\n")


if __name__ == "__main__":
    main()
