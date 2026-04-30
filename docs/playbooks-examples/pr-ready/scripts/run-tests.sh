#!/usr/bin/env bash
# pr-ready/scripts/run-tests.sh
# Detects and runs the project's test suite. Exit 1 if any tests fail.
set -euo pipefail

SKIP_TESTS="${SKIP_TESTS:-false}"

if [ "$SKIP_TESTS" = "true" ]; then
  echo "Tests skipped (--skip-tests true)"
  exit 0
fi

# .NET
if find . -maxdepth 3 -name "*.sln" 2>/dev/null | grep -q .; then
  echo "Detected: .NET"
  dotnet test --no-build --verbosity minimal
  exit $?
fi

# Node
if [ -f "package.json" ] && grep -q '"test"' package.json 2>/dev/null; then
  echo "Detected: Node.js"
  npm test
  exit $?
fi

# Python
if command -v pytest &>/dev/null; then
  echo "Detected: Python/pytest"
  python -m pytest --tb=short -q
  exit $?
fi

# Go
if [ -f "go.mod" ]; then
  echo "Detected: Go"
  go test ./...
  exit $?
fi

echo "No test framework detected — skipping tests."
exit 0
