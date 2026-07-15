# API keys vault

**Canonical secrets file (never commit):** `~/jarvis/secrets/api-keys.env`

Paste keys there. MCP servers launched via `~/jarvis/scripts/mcp-with-keys.sh` inherit every variable in that file.

## Tailscale (do this now)

1. Open https://login.tailscale.com/admin/settings/keys
2. Generate **API key** (`tskey-api-...`)
3. Paste into `~/jarvis/secrets/api-keys.env`:

```
TAILSCALE_API_KEY=tskey-api-...
TAILSCALE_TAILNET=-
```

4. Tell Alfred: **keys are in**

Tailscale MCP is already wired in `~/.cursor/mcp.json` as `tailscale` (`@hexsleeves/tailscale-mcp-server`).

## Shared keys for “everybody”

Do **not** paste secrets into each MCP server block. Put them once in `api-keys.env`, then launch servers with:

```bash
~/jarvis/scripts/mcp-with-keys.sh <command> [args...]
```

That is how Tailscale is configured globally for Cursor.
