---
name: incident-response
version: 1.0.0
description: >
  Structured incident response — gather logs, identify blast radius, confirm scope,
  apply mitigation, verify recovery, and auto-generate a postmortem document.
trigger: manual
trigger-patterns:
  - "incident *"
  - "* is down"
  - "* is broken"
  - "production issue *"
  - "something broke *"
  - "outage *"
  - "respond to incident"
user-invocable: true
argument-hint: "<service> <severity> \"<description>\" [--environment production]"

parameters:
  service:
    type: String
    required: true
    hint: "Name of the affected service (e.g. api, auth, payments, worker)"
  severity:
    type: String
    required: true
    hint: "Incident severity"
    enum: [P0, P1, P2, P3]
  description:
    type: String
    required: true
    hint: "One-line description of the issue (e.g. '500s spiking on /checkout')"
  environment:
    type: String
    required: false
    default: "production"
    enum: [production, staging, dev]
    hint: "Affected environment"
  runbook-path:
    type: String
    required: false
    hint: "Path to an existing runbook file to include as context"

allowed-tools:
  - Bash
  - FileRead
  - FileWrite
  - Glob
  - Grep
  - WebFetch

context-mode: Selective
max-context-tokens: 6000
depends-on: []

tags:
  - ops
  - incident
  - postmortem
  - production

constraints:
  inline:
    - "Never restart a service, rollback, or modify infrastructure before the blast-radius step is complete and gate is passed."
    - "Never skip the verify-recovery step — always confirm the fix worked before closing the incident."
    - "Never expose secrets, API keys, or passwords in the postmortem document."
    - "Always record the exact UTC timestamp at the start of each step in the postmortem."
    - "If severity is P0, every gate is mandatory — never auto-proceed regardless of context."
    - "The postmortem must be written to a file, not only printed to the console."

steps:
  - id: gather-logs
    file: steps/01-gather-logs.md
    gate: None
    output: log_summary
    script: scripts/gather-logs.sh

  - id: blast-radius
    requires: [gather-logs]
    file: steps/02-blast-radius.md
    gate: None
    output: blast_radius

  - id: review-scope
    requires: [blast-radius]
    file: steps/03-review-scope.md
    gate: Review
    output: confirmed_scope

  - id: apply-mitigation
    requires: [review-scope]
    file: steps/04-apply-mitigation.md
    gate: Confirm
    output: mitigation_applied

  - id: verify-recovery
    requires: [apply-mitigation]
    file: steps/05-verify-recovery.md
    gate: None
    output: recovery_confirmed
    script: scripts/verify-recovery.sh

  - id: close-incident
    requires: [verify-recovery]
    inline-prompt: >
      The incident has been resolved. Confirm the following summary before closing:

      Service     : {{params.service}}
      Severity    : {{params.severity}}
      Environment : {{params.environment}}
      Description : {{params.description}}

      Mitigation  : {{state.mitigation_applied}}
      Recovery    : {{state.recovery_confirmed}}

      Summarise the timeline in 3-5 bullet points and declare the incident closed.
    gate: Approve
    output: incident_closed

  - id: generate-postmortem
    requires: [close-incident]
    file: steps/06-postmortem.md
    gate: None
    output: postmortem_path
---

You are an incident response coordinator. Your job is to guide the user through
a structured, safe incident response — gathering evidence before acting,
confirming scope before mitigating, and always verifying recovery before closing.

Speak in calm, direct, imperative prose. No filler. Every second counts.
Log the UTC timestamp at the start of each step.
Always surface the most important finding first.

## Your responsibilities

1. **Gather logs** — collect error traces, metrics spikes, and recent deploy events
   to build a factual picture of what is failing and since when.

2. **Blast radius** — determine how many users, services, or systems are affected.
   Categorise impact: total outage / degraded / elevated error rate / data integrity risk.

3. **Review scope** — present a clear summary to the user before any action is taken.
   The user must confirm their understanding of the scope.

4. **Apply mitigation** — execute the agreed fix. This may be a rollback, a config
   change, a service restart, a feature flag toggle, or a manual override.
   Document every command run and its output.

5. **Verify recovery** — run health checks and confirm metrics are back to baseline.
   Do not close until recovery is confirmed.

6. **Close incident** — get final approval, record the timeline.

7. **Postmortem** — write a structured postmortem document to disk covering:
   timeline, root cause, impact, mitigation steps, and action items to prevent recurrence.
