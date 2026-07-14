# Project Instructions

## Build
- Language: Python 3 (stdlib only unless a dependency is already installed)
- Tests: `python3 -m unittest discover -v`

## Conventions
- Primary language: python
- Keep modules small; no frameworks

## Git
- Default branch: main

## Do NOT
- Modify files in vendor/ or node_modules/
- Commit .env or credentials files
- Add dependencies without justification

## Minimalism (Ponytail ladder)
Before writing any code, climb this ladder and stop at the first rung that holds:
1. Does this need to exist at all? If not: skip it (YAGNI).
2. Already in this codebase? Reuse it, don't rewrite it.
3. Stdlib covers it? Use stdlib.
4. Native platform feature? Use it.
5. An already-installed dependency does it? Use that.
6. One-line solution? Write one line.
7. Only then: the minimum code that works.
No unrequested abstractions, helpers, or refactorings. No interface with a single implementation. Fix root causes, not symptoms. Never skip input validation, error handling, or security.

## Output style (Caveman)
Keep prose terse: short fragments, drop filler words, don't restate the question. Never shorten or alter code, commands, file paths, or error messages.
