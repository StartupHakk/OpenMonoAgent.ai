# Step: Lint and Static Analysis

Run available linting and static analysis tools. Warn on issues; do not hard-stop
unless there are errors (as opposed to warnings).

## Instructions

1. Detect available linters:

   ```bash
   command -v eslint 2>/dev/null && echo "eslint"
   command -v dotnet 2>/dev/null && echo "dotnet-format"
   command -v ruff 2>/dev/null && echo "ruff"
   command -v golangci-lint 2>/dev/null && echo "golangci-lint"
   ```

2. Run what's available:

   ```bash
   # ESLint
   npx eslint . --max-warnings=0 2>&1 | tail -20

   # .NET format check
   dotnet format --verify-no-changes --verbosity diagnostic 2>&1 | tail -20

   # Ruff (Python)
   ruff check . 2>&1 | tail -20

   # golangci-lint
   golangci-lint run ./... 2>&1 | tail -20
   ```

3. Run only the tools that are installed — skip silently if a tool is absent.

## Output

```
Linters run     : <list>

Results
───────
eslint     : <pass / X warnings / X errors>
dotnet-fmt : <pass / needs formatting>
ruff       : <pass / X issues>

Issues (errors only)
────────────────────
<file>:<line> — <message>
```

Hard stop only if there are formatting errors that would fail CI.
Warnings are reported but do not block the PR.
