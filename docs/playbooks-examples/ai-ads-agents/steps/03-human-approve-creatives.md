# Step: Human approve creatives

Present the creative pack for **Review**. Do not build campaigns until this gate passes.

## Summary to present

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
CREATIVE REVIEW — APPROVE BEFORE CAMPAIGN BUILD
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Product   : {{params.product}}
Channel   : {{params.channel}}
Daily cap : ${{params.daily-budget}}
Dry-run   : {{params.dry-run}}

Pack      : {{state.creative_pack}}

RECOMMENDED TO KEEP (top 5)
1. ...
2. ...
3. ...
4. ...
5. ...

RECOMMENDED TO CUT / REWRITE
- ...

RISKS
- Brand / compliance claims to scrub
- Missing assets (image/video/VO)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

Ask the user to:
1. Approve as-is, or
2. List edits (hooks to kill, lines to change), or
3. Abort

## Output

Set `approved_creatives` to the approved subset (paths + IDs of kept creatives)
and any edit notes. If aborted, stop the playbook.
