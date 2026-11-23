# Step: Optimize loop (Spend Guardian)

Check performance and pause losers. **Never raise budgets** in this step.
Propose promotions only — human approves moves in the morning brief.

## Instructions

1. Ensure kill switch exists; dry-run it:

   ```bash
   ./scripts/pause-all.sh --dry-run
   ```

2. Pull last 3–24h metrics for live ads (spend, CPC, CTR, purchases, CAC/ROAS).
   If APIs unavailable, ask for a CSV/export path or skip with `NO_METRICS`.

3. Apply kill rules from the paused-campaign plan:
   - 0 purchases after $X spend → pause
   - CPC above $Y for 24h → pause
   - Frequency above Z → flag refresh (do not auto-raise budget)

4. Pause losers via API when connected; otherwise list IDs for manual pause.

5. Write optimize report:

   ```
   artifacts/ai-ads-agents/optimize-{{shell:date -u +%Y%m%d-%H%M}}.md
   ```

## Report template

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
SPEND GUARDIAN — {{params.product}}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Cap        : ${{params.daily-budget}}/day
Spend so far: $<amount>
Paused now  : <ids + reasons>
Still live  : <ids>
Watch list  : <ids>

PROPOSED BUDGET MOVES (do not apply yet)
- ...

ANOMALIES
- ...
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

## Output

Set `optimize_report` to the report path. If overspend or missing kill switch,
run `scripts/pause-all.sh` (for real) and halt.
