# Step: Morning brief

One page: sold, spent, where to move budget. **Approve** before reallocating.

## Instructions

1. Run:

   ```bash
   ./scripts/morning-brief.sh "{{params.product}}" "{{params.channel}}" "{{params.daily-budget}}"
   ```

2. Merge `{{state.optimize_report}}`, store/CRM sales if available, and ad spend.

3. Present:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
MORNING ADS BRIEF — {{params.product}}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Channel   : {{params.channel}}
Daily cap : ${{params.daily-budget}}

YESTERDAY / LAST 24h
Spend     : $...
Sales     : $... / N orders
CAC       : $...
ROAS      : ...

WINNERS
- ...

PAUSED / KILLED
- ...

PROPOSED MOVES (need your Approve)
1. Shift $A from <loser> → <winner>
2. Refresh creative on <ad>
3. Keep testing <angle>

AI tested. You decide.
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

4. Save brief to:

   ```
   artifacts/ai-ads-agents/brief-{{shell:date -u +%Y%m%d}}.md
   ```

5. Gate **Approve**:
   - Apply proposed moves (only within daily cap), or
   - Edit proposals, or
   - No changes

## Output

Set `brief_path` to the brief file. If Approve includes moves, document each
change and confirm new statuses. Never exceed `{{params.daily-budget}}`.
