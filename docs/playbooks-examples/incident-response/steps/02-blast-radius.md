# Step: Blast Radius Assessment

Record the UTC timestamp: `{{shell:date -u +"%Y-%m-%dT%H:%M:%SZ"}}`

Using the log summary from the previous step, determine how wide the impact is.

## Instructions

1. Estimate affected user count (use whatever signals are available):

   ```bash
   # Active connections / sessions
   ss -s 2>/dev/null || netstat -an 2>/dev/null | grep ESTABLISHED | wc -l
   ```

2. Check dependent services that call {{params.service}}:

   ```bash
   grep -r "{{params.service}}" . --include="*.env*" --include="*.yaml" \
     --include="*.yml" --include="*.json" -l 2>/dev/null | head -10
   ```

3. Determine impact category:
   - **Total outage** — service completely unavailable
   - **Degraded** — service running but errors elevated (e.g. >1% error rate)
   - **Partial** — subset of endpoints or users affected
   - **Data integrity risk** — data may be corrupted or lost

4. Check if there is an existing health endpoint:

   ```bash
   curl -sf http://localhost/health 2>/dev/null || \
   curl -sf http://localhost:8080/health 2>/dev/null || \
   echo "No health endpoint found"
   ```

## Output

```
Blast Radius Assessment
───────────────────────
Impact category   : <Total outage | Degraded | Partial | Data integrity risk>
Estimated users   : <count or "unknown">
Dependent services: <list of services that depend on {{params.service}}>
Data at risk      : <yes/no — describe if yes>
Health endpoint   : <URL and current status or "none">

Affected scope
──────────────
<2-4 bullet points describing exactly what is broken>

Not affected
────────────
<1-3 bullet points of what is confirmed still working>

Recommended action
──────────────────
<one clear sentence — rollback / restart / config change / escalate>
```

Do not take any action. This step is assessment only.
