# Step: Human approve launch

Confirm whether paused campaigns may go live. Respect **dry-run**.

## Summary to present

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
LAUNCH GATE — MONEY CAN MOVE AFTER THIS
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Product     : {{params.product}}
Channel     : {{params.channel}}
Daily cap   : ${{params.daily-budget}}  ← HARD CAP
Dry-run     : {{params.dry-run}}

Paused plan : {{state.paused_campaigns}}

WHAT GOES LIVE IF YOU CONFIRM
- Campaigns / ad sets / ads listed in the plan
- Test budget only — never above daily cap

IF DRY-RUN IS TRUE
- Do NOT unpause. Record decision as "approved for later live"
- Tell the user how to re-run with --dry-run false

KILL SWITCH
- scripts/pause-all.sh must be available before any unpause
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

Ask the user to Confirm one of:
1. **Stay paused** (recommended first run)
2. **Go live** (only if `dry-run` is false)
3. **Abort**

## Rules

- If `{{params.dry-run}}` is true → never unpause, even if user says go live;
  instruct them to re-run with `--dry-run false`.
- If go-live confirmed and dry-run false → unpause only the listed IDs, then
  immediately verify status and remaining budget caps.
- On any API error → run pause-all and stop.

## Output

Set `launch_decision` to `paused` | `live` | `aborted` with notes and IDs.
