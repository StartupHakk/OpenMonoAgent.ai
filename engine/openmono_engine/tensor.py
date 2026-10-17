"""Tiny CPU tensor helpers (stdlib only, no numpy).

Used by tests and the synthetic forward path. Not a production kernel.
"""

from __future__ import annotations

import math
from array import array


def zeros(n: int) -> array:
    return array("f", [0.0]) * n


def from_list(xs: list[float]) -> array:
    return array("f", (float(x) for x in xs))


def add(a: array, b: array) -> array:
    if len(a) != len(b):
        raise ValueError(f"add length mismatch {len(a)} vs {len(b)}")
    out = zeros(len(a))
    for i in range(len(a)):
        out[i] = a[i] + b[i]
    return out


def scale(a: array, s: float) -> array:
    out = zeros(len(a))
    for i in range(len(a)):
        out[i] = a[i] * s
    return out


def silu(x: float) -> float:
    return x / (1.0 + math.exp(-x))


def softmax(xs: list[float]) -> list[float]:
    if not xs:
        return []
    m = max(xs)
    exps = [math.exp(x - m) for x in xs]
    s = sum(exps)
    if s == 0.0:
        return [1.0 / len(xs)] * len(xs)
    return [e / s for e in exps]


def rmsnorm(x: array, eps: float = 1e-6) -> array:
    acc = 0.0
    for v in x:
        acc += v * v
    rms = math.sqrt(acc / len(x) + eps)
    out = zeros(len(x))
    inv = 1.0 / rms
    for i in range(len(x)):
        out[i] = x[i] * inv
    return out


def matvec(matrix: array, vec: array, rows: int, cols: int) -> array:
    """matrix is row-major rows*cols; vec has length cols."""
    if len(matrix) != rows * cols:
        raise ValueError("matrix size mismatch")
    if len(vec) != cols:
        raise ValueError("vec size mismatch")
    out = zeros(rows)
    for r in range(rows):
        acc = 0.0
        base = r * cols
        for c in range(cols):
            acc += matrix[base + c] * vec[c]
        out[r] = acc
    return out


def topk_indices(scores: list[float], k: int) -> list[int]:
    if k <= 0:
        return []
    order = sorted(range(len(scores)), key=lambda i: scores[i], reverse=True)
    return order[: min(k, len(scores))]
