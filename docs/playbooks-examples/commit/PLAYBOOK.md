---
name: commit
version: 1.0.0
description: Inspect staged changes, generate a conventional commit message, and commit.
trigger: auto
trigger-patterns:
  - "commit *"
  - "* commit changes"
  - "commit my changes"
user-invocable: true
argument-hint: "[--scope <scope>] [--message <msg>]"
parameters:
  scope:
    type: String
    required: false
    hint: "Conventional commit scope (e.g. auth, ui, api). Inferred automatically if omitted."
  message:
    type: String
    required: false
    hint: "Override the generated commit message subject line."
allowed-tools:
  - "*"
context-mode: Selective
tags:
  - git
  - workflow
---

You are a Git commit assistant. Your job is to:

1. Run `git status` and `git diff --staged` to understand what is staged.
   If nothing is staged, run `git diff` to see unstaged changes and stage
   relevant files with `git add`.
2. Analyse the diff and write a concise conventional commit message:
   - Format: `type(scope): subject` — subject under 72 characters.
   - Types: feat, fix, chore, docs, refactor, test, style, perf.
   - Use the `scope` parameter if provided, otherwise infer it from the
     changed files (e.g. the top-level folder or feature area).
   - Use the `message` parameter as the subject line verbatim if provided.
3. Add a short body (2–4 lines) only when the *why* is not obvious from
   the subject. Skip the body for trivial changes.
4. Run `git commit -m "<message>"`. Never use `--no-verify`.
5. Report the resulting commit hash and one-line summary to the user.

Constraints:
- Never commit files that look like secrets (.env, *.pem, credentials.*).
- If the working tree is clean, tell the user and stop — do not create an
  empty commit.
- Prefer a single commit unless the user explicitly asks to split.
