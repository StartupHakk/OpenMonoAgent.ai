# Step: Build paused campaigns

Create campaign structure for **approved** creatives only.
**All campaigns must be PAUSED.** Never go live in this step.

## Instructions

1. Read `{{state.approved_creatives}}`.

2. Plan (write before touching any API):
   - Campaign name: `OM-{{params.product}}-{{params.channel}}-{{shell:date -u +%Y%m%d}}`
   - Daily budget: **${{params.daily-budget}}** hard cap
   - Ad sets: 1–2 test sets (broad + one interest/lookalike if available)
   - Ads: only approved creatives (max 5 live later; build up to 10 paused)
   - Landing: {{params.landing-url}}
   - Kill rules (document):
     - 0 purchases after $X spend → pause
     - CPC above $Y for 24h → pause
     - Frequency above Z → refresh

3. If ad APIs are not connected, write a **campaign plan JSON/Markdown** that a
   human (or later script) can apply — still mark every entity `status: PAUSED`.

4. If APIs are connected:
   - Create campaign / ad sets / ads as **PAUSED**
   - Confirm status via API read-back
   - Never call unpause / enable

5. Save:

   ```
   artifacts/ai-ads-agents/paused-campaigns-{{shell:date -u +%Y%m%d}}.md
   ```

## Output block

```
PAUSED CAMPAIGNS BUILT
──────────────────────
Campaign : <name>
Channel  : {{params.channel}}
Budget   : ${{params.daily-budget}}/day
Status   : PAUSED (all)
Ads      : <count>
Plan     : <file path>
Dry-run  : {{params.dry-run}}
```

## Output

Set `paused_campaigns` to the plan path + IDs. Do not spend.
