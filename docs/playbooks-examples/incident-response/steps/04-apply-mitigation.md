# Step: Apply Mitigation

Record the UTC timestamp: `{{shell:date -u +"%Y-%m-%dT%H:%M:%SZ"}}`

Execute the mitigation agreed in the review-scope gate.
Log every command and its exit code. Do not skip steps.

## Instructions

1. Before making any change, snapshot the current state:

   ```bash
   # Current process state
   ps aux | grep {{params.service}} 2>/dev/null | head -5
   # Current config (if applicable)
   env | grep -i {{params.service}} | grep -v PASSWORD | grep -v SECRET 2>/dev/null
   ```

2. Apply the mitigation. This may include:
   - Rolling back to a previous deployment: `git revert`, `docker pull <prev-tag>`
   - Restarting the service: `systemctl restart {{params.service}}`
   - Toggling a feature flag
   - Applying a config patch
   - Scaling up/down instances

   Execute what was agreed in the previous step. Log every command.

3. Confirm the change took effect:

   ```bash
   # Re-check the service state
   ps aux | grep {{params.service}} 2>/dev/null | head -5
   ```

## Output

```
Mitigation Applied
──────────────────
Started at  : <UTC timestamp>
Completed at: <UTC timestamp>

Commands run
────────────
$ <command 1>
  exit: <code>  output: <first line>

$ <command 2>
  exit: <code>  output: <first line>

Result
──────
<What changed. What is now different from before.>
```
