# Step: Verify Row Counts

Compare row counts before and after the production migration.
Flag any unexpected changes and write a migration report to disk.

## Instructions

1. Capture post-migration row counts:

   ```bash
   bash {{playbook.base-path}}/scripts/row-counts.sh prod
   ```

2. Compare with pre-migration counts captured in `apply-prod` step.

3. Flag as unexpected any table where:
   - Row count dropped and the migration did not include a DELETE/DROP/TRUNCATE
   - Row count is zero and it wasn't before
   - Row count changed by more than expected for a data migration

4. Write the migration report:

   ```bash
   mkdir -p migration-reports
   # Write report to migration-reports/YYYYMMDD-HHMMSS.md
   ```

## Report format

```markdown
# Migration Report — {{env.DATE}}

## Summary
- Migration path : {{params.migration-path}}
- Environment   : production
- Result        : SUCCESS

## Row Count Verification

| Table    | Before  | After   | Delta   | Expected |
|----------|---------|---------|---------|----------|
| users    | 182,491 | 182,491 | 0       | ✔        |
| sessions | 941,203 | 0       | -941,203| ✔ (DROP TABLE) |
| orders   | 58,102  | 58,102  | 0       | ✔        |

## Anomalies
<none — or list unexpected changes>

## Applied Migrations
<list of migration files with timestamps>
```

5. Report the path of the written file as the step output.
