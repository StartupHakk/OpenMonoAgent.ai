# AI Ads Agents Blueprint (StartupHakk / OpenMono)

Build a **human-approved** ad system: agents research, create, launch, and optimize — you sign off before spend goes live.

## Non-negotiables
1. **Hard budget caps** per day / campaign / account.
2. **No live ads without your approval** (draft → review → publish).
3. **Sales > clicks** — optimize on purchases / ROAS when possible.
4. **Kill switches** — pause all spend in one command.

---

## Stack (tools)

| Layer | Tool options | Role |
|-------|----------------|------|
| Agent runtime | OpenMono, Claude Code, Cursor | Orchestrate agents + playbooks |
| Research | Meta Ad Library, TikTok Creative Center, web scrape | Competitor + voice-of-customer |
| Copy / angles | Claude / OpenRouter | Hooks, offers, scripts |
| Creative | Canva API, CapCut, Runway, “image/video” agents | Static + video ads |
| Voice | ElevenLabs | Voiceovers |
| Launch | Meta Marketing API, TikTok Ads API, Google Ads API | Create campaigns as **PAUSED** |
| Attribution | Shopify / Stripe + Meta CAPI / GA4 | Sales, CAC, ROAS |
| CRM | HubSpot MCP | Leads, deals, LTV |
| Alerts | Slack / email | Morning one-pager + kill alerts |
| Secrets | Env vars / 1Password — never commit keys | Auth |

Start with **one channel** (usually Meta), then add TikTok/Google.

---

## Agent roster (32 → start with 12)

You don’t need 32 on day one. Ship this **core 12**, then scale.

### Research (3)
1. **Competitor Scout** — pulls active ads + angles from Ad Library.
2. **VOC Miner** — reviews, support tickets, testimonials → pain/desire language.
3. **Offer Analyst** — maps offers, pricing, guarantees, objections.

### Creative (4)
4. **Angle Writer** — 10 hooks/angles from research.
5. **Script Writer** — 15s / 30s short-form scripts.
6. **Static Designer** — image ad variants (3–5 per angle).
7. **Video Assembler** — cut + captions + ElevenLabs VO.

### Launch (2)
8. **Campaign Builder** — creates campaigns/ad sets/ads **PAUSED** with test budgets.
9. **Audience Mapper** — interests / lookalikes / broad + geo exclusions.

### Optimize (2)
10. **Spend Guardian** — every few hours: pause high CPC / zero purchase / over-cap.
11. **Winner Promoter** — proposes budget shifts; waits for your OK.

### Reporting (1)
12. **Morning Brief** — one page: spend, sales, CAC/ROAS, pauses, next tests.

### Optional scale to ~32
Duplicate creative/launch agents per channel (Meta/TikTok/Google), add A/B statisticians, landing-page tester, email follow-up, UGC brief writer, compliance checker, brand-voice linter, etc.

---

## Workflow (daily)

```
Research → Creatives (draft) → YOU APPROVE
    → Launch PAUSED → YOU APPROVE LIVE
    → Guardian loop (auto-pause losers)
    → Morning Brief → YOU APPROVE budget moves
```

### Approval gates
| Gate | What you see | What agents must not do |
|------|----------------|-------------------------|
| Creative | Hooks, scripts, creatives | Publish |
| Launch | Campaign + budget + audience | Unpause |
| Budget move | Proposed reallocations | Raise caps without OK |

---

## Test budget recipe (safe default)
- **$20–50/day** total while learning.
- **3–5 ads** live at once max.
- Kill rule examples:
  - 0 purchases after **$X** spend → pause
  - CPC > **$Y** for 24h → pause
  - Frequency > **Z** → refresh creative

Tune X/Y/Z to your margins.

---

## OpenMono playbook shape

```
ai-ads-agents/
  PLAYBOOK.md          # this blueprint, short form
  steps/
    01-research.md
    02-creatives.md
    03-human-approve-creatives.md
    04-build-paused-campaigns.md
    05-human-approve-launch.md
    06-optimize-loop.md
    07-morning-brief.md
  scripts/
    pull-ad-library.sh
    pause-all.sh         # kill switch
    morning-brief.sh
```

Wire each step as an OpenMono playbook step with **explicit human confirmation** before publish/spend.

---

## MVP in 7 days

| Day | Outcome |
|-----|---------|
| 1 | Meta + store/CRM connected; budget caps + pause-all script |
| 2 | Competitor + VOC research agent producing a brief |
| 3 | 10 angles + 5 scripts |
| 4 | 5 static + 2 video drafts |
| 5 | Human approve → build **paused** campaigns |
| 6 | Soft launch tiny budget; Guardian loop on |
| 7 | Morning Brief + first winner/kill decisions |

---

## CTA asset (for the Short)
Comment **ADS** → send:
1. This blueprint PDF/Notion page  
2. Checklist of API connections  
3. Kill-switch script  
4. Sample morning-brief template  

---

## Kill switch (concept)
One command / button that:
1. Pauses all campaigns in connected ad accounts  
2. Posts “SPEND PAUSED” to Slack  
3. Writes a timestamped log under `~/.openmono/artifacts/`
