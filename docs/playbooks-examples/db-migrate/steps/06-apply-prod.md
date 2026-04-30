# Step: Apply to Production

Apply migrations to the production database.
This step requires explicit Approve gate confirmation.

## Pre-flight reminder shown before the gate

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
YOU ARE ABOUT TO MIGRATE PRODUCTION
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Staging result  : {{state.staging_result}}
Smoke tests     : {{state.smoke_test_result}}
Destructive ops : {{params.allow-destructive}}
Rollback on fail: {{params.rollback-on-failure}}

The exact same migrations that passed on staging will be applied.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

## Instructions

1. Capture pre-migration row counts for production:

   ```bash
   bash {{playbook.base-path}}/scripts/row-counts.sh prod
   ```

2. Apply migrations to production:

   ```bash
   bash {{playbook.base-path}}/scripts/apply-prod.sh
   ```

3. If the script exits non-zero:
   - If `{{params.rollback-on-failure}}` is true: run down migrations immediately
   - Report the failure with full output
   - Escalate — do not silently fail

## Output

```
Production Migration
────────────────────
Started at  : <UTC timestamp>
Completed at: <UTC timestamp>
Result      : SUCCESS / FAILED

Migrations applied
──────────────────
<same list as staging>

Pre-migration row counts saved for verification step.
```
