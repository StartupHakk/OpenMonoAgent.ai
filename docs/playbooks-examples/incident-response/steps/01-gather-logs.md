# Step: Gather Logs

Record the UTC start time: `{{shell:date -u +"%Y-%m-%dT%H:%M:%SZ"}}`

Your goal is to build a factual picture of what is failing and since when.

## Instructions

1. Check recent application logs for errors:

   ```bash
   # Try common log locations
   tail -n 200 /var/log/{{params.service}}/error.log 2>/dev/null || \
   journalctl -u {{params.service}} --since "1 hour ago" --no-pager 2>/dev/null || \
   docker logs {{params.service}} --tail 200 2>/dev/null || \
   echo "No log source found — check manually"
   ```

2. Check for recent deployments that may have caused the issue:

   ```bash
   git log --oneline --since="2 hours ago" 2>/dev/null | head -20
   ```

3. Check system resource pressure:

   ```bash
   # Memory, CPU, disk
   free -h 2>/dev/null; df -h / 2>/dev/null; uptime 2>/dev/null
   ```

4. If a runbook was provided, read it for context:
   `{{file:{{params.runbook-path}}}}`

## Output

Produce a structured log summary:

```
Incident start    : {{params.description}}
Service           : {{params.service}}
Severity          : {{params.severity}}
Environment       : {{params.environment}}
Gathered at (UTC) : <timestamp>

── Error snapshot ───────────────────────────────
<first 10 unique error messages / stack traces>

── Recent deployments ───────────────────────────
<last 3 deploys with timestamp and author>

── System resources ─────────────────────────────
<memory / CPU / disk summary>

── Initial hypothesis ───────────────────────────
<1-2 sentences on most likely cause based on evidence>
```

Do not take any remediation action in this step.
