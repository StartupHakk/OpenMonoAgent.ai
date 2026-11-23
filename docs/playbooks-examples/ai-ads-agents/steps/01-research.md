# Step: Research

Gather competitor ads, voice-of-customer, and offer angles for **{{params.product}}**
on channel **{{params.channel}}**.

## Instructions

1. Record UTC start timestamp.

2. Competitor Scout — find 5–15 active ads for similar offers:
   - Meta Ad Library / TikTok Creative Center / web search
   - Note hook, offer, CTA, format (static / UGC / demo)

3. VOC Miner — pull language from reviews, testimonials, support themes, Reddit/forums:
   - pains, desires, objections, exact phrases customers use

4. Offer Analyst — map:
   - primary offer, price, guarantee, urgency, differentiator
   - landing URL: {{params.landing-url}}

5. Write a research brief to disk:

   ```
   artifacts/ai-ads-agents/research-{{shell:date -u +%Y%m%d}}.md
   ```

## Brief template

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
RESEARCH BRIEF — {{params.product}}
Channel : {{params.channel}}
Budget  : ${{params.daily-budget}}/day hard cap
Dry-run : {{params.dry-run}}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

COMPETITOR ANGLES (cite sources)
1. ...
2. ...

VOC LANGUAGE (verbatim where possible)
- ...

OBJECTIONS TO HANDLE
- ...

TOP 10 ANGLE HYPOTHESES
1. ...
10. ...

DO NOT CLAIM (compliance / brand)
- ...
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

## Output

Set `research_brief` to the file path and a 5-bullet executive summary.
Do not create ads or campaigns in this step.
