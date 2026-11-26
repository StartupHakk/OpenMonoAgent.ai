# Step: Creatives (draft only)

Turn the research brief into a creative pack. **Nothing publishes.**

## Instructions

1. Read `{{state.research_brief}}`.

2. Produce:
   - **10 hooks** (≤12 words each)
   - **5 body angles** (pain → promise → proof → CTA)
   - **3 × 15s scripts** and **2 × 30s scripts** for short-form video
   - **Static brief** — 5 image concepts (headline, visual, CTA)
   - **Video brief** — 2 UGC/demo concepts (shot list + VO lines)
   - Brand voice: {{params.brand-voice}}

3. For each creative, include a **real reason to buy** (feature → outcome).

4. Save the pack:

   ```
   artifacts/ai-ads-agents/creatives-{{shell:date -u +%Y%m%d}}.md
   ```

5. If image/video/ElevenLabs tools are not connected, leave placeholders and
   mark status `DRAFT_TEXT_ONLY` — still complete the copy pack.

## Creative pack template

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
CREATIVE PACK — {{params.product}} (DRAFT)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

HOOKS
1. ...

SCRIPTS
15s-A: ...
15s-B: ...
15s-C: ...
30s-A: ...
30s-B: ...

STATICS
S1: ...
S5: ...

VIDEOS
V1: ...
V2: ...

STATUS: DRAFT — awaiting human Review gate
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

## Output

Set `creative_pack` to the file path. Do not ask for spend approval here.
