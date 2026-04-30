# Configuration

Settings are loaded in this order, each layer overriding the previous:

1. Built-in defaults
2. `~/.openmono/settings.json` — user-level
3. `.openmono/settings.json` — project-level (in cwd)
4. `--config <path>` — CLI flag
5. Environment variables — highest priority

---

## CLI flags

Flags passed to `openmono agent` override settings.json and env vars for that session only.

| Flag | Equivalent setting | Description |
|------|--------------------|-------------|
| `--config <path>` | — | Load settings from a specific file |
| `--model <name>` | `llm.model` | Override the model name |
| `--endpoint <url>` | `llm.endpoint` | Override the LLM server endpoint |
| `--api-key <key>` | `llm.apiKey` | Set API key for cloud providers |
| `--verbose` | `verbose` | Show full LLM stream, SSE events, and token counts |
| `--classic` | — | Use classic scrolling terminal instead of TUI |

---

## `openmono config` commands

Read and write settings.json from the terminal without editing the file directly.

```bash
openmono config set llm.endpoint http://localhost:7474
openmono config set llm.model qwen3.6-27b
openmono config get llm.endpoint
openmono config unset llm.apiKey
```

By default these write to the project-level `.openmono/settings.json`. Pass `--global` to write to `~/.openmono/settings.json` instead.

---

## Full example

```jsonc
{
  "llm": {
    "endpoint": "http://localhost:7474",
    "model": "qwen3.6-27b",
    "maxOutputTokens": 16384,
    "temperature": 0.7,
    "topP": 0.8,
    "topK": 20,
    "presencePenalty": 1.5
  },
  "providers": {
    "anthropic": {
      "apiKey": "sk-ant-...",
      "model": "claude-opus-4-7",
      "active": false
    },
    "openai": {
      "apiKey": "sk-...",
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
    "preToolUse": [
      {
        "if": { "tool": "Bash", "inputContains": "rm" },
        "run": "echo '{{tool_name}}: {{tool_input}}' >> audit.log"
      }
    ],
    "postToolUse": [],
    "sessionStart": []
  },
  "mcpServers": {
    "my-server": {
      "command": "npx",
      "args": ["-y", "@my-org/mcp-server"],
      "env": { "MY_KEY": "value" },
      "enabled": true
    }
  },
  "modelPresets": {
    "precise": {
      "temperature": 0.2,
      "topP": 0.9,
      "active": false
    }
  },
  "playbooks": {
    "paths": [".openmono/playbooks/", "~/.openmono/playbooks/"]
  },
  "autoDetectCodeGraph": true,
  "verbose": false,
  "dataDirectory": "~/.openmono"
}
```

---

## `llm`

Controls the active LLM connection and sampling parameters. At startup, `model` and `contextSize` are overridden automatically from the llama.cpp `/props` endpoint.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `endpoint` | string | `http://localhost:7474` | OpenAI-compatible chat endpoint |
| `model` | string | *(from /props)* | Model name sent in requests |
| `apiKey` | string | — | API key for cloud providers |
| `contextSize` | int | *(from /props)* | Context window size in tokens |
| `maxOutputTokens` | int | `16384` | Max tokens per response |
| `temperature` | float | `0.7` | Sampling temperature |
| `topP` | float | `0.8` | Nucleus sampling threshold |
| `topK` | int | `20` | Top-K sampling |
| `presencePenalty` | float | `1.5` | Penalise repeated tokens |
| `minP` | float | `0.0` | Min-P sampling cutoff |
| `repetitionPenalty` | float | `1.0` | Repetition penalty multiplier |

---

## `providers`

Named provider configurations. Set `"active": true` on one to use it as the active provider. Switch mid-session with `/model`.

```jsonc
"providers": {
  "anthropic": { "apiKey": "sk-ant-...", "model": "claude-opus-4-7", "active": true },
  "openai":    { "apiKey": "sk-...",     "model": "gpt-4o",           "active": false },
  "ollama":    { "endpoint": "http://localhost:11434", "model": "llama3", "active": false }
}
```

| Key | Type | Description |
|-----|------|-------------|
| `apiKey` | string | Provider API key |
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
| `deny` | Reject silently |
| `ask` | Always prompt, even if a session-level allow is set |

Permissions from user and project settings are merged additively.

---

## `hooks`

Shell commands triggered at key points in the agent loop. Templates `{{tool_name}}` and `{{tool_input}}` are available in `run`. Timeout: 30 s.

```jsonc
"hooks": {
  "preToolUse": [
    {
      "if": { "tool": "Bash", "inputContains": "rm" },
      "run": "echo '{{tool_name}}: {{tool_input}}' >> audit.log"
    }
  ],
  "postToolUse": [],
  "sessionStart": [
    { "run": "echo 'Session started' >> session.log" }
  ]
}
```

| Hook | When |
|------|------|
| `sessionStart` | Once, when the agent session initialises |
| `preToolUse` | Before each tool call |
| `postToolUse` | After each tool call completes |

The `if` condition is optional. Both `tool` (exact name) and `inputContains` (substring) can be combined.

Hooks from user and project settings are merged additively.

---

## `mcpServers`

MCP servers started as subprocesses on session init. Each server's tools are registered as `mcp__{serverName}__{toolName}`.

```jsonc
"mcpServers": {
  "my-server": {
    "command": "npx",
    "args": ["-y", "@my-org/mcp-server"],
    "env": { "API_KEY": "..." },
    "workingDirectory": "/path/to/dir",
    "enabled": true
  }
}
```

| Key | Required | Description |
|-----|----------|-------------|
| `command` | yes | Executable to run |
| `args` | no | Arguments array |
| `env` | no | Extra environment variables |
| `workingDirectory` | no | Working directory for the subprocess |
| `enabled` | no | Set to `false` to disable without removing (default: `true`) |

**Auto-detected servers**: `code-review-graph` and `graphify` are registered automatically if found in PATH with a graph DB present — no config needed.

---

## `modelPresets`

Named LLM parameter bundles. Activate one via `"active": true` or the `OPENMONO_MODEL_PRESET` env var. The built-in `qwen` preset ships with the default sampling values for Qwen3.6.

```jsonc
"modelPresets": {
  "precise": {
    "temperature": 0.2,
    "topP": 0.95,
    "topK": 40,
    "active": false
  },
  "creative": {
    "temperature": 1.0,
    "topP": 0.9,
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
| `autoDetectCodeGraph` | bool | `true` | Auto-register MCP graph servers if found |
| `verbose` | bool | `false` | Log full LLM stream and tool pipeline |
| `showDetail` | bool | `false` | Show extra detail in TUI panels |
| `dataDirectory` | string | `~/.openmono` | Where sessions, memory, and checkpoints are stored |
| `workingDirectory` | string | cwd | Override the workspace root |
| `hostWorkingDirectory` | string | — | Host path when running inside Docker (used for bind-mount mapping) |

---

## Environment variables

All env vars override their settings.json equivalents regardless of load order.

| Variable | Equivalent setting |
|----------|--------------------|
| `OPENMONO_ENDPOINT` | `llm.endpoint` |
| `OPENMONO_MODEL` | `llm.model` |
| `OPENMONO_API_KEY` | `llm.apiKey` |
| `OPENMONO_CONTEXT_SIZE` | `llm.contextSize` |
| `OPENMONO_MAX_OUTPUT_TOKENS` | `llm.maxOutputTokens` |
| `OPENMONO_TOP_P` | `llm.topP` |
| `OPENMONO_TOP_K` | `llm.topK` |
| `OPENMONO_PRESENCE_PENALTY` | `llm.presencePenalty` |
| `OPENMONO_MIN_P` | `llm.minP` |
| `OPENMONO_REPETITION_PENALTY` | `llm.repetitionPenalty` |
| `OPENMONO_WORKSPACE` | `workingDirectory` |
| `OPENMONO_HOST_WORKSPACE` | `hostWorkingDirectory` |
| `OPENMONO_DATA_DIR` | `dataDirectory` |
| `OPENMONO_MODEL_PRESET` | Activate a preset by name |
| `OPENMONO_PROVIDER` | Activate a provider by name |
