# Configuration

## CLI commands

Commands are grouped by which box they run on.

### Any box

| Command | Description |
|---------|-------------|
| `openmono setup [--full\|--inference\|--agent] [--gpu\|--cpu] [--model=NAME] [--vision=on\|off] [--options]` | Install and configure a box role |
| `openmono config <list\|get\|set\|unset> [key] [value]` | Read/write `~/.openmono/settings.json` |
| `openmono help` | Show help |

### Agent box

| Command | Description |
|---------|-------------|
| `openmono agent` | Run the coding agent in the current directory |
| `openmono graph [path]` | Build the code-review-graph index for a project |
| `openmono graphify [path]` | Build the Graphify knowledge graph for a project |

### Inference box — llama-server lifecycle

| Command | Description |
|---------|-------------|
| `openmono start` | Start llama-server in the background |
| `openmono stop` | Stop llama-server (`docker compose down`) |
| `openmono restart` | Restart llama-server |
| `openmono logs` | Tail llama-server logs |
| `openmono status` | Show container, GPU, and model status |

### Inference box — frpc tunnel (dual-box only)

| Command | Description |
|---------|-------------|
| `openmono tunnel setup` | Install frpc + systemd unit (prompts for relay signup values) |
| `openmono tunnel start` | Start the frpc tunnel |
| `openmono tunnel stop` | Stop the frpc tunnel |
| `openmono tunnel restart` | Restart the frpc tunnel |
| `openmono tunnel status` | Show tunnel state and configured target |
| `openmono tunnel logs` | Tail frpc logs (`journalctl -u frpc`) |

The frp client version used by `openmono tunnel setup` is configurable in
settings.json at `frp.version` (e.g. `openmono config set frp.version 0.71.1`).
It defaults to `0.71.1` when unset, and the `FRP_VERSION` environment variable
overrides it for a single run.

### Upgrade & component versioning

| Command | Description |
|---------|-------------|
| `openmono upgrade` | Reconcile all installable components to their pinned versions |

`openmono upgrade` is a **component-aware reconciler**. It checks the repo
code for updates, then ensures every installable component on the box matches
its pinned version. It only updates components that are actually installed;
it never touches user state (settings.json, relay.json, frpc.toml, API keys,
model GGUF files).

**Pinned components:**

| Component | Pinned in | What it controls |
|-----------|-----------|------------------|
| frp | `versions.json` → `frp` | frpc tunnel client version |
| llama | `versions.json` → `llama_*` / `prism_*` | llama.cpp server image (cuda/vulkan/cpu) or Prism fork (Bonsai) |
| strata | `versions.json` → `strata` | Strata inference backend image |
| agent | `versions.json` → `agent` | Agent Docker image (rebuilt only when code changes) |
| caddy | `versions.json` → `caddy` | Caddy gateway image |
| searxng | `versions.json` → `searxng` | SearXNG private search image |
| scrapling | `versions.json` → `scrapling` | Scrapling anti-bot scraper image |

**Per-box version lock (`~/.openmono/versions.json`):**

Each box maintains a lock file at `~/.openmono/versions.json`. Every component
has an entry of the form `{ "version": <string>, "status": <string> }`:

- `"version"` — the concrete ref actually installed on this box (e.g.
  `"searxng/searxng:2026.10.30-ffa88e3ba"` or `"latest"`).
- `"status"` — either `"latest"` or `"pinned"`:
  - `"latest"`: the component tracks the repo catalog pin. On each `openmono
    upgrade`, the reconciler refreshes it to whatever `versions.json` says.
    This is the default for all components after a fresh install.
  - `"pinned"`: the user has explicitly locked this component to a specific
    value. `openmono upgrade` **skips** it (reports it as "locked (skipped)").
    To unlock, set the status back to `"latest"` or change the version to
    `"latest"`.

To pin a component, edit `~/.openmono/versions.json` directly:

```bash
# Pin searxng to a specific version (upgrade will skip it):
jq '.searxng = { version: "searxng/searxng:2025.11.6-7af922c9d", status: "pinned" }' \
   ~/.openmono/versions.json > /tmp/vj && mv /tmp/vj ~/.openmono/versions.json

# Un-pin (resume tracking the catalog):
jq '.searxng = { version: "latest", status: "latest" }' \
   ~/.openmono/versions.json > /tmp/vj && mv /tmp/vj ~/.openmono/versions.json
```

The box file is **auto-created** on both `openmono setup` and `openmono
upgrade` (self-healing: if the file is missing or malformed, it is re-seeded
without clobbering any user-pinned values).

**Prism (Bonsai) detection:**

A box running the Bonsai (Prism) backend is detected by the `MODEL_NAME` in
`docker/.env` matching `*Ternary-Bonsai*`. The reconciler pins these boxes to
the `prism_cuda` / `prism_vulkan` catalog keys (not the stock `llama_*` keys)
and **never** replaces a live Prism image with a stock llama.cpp image — the
stock image rejects the ternary packings and the `--no-mmap` flag that the
override passes, which would crash-loop the box.

**What `openmono upgrade` does (in order):**

1. Pulls the latest code from the current branch (skipped if already current,
   but the reconciler still runs).
2. Seeds the box lock file if missing.
3. Reports which components are user-locked (skipped).
4. Refreshes frpc to the pinned version (Homebrew on macOS, binary download on
   Linux).
5. Refreshes the llama-server image to the pinned ref (pulls + force-recreates
   the container).
6. Refreshes caddy and searxng images (if installed).
7. Re-applies inference config from settings.json to `docker/.env` (if drifted).
8. Prints a component report: `updated`, `up to date`, `locked (skipped)`,
   or `skipped (run manually)`.

The agent Docker image is **rebuilt only when code has actually changed**; if
the code is already at the upstream pin, no rebuild occurs.

### Global flags

| Flag | Description |
|------|-------------|
| `--verbose`, `-v` | Enable verbose/debug output (forwarded to the agent) |

### Examples

```bash
openmono setup                    # First-time single-box setup (auto-detects GPU)
openmono setup --cpu              # Force CPU mode
openmono start                    # Start llama-server
openmono agent                    # Run agent in current directory
openmono --verbose                # Run agent with LLM debug output
WORKSPACE=/my/repo openmono agent # Run agent against a specific repo
openmono graph /path/to/project   # Index a project for code search
```

For the dual-box walkthrough, see [SETUP.md → Dual-box setup](SETUP.md#dual-box-setup).

---

## Settings

Settings are loaded in this order, each layer overriding the previous:

1. Built-in defaults
2. `~/.openmono/settings.json` — user-level
3. `.openmono/settings.json` — project-level (in cwd)
4. `--config <path>` — load settings from a specific file
5. Environment variables
6. CLI flags (`--model`, `--endpoint`, etc.) — highest priority

---

## CLI flags

Flags passed to `openmono agent` override settings.json and env vars for that session only.

| Flag | Equivalent setting | Description |
|------|--------------------|-------------|
| `--config <path>` | — | Load settings from a specific file |
| `--model <name>` | `llm.model` | Override the model name |
| `--endpoint <url>` | `llm.endpoint` | Override the LLM server endpoint |
| `--workdir <path>` | — | Override the working directory for this run |
| `--verbose`, `-v` | — | Show full LLM stream, SSE events, and token counts |
| `--detail` | — | Show extra detail in TUI panels |
| `--tui` | — | Force the TUI renderer |
| `--classic` | — | Use classic scrolling terminal instead of TUI |
| `--no-acp` | — | Disable the ACP server |
| `--acp-only` | — | Start in ACP server mode (no TUI) — for VS Code / Cursor extension |
| `--acp-port <port>` | — | ACP server port (default: `7475`) |

---

## `openmono config` commands

Read and write settings.json from the terminal without editing the file directly.

```bash
openmono config set llm.endpoint http://localhost:7474
openmono config set llm.model qwen3.8-27b
openmono config get llm.endpoint
openmono config unset llm.api_key
```

These always write to `~/.openmono/settings.json`. Project-level `.openmono/settings.json` is read when present, but `openmono config` does not write it, and there is no `--global` flag.

---

## Full example

```jsonc
{
  "llm": {
    "endpoint": "http://localhost:7474",
    "model": "qwen3.8-27b",
    "max_output_tokens": 16384,
    "temperature": 0.7,
    "top_p": 0.8,
    "top_k": 20,
    "presence_penalty": 1.5
  },
  "providers": {
    "anthropic": {
      "api_key": "sk-ant-...",
      "model": "claude-opus-4-7",
      "active": false
    },
    "openai": {
      "api_key": "sk-...",
      "model": "gpt-4o",
      "active": false
    },
    "ollama": {
      "endpoint": "http://localhost:11434",
      "model": "llama3",
      "active": false
    }
  },
  "permissions": {
    "tools": {
      "Bash": {
        "allow": ["git *", "dotnet *", "npm *"],
        "deny": ["rm -rf *"],
        "ask": ["sudo *"]
      }
    }
  },
  "hooks": {
    "pre_tool_use": [
      {
        "if": { "tool": "Bash", "input_contains": "rm" },
        "run": "echo '{{tool_name}}: {{tool_input}}' >> audit.log"
      }
    ],
    "post_tool_use": [],
    "session_start": []
  },
  "mcp_servers": {
    "my-server": {
      "command": "npx",
      "args": ["-y", "@my-org/mcp-server"],
      "env": { "MY_KEY": "value" },
      "enabled": true
    }
  },
  "model_presets": {
    "precise": {
      "temperature": 0.2,
      "top_p": 0.9,
      "active": false
    }
  },
  "playbooks": {
    "paths": [".openmono/playbooks/", "~/.openmono/playbooks/"]
  },
  "web": {
    "gateway": "http://localhost:47480",
    "search": true,
    "scrape": true
  },
  "frp": {
    "version": "0.71.1"
  },
  "auto_detect_code_graph": true
}
```

---

## `llm`

Controls the active LLM connection and sampling parameters. At startup, `model` and `context_size` are overridden automatically from the llama.cpp `/props` endpoint.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `endpoint` | string | `http://localhost:7474` | OpenAI-compatible chat endpoint |
| `model` | string | *(from /props)* | Model name sent in requests |
| `api_key` | string | — | API key for cloud providers |
| `context_size` | int | *(from /props)* | Context window size in tokens |
| `max_output_tokens` | int | `16384` | Max tokens per response |
| `temperature` | float | `0.7` | Sampling temperature |
| `top_p` | float | `0.8` | Nucleus sampling threshold |
| `top_k` | int | `20` | Top-K sampling |
| `presence_penalty` | float | `1.5` | Penalise repeated tokens |
| `min_p` | float | `0.0` | Min-P sampling cutoff |
| `repetition_penalty` | float | `1.0` | Repetition penalty multiplier |

---

## `providers`

Named provider configurations. Set `"active": true` on one to use it as the active provider. The provider is selected at startup; `/model` only changes the model name sent to it.

```jsonc
"providers": {
  "anthropic": { "api_key": "sk-ant-...", "model": "claude-opus-4-7", "active": true },
  "openai":    { "api_key": "sk-...",     "model": "gpt-4o",           "active": false },
  "ollama":    { "endpoint": "http://localhost:11434", "model": "llama3", "active": false }
}
```

| Key | Type | Description |
|-----|------|-------------|
| `api_key` | string | Provider API key |
| `endpoint` | string | Override the default endpoint URL |
| `model` | string | Model name for this provider |
| `active` | bool | Set to `true` to activate this provider |

Only one provider can be active at a time. The built-in `local` provider uses `llm.endpoint` directly.

---

## `permissions`

Per-tool allow/deny/ask rules. Rules are glob patterns matched against the tool's input string. Evaluated after the built-in capability check.

```jsonc
"permissions": {
  "tools": {
    "Bash": {
      "allow": ["git *", "dotnet *"],
      "deny":  ["rm -rf *"],
      "ask":   ["sudo *"]
    },
    "FileWrite": {
      "deny": ["*.env", "*.pem"]
    }
  }
}
```

| List | Behaviour |
|------|-----------|
| `allow` | Auto-approve without prompting |
| `deny` | Reject and show a denial warning to the user |
| `ask` | Always prompt, even if a session-level allow is set |

Permissions from user and project settings are merged additively.

---

## `hooks`

Shell commands triggered at key points in the agent loop. Templates `{{tool_name}}` and `{{tool_input}}` are available in `run`. Timeout: 30 s.

```jsonc
"hooks": {
  "pre_tool_use": [
    {
      "if": { "tool": "Bash", "input_contains": "rm" },
      "run": "echo '{{tool_name}}: {{tool_input}}' >> audit.log"
    }
  ],
  "post_tool_use": [],
  "session_start": [
    { "run": "echo 'Session started' >> session.log" }
  ]
}
```

| Hook | When |
|------|------|
| `session_start` | Once, when the agent session initialises |
| `pre_tool_use` | Before each tool call |
| `post_tool_use` | After each tool call completes |

The `if` condition is optional. Both `tool` (exact name) and `input_contains` (substring) can be combined.

Hooks from user and project settings are merged additively.

---

## `mcp_servers`

MCP servers started as subprocesses on session init. Each server's tools are registered as `mcp__{serverName}__{toolName}`.

```jsonc
"mcp_servers": {
  "my-server": {
    "command": "npx",
    "args": ["-y", "@my-org/mcp-server"],
    "env": { "API_KEY": "..." },
    "working_directory": "/path/to/dir",
    "enabled": true
  }
}
```

| Key | Required | Description |
|-----|----------|-------------|
| `command` | yes | Executable to run |
| `args` | no | Arguments array |
| `env` | no | Extra environment variables |
| `working_directory` | no | Working directory for the subprocess |
| `enabled` | no | Set to `false` to disable without removing (default: `true`) |

**Auto-detected servers**: `code-review-graph` is registered automatically if found in PATH with a graph DB present — no config needed.

---

## `model_presets`

Named LLM parameter bundles. Activate one via `"active": true` or the `OPENMONO_MODEL_PRESET` env var. Activation is applied at startup; there is no runtime command to switch presets. The built-in `qwen` and `qwen38` presets ship with the default sampling values for their model families.

```jsonc
"model_presets": {
  "precise": {
    "temperature": 0.2,
    "top_p": 0.95,
    "top_k": 40,
    "active": false
  },
  "creative": {
    "temperature": 1.0,
    "top_p": 0.9,
    "active": false
  }
}
```

Presets support all fields from [`llm`](#llm). Only one preset can be active at a time.

---

## `playbooks`

```jsonc
"playbooks": {
  "paths": [".openmono/playbooks/", "~/.openmono/playbooks/"]
}
```

Additional directories to scan for `.yaml` playbook files. Paths are checked in order; all discovered playbooks are registered.

---

## `web`

Controls the self-hosted web search and scraping gateway. Services are auto-detected via `GET /services` on the gateway — no manual config needed if you used `openmono setup search` or `openmono setup scraper`.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `gateway` | string | *(same as `llm.endpoint`)* | Caddy gateway base URL (`:47480`) |
| `search` | bool | *(auto-detected)* | Force-enable/disable SearXNG routing for `WebSearch` |
| `scrape` | bool | *(auto-detected)* | Force-enable/disable Scrapling routing for `WebFetch` |

Override via env vars: `OPENMONO_WEB_SEARCH=1` / `OPENMONO_WEB_SCRAPE=1` (truthy: `1/true/yes/on`).

→ [Web services architecture](ARCHITECTURE.md#inference-side-web-services-caddy-gateway)

---

## Host bridge (`openmono agent --host`)

The bare-metal host sub-agent has its **own** config file: `~/.openmono/host-bridge.json`
(0600, never holds secrets). It is *not* part of `settings.json`. `openmono config`
routes `host.*` keys to that file; every other key still goes to `settings.json`.

### Running the daemon

The host-bridge daemon is the bare-metal executor. Run it directly, or let
`openmono agent --host` start/stop it around an agent session:

```bash
openmono host daemon     # start the daemon (preferred; it stays up after the agent exits)
openmono host status     # is it running? shows version + paired-session count
openmono host stop       # stop it
```

Then pair any sandbox agent to it with `/host on` inside the agent (`/host off`
revokes it mid-session). The daemon listens on `127.0.0.1:7476` plus — on Linux —
the docker bridge gateway (`docker0`/`br-*`) so the agent container reaches it
via `host.docker.internal`; on macOS Docker Desktop forwards that name to the
host loopback natively. It never binds `0.0.0.0` or any LAN interface. The pair
secret is read from the bind-mounted `~/.openmono/host-bridge.json`.

### Policy keys

```bash
openmono config set host.sudo_policy allow      # escalation policy
openmono config set host.exec_default ask       # default policy for host commands
openmono config set host.run_as dimnd           # account host commands run as
openmono config set host.allow_list "git *,docker *,systemctl status *"
openmono config set host.deny_list "rm *,dd *,mkfs *"
openmono config set host.timeout_ms 600000      # foreground command timeout
openmono config get host.sudo_policy
openmono config unset host.run_as
```

### Key → JSON path mapping

| `openmono config` key | JSON path in `host-bridge.json` | Type | Default |
|-----------------------|--------------------------------|------|---------|
| `host.exec_default` | `.host_exec.default` | `ask` \| `allow` \| `deny` | `ask` |
| `host.sudo_policy` | `.allow_sudo` | `ask` \| `allow` \| `deny` | `ask` |
| `host.run_as` | `.run_as` | username (empty = whoever runs the bridge) | *(empty)* |
| `host.allow_list` | `.host_exec.allow` | string array (comma-separated on the CLI) | `git *`, `systemctl status *`, `journalctl *` (Linux; macOS samples ship `git *`, `launchctl list *`, `log show *`) |
| `host.deny_list` | `.host_exec.deny` | string array (comma-separated on the CLI) | *(sample deny list)* |
| `host.timeout_ms` | `.timeout_ms` | integer (milliseconds) | `300000` |

Policy semantics (`ask`/`allow`/`deny`) are shared between both policy keys:
`ask` prompts y/N per command (or per escalation, for `host.sudo_policy`); a **yes
is in-memory only for that one command and is never persisted**. Non-interactive
runs fail closed (an `ask` that cannot be confirmed is denied). See
[Deployment modes — host sub-agent](DEPLOYMENT-MODES.md#run-as-identity-sudo-and-the-paper-trail)
for the full behavior.

Install-time presets (non-interactive): `OPENMONO_HOST_BRIDGE`, `OPENMONO_HOST_EXEC_DEFAULT`,
`OPENMONO_HOST_SUDO`, `OPENMONO_HOST_RUN_AS`, `OPENMONO_HOST_PASSWORD_FILE`.

---

## Top-level controls

These are resolved from CLI flags or environment variables. The current config loader does not apply their `settings.json` equivalents, except `auto_detect_code_graph`, where only `true` is honored.

| Control | Source | Default | Description |
|---------|--------|---------|-------------|
| `auto_detect_code_graph` | settings.json | `true` | Auto-register MCP graph servers if found. A `false` value is currently ignored |
| `verbose` | `--verbose` | `false` | Log full LLM stream and tool pipeline |
| `show_detail` | `--detail` | `false` | Show extra detail in TUI panels |
| `data_directory` | `OPENMONO_DATA_DIR` | `~/.openmono` | Where sessions, memory, and checkpoints are stored |
| `working_directory` | `--workdir` / `OPENMONO_WORKSPACE` | cwd | Override the workspace root |
| `host_working_directory` | `OPENMONO_HOST_WORKSPACE` | — | Host path when running inside Docker (used for bind-mount mapping) |

---

## Environment variables

All env vars override their settings.json equivalents regardless of load order,
with one exception: `OPENMONO_CONTEXT_SIZE` is a fallback — a usable
server-reported `n_ctx` (at least 1024) takes precedence at startup.

| Variable | Equivalent setting |
|----------|--------------------|
| `OPENMONO_ENDPOINT` | `llm.endpoint` |
| `OPENMONO_MODEL` | `llm.model` |
| `OPENMONO_API_KEY` | `llm.api_key` |
| `OPENMONO_CONTEXT_SIZE` | `llm.context_size` |
| `OPENMONO_MAX_OUTPUT_TOKENS` | `llm.max_output_tokens` |
| `OPENMONO_TOP_P` | `llm.top_p` |
| `OPENMONO_TOP_K` | `llm.top_k` |
| `OPENMONO_PRESENCE_PENALTY` | `llm.presence_penalty` |
| `OPENMONO_MIN_P` | `llm.min_p` |
| `OPENMONO_REPETITION_PENALTY` | `llm.repetition_penalty` |
| `OPENMONO_WORKSPACE` | `working_directory` |
| `OPENMONO_HOST_WORKSPACE` | `host_working_directory` |
| `OPENMONO_DATA_DIR` | `data_directory` |
| `OPENMONO_MODEL_PRESET` | Activate a preset by name |
| `OPENMONO_PROVIDER` | Activate a provider by name |
| `OPENMONO_VISION_ENABLED` | `inference.vision_enabled` — set to `1` to enable image input (the settings file wins when explicitly set) |
| `OPENMONO_WEB_SEARCH` | `web.search` — set to `1` to force-enable SearXNG routing |
| `OPENMONO_WEB_SCRAPE` | `web.scrape` — set to `1` to force-enable Scrapling routing |
