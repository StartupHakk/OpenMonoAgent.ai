# Configuration

## CLI commands

Commands are grouped by which box they run on.

### Any box

| Command | Description |
|---------|-------------|
| `openmono setup [--full\|--agent] [--gpu\|--cpu]` | Install and configure a box role |
| `openmono config <set\|get\|unset> <key> [value]` | Read/write `~/.openmono/settings.json` |
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

For the dual-box walkthrough, see `setup/readme.md`.

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
| `--api-key <key>` | `llm.api_key` | Set API key for cloud providers |
| `--verbose` | `verbose` | Show full LLM stream, SSE events, and token counts |
| `--classic` | — | Use classic scrolling terminal instead of TUI |

---

## `openmono config` commands

Read and write settings.json from the terminal without editing the file directly.

```bash
openmono config set llm.endpoint http://localhost:7474
openmono config set llm.model qwen3.6-27b
openmono config get llm.endpoint
openmono config unset llm.api_key
```

By default these write to the project-level `.openmono/settings.json`. Pass `--global` to write to `~/.openmono/settings.json` instead.

---

## Full example

```jsonc
{
  "llm": {
    "endpoint": "http://localhost:7474",
    "model": "qwen3.6-27b",
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
  "auto_detect_code_graph": true,
  "verbose": false,
  "data_directory": "~/.openmono"
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

Named provider configurations. Set `"active": true` on one to use it as the active provider. Switch mid-session with `/model`.

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

Named LLM parameter bundles. Activate one via `"active": true` or the `OPENMONO_MODEL_PRESET` env var. The built-in `qwen` preset ships with the default sampling values for Qwen3.6.

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

## Top-level flags

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `auto_detect_code_graph` | bool | `true` | Auto-register MCP graph servers if found |
| `verbose` | bool | `false` | Log full LLM stream and tool pipeline |
| `show_detail` | bool | `false` | Show extra detail in TUI panels |
| `data_directory` | string | `~/.openmono` | Where sessions, memory, and checkpoints are stored |
| `working_directory` | string | cwd | Override the workspace root |
| `host_working_directory` | string | — | Host path when running inside Docker (used for bind-mount mapping) |

---

## Environment variables

All env vars override their settings.json equivalents regardless of load order.

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
