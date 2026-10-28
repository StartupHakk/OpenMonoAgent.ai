# WebSearch — Findings & Improvement Plan

**Status:** Investigation complete (2026-10-22). Root cause reproduced and confirmed.

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
