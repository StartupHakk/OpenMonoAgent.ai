# Step: Review Scope

Present a complete summary of the incident to the user before any action is taken.
This is the gate before remediation begins.

## Summary to present

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
INCIDENT SUMMARY — REVIEW BEFORE PROCEEDING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Service      : {{params.service}}
Severity     : {{params.severity}}
Environment  : {{params.environment}}
Description  : {{params.description}}

BLAST RADIUS
{{state.blast_radius}}

ROOT CAUSE HYPOTHESIS
<your best current hypothesis based on logs and blast radius>

PROPOSED MITIGATION
<specific action you will take if the user approves — be exact>

RISK OF MITIGATION
<what could go wrong with the proposed fix>

ROLLBACK PLAN
<how to undo the mitigation if it makes things worse>
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

Ask the user to confirm they understand the scope and agree with the proposed
mitigation before the gate is passed.
