"""OpenMono.ai — Scrapling scrape service.

Thin HTTP wrapper around Scrapling that backs the agent's WebFetch tool. Runs on
the inference box behind the Caddy gateway, which enforces the shared bearer
token, so this service stays auth-free on the internal network and is never
published to the host.

POST /scrape  { "url": "...", "render": false, "headless": true,
                "max_length": 20000, "format": "markdown" }
  -> { "status": 200, "url": "...", "engine": "fetcher"|"stealthy",
       "format": "markdown", "truncated": false, "content": "..." }
"""
import logging
from typing import Optional

from fastapi import FastAPI
from pydantic import BaseModel
from starlette.concurrency import run_in_threadpool

from scrapling.fetchers import AsyncFetcher, StealthyFetcher

logging.basicConfig(level=logging.INFO)
log = logging.getLogger("scrapling-service")

app = FastAPI(title="OpenMono Scrapling service")


class ScrapeRequest(BaseModel):
    url: str
    render: bool = False            # True -> skip the fast path, use the browser
    headless: bool = True           # browser path only; False -> headed (needs a display)
    max_length: Optional[int] = None
    format: str = "markdown"        # markdown | text | html


def _extract(page, fmt: str) -> str:
    if fmt == "html":
        return page.html_content or ""
    if fmt == "text":
        return page.get_all_text() or ""
    # Default markdown; fall back to plain text on versions without `.markdown`.
    md = getattr(page, "markdown", None)
    return md if md else (page.get_all_text() or "")


def _stealth_fetch(url: str, headless: bool = True):
    # Camoufox-backed real browser; solves Cloudflare Turnstile / interstitials.
    return StealthyFetcher.fetch(
        url, headless=headless, solve_cloudflare=True, network_idle=True
    )


@app.get("/health")
async def health():
    return {"status": "ok"}


@app.post("/scrape")
async def scrape(req: ScrapeRequest):
    engine = "stealthy" if req.render else "fetcher"
    page = None
    error = None

    if not req.render:
        # Fast path: plain HTTP, no browser.
        try:
            page = await AsyncFetcher.get(req.url)
        except Exception as exc:  # noqa: BLE001
            error = str(exc)
        # Auto-escalate to the stealth browser on block/challenge responses.
        if page is None or getattr(page, "status", 0) in (403, 429, 503):
            engine = "stealthy"
            page = None

    if page is None:
        try:
            page = await run_in_threadpool(_stealth_fetch, req.url, req.headless)
        except Exception as exc:  # noqa: BLE001
            return {
                "status": 502,
                "url": req.url,
                "engine": engine,
                "content": "",
                "error": error or str(exc),
            }

    content = _extract(page, req.format)
    truncated = False
    if req.max_length and len(content) > req.max_length:
        content = content[: req.max_length]
        truncated = True

    return {
        "status": getattr(page, "status", 200),
        "url": req.url,
        "engine": engine,
        "format": req.format,
        "truncated": truncated,
        "content": content,
    }
