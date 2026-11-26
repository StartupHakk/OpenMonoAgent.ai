# Screen → Skill Distillation Prompt

You are distilling the last ~30 minutes of local screen capture into Cursor Agent Skills.

## Inputs

Read these files from the distill bundle directory provided by the user (or `@`-mention them):

1. `bundle.md` — session metadata, frame index, optional OCR/notes (already secret-scanned)
2. Sampled keyframe images under `frames/` (if attached)
3. `decision_log.md` — append your accept/reject decisions here

Also read the skill authoring rules in `~/.cursor/skills-cursor/create-skill/SKILL.md` and the decision rubric in `~/.cursor/skills/screen-workflow-capture/SKILL.md`.

## Hard rules

1. **Never** put secrets, API keys, passwords, tokens, private keys, connection strings, or personal account numbers into a skill.
2. If the bundle still contains secret-like material, redact it and refuse to encode that material.
3. Prefer **personal** skills at `~/.cursor/skills/<name>/SKILL.md` unless the workflow is clearly project-specific for the open repo — then use `.cursor/skills/<name>/`.
4. Do **not** write under `~/.cursor/skills-cursor/` (Cursor built-ins).
5. Default new skills to `disable-model-invocation: true` unless the user clearly wants auto-invocation.
6. Keep each `SKILL.md` under 500 lines and concise.

## Decision rubric (apply to every candidate)

Promote to a skill **only if most YES signals are true and no hard NO applies**:

### YES — promote when

- Reusable multi-step workflow the user (or agent) will likely repeat
- Stable project/tool conventions not obvious from generic knowledge
- A fragile sequence where exact commands/order matter
- Domain checklist that improves consistency (deploy, review, setup)
- Clear trigger phrases the agent can match later

### NO — do not promote when

- One-off exploration, browsing, or entertainment
- Pure UI clicking with no transferable procedure
- Content dominated by secrets, PII, or private chats
- Already covered well by an existing skill (update that skill instead, or skip)
- Transient state (window layout, unread counts, timestamps)
- Noise / idle desktop / login screens only
- Speculative “might be useful someday” with no evidence of repetition

When unsure: write a **candidate draft** under `~/Library/Application Support/screen-skills/candidates/<name>/` and mark status `needs-review` — do not install until the user confirms.

## Output format

For each candidate, write:

```markdown
## Candidate: <skill-name>

- Decision: ACCEPT | REJECT | NEEDS-REVIEW
- Why: <1-3 sentences tied to the rubric>
- Triggers: <when the agent should use it>
- Destination: ~/.cursor/skills/<name>/ OR .cursor/skills/<name>/ OR candidates/
```

Then:

- **ACCEPT**: create the skill directory + `SKILL.md` (and optional `scripts/`) following create-skill format.
- **REJECT**: do not create files; only log the reason.
- **NEEDS-REVIEW**: write draft under Application Support `candidates/` only.

Append a short summary to `decision_log.md`.

## Quality bar for ACCEPT

A good distilled skill includes:

1. Specific third-person `description` with WHAT + WHEN
2. Step-by-step instructions grounded in observed workflow
3. Exact commands/paths when they matter
4. Explicit “do not” notes for pitfalls seen on screen
5. No secrets, no unnecessary narrative
