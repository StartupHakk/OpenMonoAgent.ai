# Step: Verify Recovery

Record the UTC timestamp: `{{shell:date -u +"%Y-%m-%dT%H:%M:%SZ"}}`

Confirm the service has recovered before the incident is declared resolved.

## Instructions

1. Run the health check script:

   ```bash
   bash {{playbook.base-path}}/scripts/verify-recovery.sh
   ```

2. Check error logs for the period since mitigation:

   ```bash
   # Errors in the last 5 minutes
   journalctl -u {{params.service}} --since "5 minutes ago" --no-pager 2>/dev/null | \
     grep -i "error\|exception\|fatal" | tail -20 || \
   docker logs {{params.service}} --since 5m 2>/dev/null | \
     grep -i "error\|exception\|fatal" | tail -20 || \
   echo "No log source found"
   ```

3. Confirm the original symptom is gone:

   ```bash
   curl -sf http://localhost/health 2>/dev/null || echo "Health check: no endpoint"
   ```

## Recovery criteria

Mark recovery as confirmed ONLY if ALL of the following are true:
- Health endpoint returns 200 (or no health endpoint exists and service is running)
- Error rate has returned to baseline (no new errors of the same type)
- The original symptom described in the incident is no longer reproducible

If recovery is NOT confirmed, report what is still failing and recommend next steps.

## Output

```
Recovery Verification
─────────────────────
Verified at  : <UTC timestamp>
Status       : RECOVERED / NOT RECOVERED

Health check : <pass/fail>
Error rate   : <baseline / elevated — describe>
Symptom gone : <yes/no>

Evidence
────────
<2-4 lines of concrete evidence that recovery is confirmed>
```
