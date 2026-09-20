#!/usr/bin/env bash
# Phase 3: measure, don't just run. Evaluates the active decision backend
# over the hand-labeled corpus (src/OpenMono.Tests/Decisions/
# decision-corpus.json), prints Brier/ECE, and writes
# docs/decision-calibration.md. The heuristic backend is the miscalibrated
# control — expect poor numbers; that is the baseline to beat.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== decision calibration =="
echo ">> build"
dotnet build OpenMono.sln --nologo -v q 2>&1 | tail -3

echo ">> corpus calibration (measures Brier/ECE, writes docs/decision-calibration.md)"
dotnet test src/OpenMono.Tests/ \
  --filter "FullyQualifiedName~DecisionCalibration" \
  --nologo -v q 2>&1 | tail -3

echo ">> decision suites (goldens + gate matrix + sweep, no-build)"
dotnet test src/OpenMono.Tests/ \
  --filter "FullyQualifiedName~Decision|FullyQualifiedName~JudgeGate|FullyQualifiedName~SpecialistToolTests" \
  --no-build --nologo -v q 2>&1 | tail -3

echo ""
echo ">> calibration result"
grep -E "^- (Backend|Corpus|Brier|ECE)" docs/decision-calibration.md
echo ""
echo "full reliability table: docs/decision-calibration.md"
