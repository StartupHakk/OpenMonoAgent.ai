#!/usr/bin/env python3
"""Collect recent screen-skills frames/notes into a distill output directory."""

from __future__ import annotations

import argparse
import shutil
import time
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sessions", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--minutes", type=int, default=30)
    parser.add_argument("--sample-every", type=int, default=2)
    args = parser.parse_args()

    sessions = args.sessions
    out = args.out
    cutoff = time.time() - args.minutes * 60
    sample_every = max(1, args.sample_every)
    frames_dir = out / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)

    candidates: list[Path] = []
    if sessions.is_dir():
        for p in sessions.rglob("*.png"):
            try:
                if p.stat().st_mtime >= cutoff:
                    candidates.append(p)
            except OSError:
                continue
    candidates.sort(key=lambda p: p.stat().st_mtime)

    if not candidates and sessions.is_dir():
        session_dirs = sorted([d for d in sessions.iterdir() if d.is_dir()], key=lambda d: d.name)
        if session_dirs:
            candidates = sorted((session_dirs[-1] / "frames").glob("*.png"), key=lambda p: p.name)

    selected: list[Path] = []
    n = len(candidates)
    for i, src in enumerate(candidates, start=1):
        if i == 1 or i == n or (i % sample_every == 0):
            selected.append(src)

    for src in selected:
        dest = frames_dir / src.name
        if dest.exists():
            dest = frames_dir / f"{src.parent.parent.name}_{src.name}"
        shutil.copy2(src, dest)

    notes_path = out / "notes_combined.md"
    chunks: list[str] = []
    if sessions.is_dir():
        for notes in sessions.rglob("notes.md"):
            try:
                if notes.stat().st_mtime < cutoff:
                    continue
            except OSError:
                continue
            body = notes.read_text(encoding="utf-8", errors="replace").strip()
            if body:
                chunks.append(f"## {notes.parent.name}\n{body}\n")
    notes_path.write_text("\n".join(chunks) if chunks else "_none_\n", encoding="utf-8")

    index = out / "frame_index.txt"
    lines: list[str] = []
    for img in sorted(frames_dir.glob("*.png")):
        st = img.stat()
        mtime = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(st.st_mtime))
        lines.append(f"{st.st_size:8d}  {mtime}  {img.name}")
    index.write_text("\n".join(lines) + ("\n" if lines else "no frames\n"), encoding="utf-8")
    print(len(selected))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
