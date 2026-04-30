# Step: Run Tests

Detect the project's test framework and run the full test suite.
Hard stop if any tests fail — do not proceed to lint or PR.

## Instructions

1. Detect test framework:

   ```bash
   # .NET
   ls *.sln **/*.csproj 2>/dev/null | head -1
   # Node
   cat package.json 2>/dev/null | grep -E '"test"' | head -1
   # Python
   ls pytest.ini setup.cfg pyproject.toml 2>/dev/null | head -1
   # Go
   ls go.mod 2>/dev/null
   ```

2. Run tests based on what was detected:

   ```bash
   # .NET
   dotnet test --no-build --verbosity minimal 2>&1

   # Node (npm)
   npm test --if-present 2>&1

   # Python
   python -m pytest --tb=short -q 2>&1

   # Go
   go test ./... 2>&1
   ```

   If no test framework is detected, report "No test suite found" and continue.

3. Parse the output: extract pass count, fail count, and any failing test names.

## Output

```
Test Framework  : <dotnet | npm | pytest | go | none>
Tests passed    : <count>
Tests failed    : <count>
Duration        : <seconds>

Failures
────────
<failing test name>
  <error message — first 3 lines>
```

If any test fails, output: "BLOCKED — fix failing tests before opening a PR."
