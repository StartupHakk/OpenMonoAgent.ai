# Playbooks

Playbooks are structured, multi-step automation workflows that run inside OpenMono.ai. They let you encode repeatable engineering processes — git commits, releases, code reviews, file scans — as versioned, composable procedures that the AI executes step by step, pausing for your approval at critical points.

---

## Table of Contents

- [Overview](#overview)
- [Directory Layout](#directory-layout)
- [PLAYBOOK.md Format](#playbookmd-format)
  - [Identity](#identity)
  - [Invocation](#invocation)
  - [Parameters](#parameters)
  - [Steps](#steps)
  - [Constraints](#constraints)
  - [Allowed Tools](#allowed-tools)
  - [Context Mode](#context-mode)
- [Template Variables](#template-variables)
- [Gates](#gates)
- [State and Checkpointing](#state-and-checkpointing)
- [Playbook Discovery](#playbook-discovery)
- [Built-in Playbooks](#built-in-playbooks)
  - [commit](#commit)
  - [release](#release)
  - [file-scan](#file-scan)
- [Writing Your Own Playbook](#writing-your-own-playbook)
- [PLAYBOOK.md Reference](#playbookmd-reference)

---

## Overview

A Playbook is a directory containing a `PLAYBOOK.md` file. The file uses YAML frontmatter to declare metadata, parameters, and a step list, followed by a markdown body that becomes the AI's system prompt for the duration of the playbook.

Key properties:

- **Multi-step** — work is broken into discrete, ordered steps. The AI completes one step fully before moving to the next.
- **Typed parameters** — inputs are declared with types, constraints, defaults, and enums. Invalid input is rejected before execution starts.
- **Gates** — steps can pause and require human review, confirmation, or approval before proceeding.
- **State** — each step can store a named output that later steps reference as a template variable.
- **Checkpointed** — after every step the state is written to disk. If execution is interrupted, it can be resumed from the last completed step.
- **Versioned** — playbooks carry a semver version string and can be updated or rolled back like any other file.

---

## Directory Layout

```
my-playbook/
├── PLAYBOOK.md          # Required — main definition
├── steps/               # Optional — external step instruction files
│   ├── 01-analyze.md
│   └── 02-implement.md
├── scripts/             # Optional — shell scripts invoked by steps
│   ├── pre-flight.sh
│   └── validate.sh
├── constraints/         # Optional — constraint files merged at runtime
│   └── rules.md
└── templates/           # Optional — output format templates
    └── summary.md
```

The only required file is `PLAYBOOK.md`. Everything else is referenced from it.

---

## PLAYBOOK.md Format

A `PLAYBOOK.md` file has two sections separated by the YAML front-matter delimiters (`---`):

```
---
<YAML frontmatter — metadata, parameters, steps>
---

<Markdown body — becomes the AI's system prompt>
```

### Identity

```yaml
name: my-playbook       # Unique identifier — used to invoke the playbook
version: 1.0.0          # SemVer string
description: >          # One-paragraph summary shown in /playbooks list
  What this playbook does and when to use it.
tags:
  - git
  - workflow
```

### Invocation

Controls how and when the playbook is triggered.

```yaml
trigger: manual          # manual | auto | both
trigger-patterns:        # Glob patterns matched against the user's input
  - "commit *"
  - "commit my changes"
user-invocable: true     # Show in the slash-command list
argument-hint: "[--scope <scope>] [--message <msg>]"
```

| `trigger` value | Behaviour |
|-----------------|-----------|
| `manual` | Only runs when explicitly invoked (e.g. `/release patch`) |
| `auto` | Runs automatically when the user's message matches a `trigger-patterns` entry |
| `both` | Can be auto-triggered or manually invoked |

When multiple playbooks match the same input, the one with the most specific (longest) matching pattern wins. If confidence is below the threshold, the playbook is suggested but not auto-run.

### Parameters

Typed, validated inputs passed at invocation time.

```yaml
parameters:
  version-type:
    type: String          # String | Number | Boolean | Array
    required: true
    hint: "Semver bump type: major, minor, or patch"
    enum: [major, minor, patch]
  tag-prefix:
    type: String
    required: false
    default: "v"
    hint: "Git tag prefix (default: v)"
  coverage:
    type: Number
    required: false
    default: 80
    min: 0
    max: 100
  dry-run:
    type: Boolean
    required: false
    default: false
```

All parameters are validated before execution begins. Required parameters with no default cause an immediate error. Enum, min, and max constraints are enforced.

Parameters are referenced in steps and scripts via `{{parameters.<name>}}`.

### Steps

The ordered list of work units. Steps run in dependency order; if no `requires` is declared they run sequentially in list order.

```yaml
steps:
  - id: pre-flight              # Unique identifier within the playbook
    inline-prompt: >            # Prompt given to the AI for this step
      Run the pre-flight check and report the output.
    script: scripts/pre-flight.sh  # Shell script executed before/during the step
    gate: None                  # None | Confirm | Review | Approve

  - id: analyze-changes
    requires: [pre-flight]      # Must complete before this step runs
    file: steps/01-analyze.md   # Load step instructions from an external file
    gate: None

  - id: bump-version
    requires: [analyze-changes]
    file: steps/03-version.md
    gate: Confirm               # Pause and ask user to confirm before writing
    output: new_version         # Store the AI's output under this state key
```

**Step content** — choose one of:

| Field | Description |
|-------|-------------|
| `inline-prompt` | The instructions are written directly in the YAML value |
| `file` | Path (relative to the playbook directory) to a markdown file containing the instructions |

Both support template variables.

**Step options:**

| Field | Description |
|-------|-------------|
| `id` | Required. Unique step name used in `requires` lists and state references |
| `requires` | Array of step IDs that must complete successfully before this step starts |
| `gate` | Human checkpoint. See [Gates](#gates) |
| `output` | State key name. The AI's final response for this step is stored at `{{state.<output>}}` |
| `script` | Path to a shell script. Executed alongside the step; its stdout is embedded in the prompt via `{{shell:...}}` |

### Constraints

Safety rules injected into every step's context, not just the preamble.

```yaml
constraints:
  file: constraints/rules.md   # Path to a markdown file of constraints
  inline:
    - "Never force-push or rebase the main branch."
    - "Never create a release from a dirty working tree."
    - "Never skip the validate-tests step."
```

Inline constraints and file constraints are merged. The combined list is available in step templates as `{{constraints}}`.

### Allowed Tools

```yaml
allowed-tools:
  - "*"                  # All tools (default)
  # Or a specific subset:
  - Shell
  - ReadFile
  - WriteFile
  - Glob
  - Search
```

### Context Mode

```yaml
context-mode: Selective   # Full | Selective | Fork
max-context-tokens: 8000  # Hard cap on playbook context injected per turn
```

| Mode | Behaviour |
|------|-----------|
| `Full` | The entire PLAYBOOK.md body is always in context |
| `Selective` | Only the current step's instructions, active constraints, and summarized prior-step state are injected. Significantly more token-efficient for long playbooks |
| `Fork` | Each step runs in an isolated context with no prior conversation history |

---

## Template Variables

Templates variables are resolved in both `inline-prompt` values and external step files before the prompt is sent to the AI.

| Variable | Resolves to |
|----------|-------------|
| `{{parameters.<name>}}` | The value of a declared parameter |
| `{{state.<key>}}` | The stored output of a previous step |
| `{{constraints}}` | The rendered constraints block |
| `{{playbook.base-path}}` | Absolute path to the playbook's directory |
| `{{env.CWD}}` | Current working directory |
| `{{env.GIT_BRANCH}}` | Active git branch |
| `{{env.DATE}}` | Today's date in ISO 8601 format |
| `{{file:path/to/file}}` | Contents of a file (path relative to playbook directory) |
| `{{shell:command}}` | Stdout of a shell command executed at template-resolution time |

Example — embedding live shell output in a step prompt:

```yaml
- id: create-files
  inline-prompt: |
    The setup script produced this output:

    {{shell:bash {{playbook.base-path}}/scripts/create-files.sh}}

    Report exactly which files were created.
```

---

## Gates

Gates pause execution and require a human response before the next step runs.

| Gate | Behaviour |
|------|-----------|
| `None` | Auto-proceed (default) |
| `Confirm` | Show a summary of what will happen next, prompt `[y/N]` |
| `Review` | Show the step's output, allow the user to edit it before proceeding |
| `Approve` | Show a full preview of proposed changes, require explicit approval |

The AI is expected to present a clear summary of the action before the gate fires. Gate responses are logged in the playbook state.

---

## State and Checkpointing

After each step completes, the playbook state is written to disk:

```
~/.openmono/playbook-state/<name>_<sessionId>.json
```

The state file contains:

```jsonc
{
  "PlaybookName": "release",
  "SessionId": "abc123",
  "Parameters": { "version-type": "minor" },
  "StepOutputs": {
    "analyze-changes": "Last tag: v1.0.0, 12 commits ...",
    "new_version": "1.1.0"
  },
  "CompletedSteps": ["pre-flight", "analyze-changes", "generate-changelog"],
  "TokensUsed": 4800
}
```

To resume an interrupted playbook:

```
/release --resume
```

The executor reloads the saved state, skips already-completed steps, and continues from the first incomplete step.

---

## Playbook Discovery

The runtime searches for playbooks in the following locations, in priority order. A playbook in a higher-priority location shadows one with the same name lower down.

| Priority | Path | Scope |
|----------|------|-------|
| 1 (highest) | Compiled into binary | Always available |
| 2 | `~/.openmono/playbooks/<name>/PLAYBOOK.md` | All projects for this user |
| 3 | `.openmono/playbooks/<name>/PLAYBOOK.md` | This project only |
| 4 (lowest) | `<added-dir>/.openmono/playbooks/` | Added workspace directories |

---

## Built-in Playbooks

Three sample playbooks ship with the project under `.openmono/playbooks/`.

---

### commit

**Path:** `.openmono/playbooks/commit/PLAYBOOK.md`

Inspects staged (or unstaged) changes, generates a conventional commit message, and runs `git commit`.

**Trigger:** `auto` — fires on patterns like `"commit *"` and `"commit my changes"`.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scope` | String | No | Conventional commit scope (e.g. `auth`, `ui`). Inferred automatically if omitted |
| `message` | String | No | Override the generated commit message subject line |

**Steps:** Single step — no `steps:` block is declared, so the entire body is the instruction.

**What it does:**

1. Runs `git status` and `git diff --staged` to understand what is staged. If nothing is staged, inspects unstaged changes and stages relevant files.
2. Writes a conventional commit message (`type(scope): subject`, body only when the *why* isn't obvious).
3. Runs `git commit -m "..."` (never `--no-verify`).
4. Reports the resulting commit hash and one-line summary.

**Constraints:**
- Never commits files that look like secrets (`.env`, `*.pem`, `credentials.*`).
- If the working tree is clean, stops without creating an empty commit.
- Prefers a single commit unless the user asks to split.

**Usage:**

```
/commit
/commit --scope auth
/commit --message "fix: resolve token expiry edge case"
```

---

### release

**Path:** `.openmono/playbooks/release/PLAYBOOK.md`

End-to-end release pipeline covering environment checks, changelog generation, version bumping, test validation, git tagging, and optional Docker push.

**Trigger:** `manual`

**Parameters:**

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `version-type` | String | Yes | — | `major`, `minor`, or `patch` |
| `tag-prefix` | String | No | `v` | Prefix for the git tag (e.g. `v1.2.3`) |
| `dry-run` | Boolean | No | `false` | Run all checks and generate artifacts but skip tagging, pushing, and publishing |
| `push-docker` | Boolean | No | `false` | Build and push Docker images after tagging |

**Steps:**

| Step | Gate | Description |
|------|------|-------------|
| `pre-flight` | None | Runs `scripts/pre-flight.sh` — verifies Docker, .NET SDK, clean git state, no secrets |
| `analyze-changes` | None | Reads `steps/01-analyze.md` — `git log` since last tag, classifies commits by semver impact |
| `generate-changelog` | **Review** | Reads `steps/02-changelog.md` — writes a Keep a Changelog entry to `CHANGELOG.md` |
| `bump-version` | **Confirm** | Reads `steps/03-version.md` — updates `<Version>` in all `.csproj` and `Directory.Build.props`; stores `new_version` in state |
| `validate-tests` | None | Runs `dotnet test`; aborts immediately if any test fails |
| `tag-and-push` | **Approve** | Runs `scripts/tag-and-push.sh` — creates annotated git tag, optionally builds and pushes Docker images; respects `dry-run` |

**Step detail — analyze-changes (`steps/01-analyze.md`):**

Finds the last tag (`git describe --tags --abbrev=0`), lists all commits since then, classifies each by conventional commit type, identifies the highest semver impact (breaking change → major, feat → minor, fix/perf → patch), and outputs a structured report.

**Step detail — generate-changelog (`steps/02-changelog.md`):**

Writes a [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) entry with sections for Added, Changed, Fixed, Deprecated, Removed, and Security (empty sections omitted). Entries are written as user-facing sentences, not raw commit subjects. A `### Breaking Changes` section is prepended if breaking changes were detected. The gate is `Review`, so the user can inspect and edit the entry before it is written to disk.

**Step detail — bump-version (`steps/03-version.md`):**

Reads the current version from `Directory.Build.props` or the first `.csproj` found. Applies the `version-type` bump. Updates `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, and `<InformationalVersion>` in every `.csproj` and in `Directory.Build.props`. Stages the changed files. Stores the new version string in `state.new_version` for the `tag-and-push` step.

**Constraints:**
- Never force-push, rebase, or delete main/master.
- Never release from a dirty working tree.
- Never skip `validate-tests`; abort immediately on any test failure.
- Never push to registry or create a public tag when `dry-run` is `true`.
- Never commit secrets or `.env` files as part of the release.
- Always use the Approve gate before pushing the git tag or Docker images.

**Usage:**

```
/release minor
/release patch --dry-run true
/release major --push-docker true --tag-prefix release/
```

---

### file-scan

**Path:** `.openmono/playbooks/file-scan/PLAYBOOK.md`

Creates workspace files and then greps the codebase. A simple two-step example demonstrating shell integration and step dependencies.

**Trigger:** `manual`

**Parameters:** None.

**Steps:**

| Step | Gate | Description |
|------|------|-------------|
| `create-files` | None | Runs `scripts/create-files.sh` via `{{shell:...}}` and reports which files were created |
| `grep-scan` | None | Runs `scripts/scan.sh` via `{{shell:...}}` and summarises the matches found |

The `grep-scan` step requires `create-files` to complete first. Both steps embed live shell output directly in their prompts using the `{{shell:bash ...}}` template variable.

**Usage:**

```
/file-scan
```

---

## Writing Your Own Playbook

1. **Create the directory** inside `.openmono/playbooks/` (project scope) or `~/.openmono/playbooks/` (user scope):

   ```
   .openmono/playbooks/my-playbook/
   ```

2. **Create `PLAYBOOK.md`** with the required fields:

   ```markdown
   ---
   name: my-playbook
   version: 1.0.0
   description: One-sentence summary of what this playbook does.
   trigger: manual
   user-invocable: true
   ---

   You are an assistant that ... (system prompt / role description)
   ```

3. **Add parameters** if the playbook needs user input:

   ```yaml
   parameters:
     target:
       type: String
       required: true
       hint: "File or module to process"
   ```

4. **Define steps** for multi-stage work:

   ```yaml
   steps:
     - id: analyze
       file: steps/01-analyze.md
       gate: None
       output: analysis

     - id: apply
       requires: [analyze]
       inline-prompt: |
         Based on the analysis: {{state.analysis}}
         Apply the changes to {{parameters.target}}.
       gate: Confirm
   ```

5. **Add scripts** for environment checks or post-step validation in `scripts/`.

6. **Add constraints** to guard against LLM improvisation:

   ```yaml
   constraints:
     inline:
       - "Never modify files outside {{parameters.target}}."
       - "Never create new files unless explicitly requested."
   ```

7. **Invoke it:**

   ```
   /my-playbook --target src/Auth.cs
   ```

---

## PLAYBOOK.md Reference

Full list of frontmatter fields.

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `name` | string | Yes | — | Unique identifier. Used to invoke via `/name` |
| `version` | string | No | `1.0.0` | SemVer version |
| `description` | string | Yes | — | Short summary shown in discovery |
| `trigger` | enum | No | `manual` | `manual`, `auto`, or `both` |
| `trigger-patterns` | string[] | No | `[]` | Glob patterns for auto-triggering |
| `user-invocable` | boolean | No | `true` | Appear in slash-command list |
| `argument-hint` | string | No | — | Usage hint shown in help |
| `parameters` | map | No | `{}` | Typed parameter declarations |
| `steps` | list | No | `[]` | Ordered step definitions |
| `constraints.file` | string | No | — | Path to a constraint markdown file |
| `constraints.inline` | string[] | No | `[]` | Inline constraint strings |
| `allowed-tools` | string[] | No | `["*"]` | Tool access list |
| `context-mode` | enum | No | `Selective` | `Full`, `Selective`, or `Fork` |
| `max-context-tokens` | number | No | `3000` | Token cap for injected context per turn |
| `depends-on` | string[] | No | `[]` | Names of other playbooks this one can invoke |
| `tags` | string[] | No | `[]` | Free-form labels for discovery and filtering |

**Parameter fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | enum | Yes | `String`, `Number`, `Boolean`, or `Array` |
| `required` | boolean | No | Whether the parameter must be supplied |
| `default` | any | No | Value used when the parameter is omitted |
| `hint` | string | No | Help text shown to the user |
| `enum` | string[] | No | Allowed values; others are rejected |
| `min` | number | No | Minimum value (Number type only) |
| `max` | number | No | Maximum value (Number type only) |

**Step fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Unique step identifier |
| `inline-prompt` | string | No* | Step instructions written inline |
| `file` | string | No* | Path to external step markdown file |
| `requires` | string[] | No | Step IDs that must complete first |
| `gate` | enum | No | `None`, `Confirm`, `Review`, or `Approve` |
| `output` | string | No | State key to store the step's result |
| `script` | string | No | Shell script path; stdout embedded in prompt |

\* At least one of `inline-prompt` or `file` is required per step.
