# Step: Smoke Test Against Staging

Verify the application works correctly against the migrated staging database.
Do not proceed to production if smoke tests fail.

## Instructions

1. Run the smoke test script:

   ```bash
   bash {{playbook.base-path}}/scripts/smoke-test.sh staging
   ```

2. If no smoke test script is found, run basic health checks:

   ```bash
   # Application health
   curl -sf "$APP_URL_STAGING/health" 2>/dev/null || \
   curl -sf "http://localhost:8080/health" 2>/dev/null || \
   echo "No health endpoint — check manually"
   ```

3. Verify queries that touch the migrated tables return expected results:
   - No 500 errors from the application
   - Key endpoints respond within normal latency
   - Data reads on modified tables succeed

## Output

```
Smoke Tests — Staging
──────────────────────
Health endpoint : <pass / fail / not found>
Critical queries: <pass / fail>
Error rate      : <0% / elevated — describe>

Test results
────────────
✔  Health check             200 OK (142ms)
✔  User query on users      returned 10,482 rows
✔  Auth flow                login succeeds
✘  Session lookup           500 — table 'old_sessions' not found

Verdict: PASS / FAIL
```

Hard stop if any smoke test fails. Do not proceed to production.
