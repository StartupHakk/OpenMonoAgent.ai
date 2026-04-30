---
name: pr-ready
version: 1.0.0
description: >
  Get a branch PR-ready — sync with target, run tests, lint, stage any remaining
  changes, generate a PR description from commit history, and open the pull request.
trigger: manual
trigger-patterns:
  - "open pr *"
  - "create pr *"
  - "make pr *"
  - "pr ready"
  - "ready for review"
  - "submit pr"
  - "raise pull request"
  - "open pull request *"
user-invocable: true
argument-hint: "[--target-branch main] [--draft false] [--labels bug,enhancement]"

parameters:
  target-branch:
    type: String
    required: false
    default: "main"
    enum: [main, develop, staging, master]
    hint: "Branch to merge into"
  draft:
    type: Boolean
    required: false
    default: false
    hint: "Open as a draft PR"
  labels:
    type: String
    required: false
    hint: "Comma-separated labels to apply (e.g. bug,enhancement,breaking-change)"
  reviewers:
    type: String
    required: false
    hint: "Comma-separated GitHub usernames to request review from"
  skip-tests:
    type: Boolean
    required: false
    default: false
    hint: "Skip the test suite (use only if tests are already passing in CI)"

allowed-tools:
  - Bash
  - FileRead
  - FileWrite
  - Glob
  - Grep

context-mode: Selective
max-context-tokens: 5000
depends-on:
  - commit

tags:
  - git
  - github
  - pr
  - workflow

constraints:
  inline:
    - "Never open a PR if tests are failing — the validate-tests step must pass."
    - "Never open a PR from main, master, or the target branch itself."
    - "Never include secrets, .env files, or credentials in the PR."
    - "Always base the PR description on actual commit messages — do not invent features."
    - "If the branch has no commits ahead of target, abort and tell the user."
    - "Never force-push during this playbook."

steps:
  - id: sync-check
    file: steps/01-sync-check.md
    gate: None
    output: sync_status
    script: scripts/sync-check.sh

  - id: run-tests
    requires: [sync-check]
    file: steps/02-run-tests.md
    gate: None
    output: test_results
    script: scripts/run-tests.sh

  - id: lint
    requires: [run-tests]
    file: steps/03-lint.md
    gate: None
    output: lint_results

  - id: stage-remaining
    requires: [lint]
    inline-prompt: >
      Check for any unstaged or uncommitted changes:
        git status --short

      If there are uncommitted changes, run the commit playbook to stage and
      commit them before the PR is opened:
        Playbook commit

      If the working tree is already clean, report "Working tree clean — nothing to commit"
      and proceed.
    gate: None
    output: commit_status
    playbook: commit

  - id: generate-description
    requires: [stage-remaining]
    file: steps/04-generate-description.md
    gate: Review
    output: pr_description

  - id: open-pr
    requires: [generate-description]
    file: steps/05-open-pr.md
    gate: Approve
    output: pr_url
---

You are a pull request assistant. Your job is to ensure the branch is clean,
tests pass, and the PR description accurately reflects the work done.

Speak in direct, concise prose. Surface blockers immediately — don't proceed
past a failing step with a workaround.

## Your responsibilities

1. **Sync check** — verify the branch is up to date with the target. If it's
   behind, advise the user to rebase or merge before continuing.

2. **Run tests** — detect and run the project's test suite. Hard stop if any fail.

3. **Lint** — run linting and static analysis. Report failures; warn on warnings.

4. **Stage remaining** — commit any leftover changes using the commit playbook
   so the PR contains a clean, complete commit history.

5. **Generate description** — inspect `git log {{params.target-branch}}..HEAD`
   and write a PR description: what changed, why, and how to test it.
   Present it for the user to review and edit before submitting.

6. **Open PR** — run `gh pr create` with the approved description, labels,
   reviewers, and draft flag. Return the PR URL.
