# WebSearch — Findings & Improvement Plan

**Status:** Phase 1 (detect + fallback + honest reporting) and Phase 2 (Wikipedia
anchor tier) implemented and tested (2026-10-28).

## TL;DR

"Simple searches return no results" is a **false success**, not a genuine empty result.
The default fallback (DuckDuckGo) is being **bot-blocked**, and the code silently
turns that block into `ToolResult.Success("No results found")`. The SearXNG path
works correctly when it's available.

---

## Architecture (current)

`WebSearchTool` (`src/OpenMono.Cli/Tools/WebSearchTool.cs`) has two backends:

1. **SearXNG (preferred)** — via the Caddy gateway (`/search?q=…&format=json`),
   gated on `GatewayCapabilities.IsEnabledAsync(Search)`. Rich JSON with
   `title`, `url`, `content`, `engine`, `score`. **Works correctly.**
2. **DuckDuckGo (fallback)** — plain `https://html.duckduckgo.com/html/?q=…`
   parsed with regex (`result__a` / `result__snippet`). **This is where it breaks.**

Routing decision: if `OPENMONO_WEB_GATEWAY` is set and the gateway reports
`search: true` (probed via `GET /services`), use SearXNG; otherwise fall back to
DDG. Most installs do **not** run `openmono setup search`, so most agents hit DDG.

## Root cause (reproduced)

Against the agent's real `User-Agent` (`OpenMono.ai/0.1 (coding-agent)`):

| Query | HTTP | results__a | Body |
|-------|------|-----------|------|
| `csharp regex library` | 200 | 10 | real results — parser pulled all 10 links + 10 snippets cleanly |
| `python asyncio` | 202 | 0 | DDG bot-challenge page |
| 10-query sweep (docker compose, git rebase, postgres jsonb, react hooks, linux systemd, go goroutines, sql join, http 404, …) | all 202 | all 0 | bot-challenge page |

The 202 body is DDG's captcha:
> "Unfortunately, bots use DuckDuckGo too. Please complete the following challenge to confirm this search was made by a human. Select all squares containing a duck."

So: **DDG's bot-detection/rate-limiting returns a challenge page for simple queries,
and the code reads 0 links and returns `Success("No results found")`.**

### What is *not* the bug

- The **regex parser is correct.** On a genuine 200 response it extracted all 10
  links and 10 snippets with proper `uddg=` URL decoding and tag-stripping.
- The **SearXNG path is correct.** Direct hit on the gateway returned rich,
  correct JSON for `python asyncio` (docs.python.org + real walkthroughs).

## Gaps in the current code

1. **No bot-block detection.** `EnsureSuccessStatusCode()` + regex means a 202/400
   challenge page just yields 0 results → indistinguishable from a genuinely
   empty search.
2. **Single endpoint, no retry.** Only `html.duckduckgo.com/html/`. No fallback to
   the more resilient `lite.duckduckgo.com/lite/`, no alternate UA, no backoff.
3. **Misleading result type.** A blocked search returns `Success`, not `Error`,
   so the agent can't tell "nothing matched" from "search is being blocked" and
   can't retry.
4. **SearXNG is opt-in by default.** Onboarding (`openmono setup search`) is not
   the default, so the fragile DDG path is what most agents actually use.

## Caveat

DDG's gating is increasingly **IP-based**, so swapping the `User-Agent` alone will
not fully fix it. That's why detection + fallback + making SearXNG the default
matter more than a UA change.

---

## Proposed fix plan (ranked by impact / risk)

### 1. Detect the block and stop lying (highest impact, lowest risk)
In `DuckDuckGoSearchAsync`:
- Treat non-200 status (esp. 202/400) as a **block**, not "no results".
- Detect the captcha body (e.g. substring `"bots use DuckDuckGo"` / "challenge"
  / duck-squares markup) as a secondary signal.
- Return a distinct result — ideally `ToolResult.Error(...)` with a message like
  `"Search provider blocked (bot challenge). Retrying via alternate endpoint."`
  so the agent sees a real failure and can retry or fall back to another tool.

### 2. Add a fallback chain
`html.duckduckgo.com/html/` → `lite.duckduckgo.com/lite/` (different markup,
more bot-resistant) → clear error.
- Add a second parser for the `lite` markup (its result links/snippets use
  `result__a` / `result__snippet` too but the page structure differs — verify the
  exact classes before reusing the regex).
- On fallback attempts, use a **browser-like UA** (e.g. Chrome) since the
  agent UA is what's being flagged.
- Keep the SearXNG path untouched; this only hardens the DDG fallback.

### 3. Retry with short backoff
On a detected transient block, retry once or twice with a small delay (e.g. 2–5s)
and the alternate endpoint/UA before giving up. Cap total work so the 15s tool
timeout isn't blown.

### 4. Make SearXNG the default path (structural)
- Nudge onboarding so `openmono setup search` is on by default (or enabled on
  install).
- Treat the DDG fallback as a true last resort, and surface in debug logs when
  SearXNG is unavailable so operators know why DDG is being used.

### 5. (Optional) Improve output for the agent
- Include `engine` and `score` from SearXNG when available, so the agent can
  judge result quality.
- Return the **query + provider + status** in the result header so the agent can
  distinguish "empty" from "blocked" from "error".

---

## Suggested implementation scope (minimal, safe)

Touch only `src/OpenMono.Cli/Tools/WebSearchTool.cs`:

- Add a `bool IsBotBlocked(int statusCode, string html)` helper.
- Refactor `DuckDuckGoSearchAsync` into a `TrySearchAsync(endpoint, ua)` loop
  over `[html, lite]`, with the block check and one retry.
- Add a `ParseLiteResults` (or generalize the existing parser) for the lite markup.
- Change the "no results" path so a **blocked** search returns `ToolResult.Error`
  (not `Success`), while a genuine empty result still returns `Success`.
- Leave `SearxngSearchAsync` and the gateway routing untouched.

**Tests to add** (`src/OpenMono.Tests/Tools/`):
- A fixture HTML containing the captcha body → returns `Error` (not `Success`).
- A fixture `lite` HTML → parsed results match expected.
- A genuine empty (200, 0 links) response → still `Success("No results found")`.
- Existing `SanityCheck` for `WebSearch` still passes.

**Build/test:** `dotnet build OpenMono.sln` + `dotnet test` — confirm green.

---

## Files involved

- `src/OpenMono.Cli/Tools/WebSearchTool.cs` — primary change.
- `src/OpenMono.Cli/Tools/GatewayCapabilities.cs` — read-only (routing).
- `docker/Caddyfile`, `docker/searxng/settings.yml` — no change needed.
- `src/OpenMono.Tests/Tools/` — new unit tests.

---
---

# Phase 2 — Reliability-chain research (no API keys)

**Date:** 2026-10-28. **Constraint:** no API keys / no paid services. **Goal:** pick the
best no-key tool as **primary**, with others as **fallbacks** in a reliability chain.

> ⚠️ **VANTAGE-POINT CAVEAT (correction):** The probes in the table below were run
> **from the agent's Docker container**, not from the inference box. But `WebSearch`
> normally routes through the **Caddy gateway beside the inference server** (in
> dual-box mode, a *different machine*), so the egress IP that actually matters for
> the SearXNG primary path is the **inference box's** IP — which could differ
> (residential vs. datacenter) and was **not** measured. The "bot-walled" conclusion
> below therefore only holds for the **DuckDuckGo fallback path** (which egresses from
> the agent container when the gateway is unreachable). It must be re-verified from the
> correct box before it is used to pick the primary.

## Candidate constraint: egress IP may be bot-walled (unverified for the real path)

The agent container's outbound IP is challenged by virtually every general
web-search provider. Probed live on 2026-10-28 **from the container**:

| Provider | Path | Result from this egress IP |
|----------|------|----------------------------|
| DuckDuckGo | `html.duckduckgo.com/html/` + `lite.duckduckgo.com/lite/` | 202 bot-challenge (already handled in Phase 1) |
| Mojeek | `mojeek.com/search` | JS captcha |
| Marginalia | `search.marginalia.nu` | "wait a moment" JS gate + aggressive-bot notice |
| SearXNG (anonsearch.win) | `/search?format=json` | HTTP 429 |
| SearXNG (baresearch.org) | `/search?format=json` | Anubis proof-of-work (JS) |
| SearXNG (searx.be) | `/search?format=json` | browser-verify JS challenge |
| SearXNG (paulgo.io, opnxng.com) | `/search?format=json` | HTTP 429 |

**Implication:** from a datacenter IP, a *pure keyless general-web* chain is fragile by
its nature — the providers that don't need keys are the same ones that wall datacenter
ranges. A keyless chain is only robust when it either (a) runs its own metasearch engine
(own egress identity), or (b) leans on a keyless source that is *not* a general web search
engine (Wikipedia).

## What still works keyless from here

- **Wikipedia REST search** — `GET /w/rest.php/v1/search/page?q=...&limit=N` returns clean
  JSON `pages[]` with `title`, `key`, `description`, `excerpt`, `thumbnail`. No auth, no
  rate-limit observed, **no bot wall**. Good for reference/technical/entity queries; weak
  for news or "latest X" content. A reliable **anchor** tier, not a general-web substitute.
- **Self-hosted SearXNG** — the existing `openmono setup search` + Caddy gateway path
  (`http://localhost:7474/search?format=json`). Runs under our own identity/IP, so it is
  the only keyless path that is *not* subject to the egress-IP wall the same way. **This
  is the strongest primary.** (Not currently running on this host: gateway `/services`
  returned 000.)

## What is out (no-key)

- **MetaGer JSON API** — moved to a **paid token model** ("~500 tokens (5 €) ≈ 2 months").
  Not keyless. Excluded.
- **Public SearXNG instance pool** (discovered via `searx.space/data/instances.json`, a
  canonical, live, machine-readable list — e.g. anonsearch.win, baresearch.org, searx.be,
  opnxng.com, all recently A+/100% uptime). Two blockers for a generic fallback:
  1. **`format=json` is opt-in** in an instance's `settings.yml`; many defaults ship
     `formats: [html]` only, so JSON requests 406.
  2. **Most instances sit behind a bot wall** (Anubis PoW / rate-limiter / JS verify),
     which a non-browser HTTP client cannot pass — i.e. the same wall as DDG.
  So a pool can only be a *best-effort* low-priority tier with per-instance
  probe-then-prune, not a dependable primary.

## Recommended no-key chain (ranked)

1. **Primary — self-hosted SearXNG via the local gateway** (`openmono setup search`).
   Most reliable keyless path because it uses our own egress identity; rich JSON
   (title/url/content/engine/score) already parsed by `SearxngSearchAsync`.
2. **Secondary — DuckDuckGo `html/` → `lite/`** (Phase 1: browser UA, bot-challenge
   detection, `Error`/`Empty` reporting). Zero setup, works on residential IPs.
3. **Tertiary (best-effort) — curated public SearXNG pool**, `format=json`. Pull
   `searx.space` instance list at runtime (cache + TTL), probe each for a clean
   JSON response (prune on 429/PoW/406), first clean hit wins. Expect high variance;
   treat as a long shot, cap the probe budget so it can't blow the tool timeout.
4. **Anchor (always-on, keyless) — Wikipedia REST search.** Deterministically clean JSON
   from this IP; guarantees a *real* (possibly empty) answer rather than a false success
   for reference/technical queries. Merge/append as `provider=wiki` results.

## Implemented (2026-10-28): Wikipedia anchor tier

**Decision (user-approved):** keep the SearXNG-if-present + DuckDuckGo chain; add a
**Wikipedia anchor** so the agent has a reliable keyless source it can lean on for
general info — *in conjunction with* (not only as a last resort to) web search, and
defending against the IP wall. The user confirmed no API keys anywhere in the chain.

**Design:** `WebSearchTool` runs the existing gateway → DuckDuckGo chain unchanged.
Only when that path yields no usable result (blocked / error / true-empty — i.e.
`ResultClass.Empty` or `InvalidInput`, **never** a `Success`) does it merge up to 3
keyless Wikipedia REST results, each tagged `(wikipedia)`, into a single `Success`
whose text keeps the honest web-search outcome above the merged results. On a
successful web search the anchor is never consulted. The anchor is best-effort: a
Wikipedia failure is swallowed and the original honest result is returned, so it can
never turn an error into a false success.

- **Endpoint:** `GET https://en.wikipedia.org/w/rest.php/v1/search/page?q=…&limit=3`
  (keyless JSON: `pages[].title`, `pages[].key`, `pages[].excerpt`; HTML match spans
  stripped; URL = `https://en.wikipedia.org/wiki/<key>`).
- **Config:** `web.wikipedia_anchor` / `OPENMONO_WEB_WIKI_ANCHOR` (default **on**;
  `false` disables — also the hook the unit tests use to assert DDG-only behaviour).
- **Files:** `WebSearchTool.cs` (anchor + merge), `AppConfig.cs` (`WebConfig` flag),
  `ConfigLoader.cs` + `AgentContainer.cs` (env wiring), `WebSearchToolTests.cs` (+5 tests).

**Live verification on this machine** (a scratch harness driving the real
`WebSearchTool.ExecuteAsync` with this machine's real config — dead
`host.docker.internal:7480` gateway, `web.search=true` — and the real network):
DuckDuckGo is bot-walled from this egress (one intermittent slip-through was observed
on a single query, confirming rate-limit behaviour). In every blocked case the anchor
merged 3 Wikipedia results and the tool returned a usable `Success`:

```
=== query: C# Roslyn compiler API
1. Roslyn (compiler) (wikipedia)   https://en.wikipedia.org/wiki/Roslyn_(compiler)
2. List of compilers (wikipedia)
3. Java compiler (wikipedia)
(Live probe: 5/5 PASS — anchor merged exactly when DuckDuckGo was blocked.)
```

**Not built (deliberately, for now):** the tier-3 public SearXNG pool — high runtime
complexity (instance discovery/probing/pruning/TTL) for a fragile benefit, and every
probed instance was bot-walled from this IP. Revisit only if a real SearXNG gateway
isn't available. A local SearXNG gateway remains the strongest keyless primary and is
unchanged; standing one up here requires host mode (`openmono setup search`).
