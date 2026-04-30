# Step: Generate Postmortem

Write a complete postmortem document to disk and report the file path.

## Instructions

1. Determine the output path:

   ```
   postmortems/{{params.service}}-{{shell:date -u +"%Y%m%d-%H%M"}}-{{params.severity}}.md
   ```

2. Create the `postmortems/` directory if it doesn't exist:

   ```bash
   mkdir -p postmortems
   ```

3. Write the postmortem using the data collected across all steps.

## Postmortem format

```markdown
# Postmortem — {{params.service}} {{params.severity}}
**Date:** {{env.DATE}}
**Author:** {{shell:git config user.name 2>/dev/null || echo "unknown"}}
**Status:** Resolved

## Summary

<2-3 sentences: what broke, how long, who was affected, how it was fixed>

## Timeline (UTC)

| Time | Event |
|------|-------|
| <time> | Incident detected — {{params.description}} |
| <time> | Logs gathered — <key finding> |
| <time> | Blast radius confirmed — <impact category> |
| <time> | Mitigation applied — <what was done> |
| <time> | Recovery confirmed |
| <time> | Incident closed |

## Root Cause

<specific technical root cause — not "human error" but the underlying condition>

## Impact

- **Duration:** <start> to <end> (total: <X> minutes)
- **Users affected:** <count or estimate>
- **Services affected:** <list>
- **Data integrity:** <none / partial / full — describe>

## What Went Well

- <thing that helped detection or resolution>
- <thing that worked as expected>

## What Went Wrong

- <thing that caused or prolonged the incident>
- <gap in monitoring, runbook, or tooling>

## Action Items

| Priority | Action | Owner | Due |
|----------|--------|-------|-----|
| P1 | <fix the root cause permanently> | TBD | <date> |
| P2 | <improve alerting / monitoring> | TBD | <date> |
| P3 | <update runbook> | TBD | <date> |
```

4. Write the completed postmortem to the file path determined in step 1.

5. Report the absolute file path as the step output.
