#!/usr/bin/env python3
"""Scan text (and optional image OCR dumps) for secrets before skill distillation."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("aws_access_key", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("aws_secret_key", re.compile(r"(?i)aws(.{0,20})?(secret|access).{0,20}['\"][0-9a-zA-Z/+]{40}['\"]")),
    ("github_pat", re.compile(r"\bghp_[A-Za-z0-9]{36,}\b")),
    ("github_fine_grained", re.compile(r"\bgithub_pat_[A-Za-z0-9_]{20,}\b")),
    ("gitlab_pat", re.compile(r"\bglpat-[A-Za-z0-9\-_]{20,}\b")),
    ("slack_token", re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")),
    ("openai_key", re.compile(r"\bsk-[A-Za-z0-9]{20,}\b")),
    ("anthropic_key", re.compile(r"\bsk-ant-[A-Za-z0-9\-_]{20,}\b")),
    ("google_api_key", re.compile(r"\bAIza[0-9A-Za-z\-_]{35}\b")),
    ("private_key_block", re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")),
    ("jwt", re.compile(r"\beyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")),
    ("generic_bearer", re.compile(r"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*\b")),
    ("connection_string", re.compile(r"(?i)\b(?:postgres|mysql|mongodb(?:\+srv)?)://[^\s'\"]+\b")),
    ("env_assignment", re.compile(r"(?i)\b(?:API_KEY|SECRET|TOKEN|PASSWORD|PRIVATE_KEY)\s*=\s*\S+")),
]

REDACT = "[REDACTED]"


def scan_text(text: str) -> tuple[str, list[str]]:
    findings: list[str] = []
    cleaned = text
    for name, pattern in PATTERNS:
        matches = list(pattern.finditer(cleaned))
        if matches:
            findings.append(f"{name}: {len(matches)} match(es)")
            cleaned = pattern.sub(REDACT, cleaned)
    return cleaned, findings


def main() -> int:
    parser = argparse.ArgumentParser(description="Scan and redact likely secrets from text files.")
    parser.add_argument("paths", nargs="+", help="Files to scan")
    parser.add_argument("--write", action="store_true", help="Overwrite files with redacted content")
    parser.add_argument("--report", type=Path, help="Write findings report to this path")
    args = parser.parse_args()

    all_findings: list[str] = []
    for raw in args.paths:
        path = Path(raw)
        if not path.is_file():
            print(f"skip missing: {path}", file=sys.stderr)
            continue
        original = path.read_text(encoding="utf-8", errors="replace")
        cleaned, findings = scan_text(original)
        if findings:
            for item in findings:
                all_findings.append(f"{path}: {item}")
            if args.write:
                path.write_text(cleaned, encoding="utf-8")
                print(f"redacted: {path}")
            else:
                print(f"findings in {path}: {', '.join(findings)}")
        else:
            print(f"clean: {path}")

    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        body = "\n".join(all_findings) if all_findings else "No secret-like patterns found.\n"
        args.report.write_text(body + ("\n" if not body.endswith("\n") else ""), encoding="utf-8")

    return 1 if all_findings else 0


if __name__ == "__main__":
    raise SystemExit(main())
