# Step: Review Schema Diff

Present the full schema diff to the user before any real database is touched.
Highlight destructive operations prominently.

## Summary to present

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
SCHEMA DIFF — REVIEW BEFORE APPLYING
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Migration path   : {{params.migration-path}}
Target           : {{params.target}}
Allow destructive: {{params.allow-destructive}}
Dry-run only     : {{params.dry-run}}

DRY RUN RESULTS
{{state.dry_run_report}}

⚠  DESTRUCTIVE OPERATIONS DETECTED  ⚠
(only shown if any were found)
────────────────────────────────────────
<list each destructive operation with the file and line number>
<explain what data will be permanently lost>

WHAT WILL HAPPEN IF YOU APPROVE
────────────────────────────────
1. Migrations applied to STAGING first
2. Smoke tests run against staging
3. If staging passes → you will be asked to approve PROD
4. Migrations applied to PROD
5. Row counts verified

WHAT CANNOT BE UNDONE
──────────────────────
<list any irreversible changes — dropped columns, deleted data>

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

The user must explicitly approve this diff before any migration is applied to staging.
