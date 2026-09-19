#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."

echo "== decision calibration replay =="
echo ">> build"
dotnet build public/OpenMono.sln --nologo -v q 2>&1 | tail -3

echo ">> frozen goldens + gate matrix + sweep"
dotnet test public/src/OpenMono.Tests/ \
  --filter "FullyQualifiedName~Decision|FullyQualifiedName~JudgeGate|FullyQualifiedName~SpecialistToolTests" \
  --nologo -v q 2>&1 | tail -3

echo ""
echo "frozen golden table (see docs/decision-calibration.md):"
echo "  no sources      -> research @ 0.90"
echo "  evidence        -> write @ 0.625 (review at default 0.85, write at 0.6)"
echo "  unclear/done    -> review @ 0.90"
echo "  oversized state -> review, capped"
echo "  git status / ls -> allow (fast path, zero backend calls)"
echo "  git reset --hard -> confirm | rm -rf / -> block | secret+egress -> block"
