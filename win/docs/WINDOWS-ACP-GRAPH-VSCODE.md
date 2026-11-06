# ACP, graph, and VS Code extension on Windows

The Windows port runs the whole OMA stack natively on Windows: Foundry Local
inference, the agent, the ACP layer, graph tools, and the VS Code extension
against that local stack. The Mac/Linux paths are untouched; everything
Windows-specific lives in `win/`.

## How each piece works today (Mac/Linux, from the real code)

- **Inference contract.** `OpenMono.Llm.OpenAiCompatClient` posts to
  `{Endpoint}/v1/chat/completions`; `OpenMono.Llm.ModelCatalog` reads
  `{Endpoint}/v1/models`; `Program.cs` polls `{Endpoint}/health` and
  `{Endpoint}/props` (llama.cpp) with `/v1/models` as the fallback, and
  `ConfigLoader` strips one trailing `/v1`. Default root:
  `http://localhost:7474`. Env overrides: `OPENMONO_ENDPOINT`,
  `OPENMONO_MODEL`, `OPENMONO_API_KEY`.
- **ACP layer.** `OpenMono.Acp.AcpServer.Build` hosts Kestrel on
  `localhost:7475` (`AcpServerSettings.Port`, default 7475,
  `BindAllInterfaces = false` by default) with routes under `/api/v1/...`
  (`discovery`, `sessions`, `sessions/{id}`, `messages`, `turn` with
  `message`/`permission`/`user_input`/`playbookPermission`/`plan_decision`
  bodies streamed back as SSE, plus `diffs`/`undo`/`redo` and `host`
  pairing). `AcpLockFileWriter` writes `<workspace>/.openmono/agent.lock`
  with agent id, host workspace, and port so editors can discover the server.
  The two Docker-specific assumptions are the default `"/workspace"` mount
  and the required `HOST_WORKSPACE_PATH` env var (normally set by the
  extension's DockerManager), plus `HOST_ACP_PORT`/`ACP_AGENT_ID` overrides.
- **Graph technology.** Two tools, both already shell-agnostic in logic:
  `code-review-graph` (probed with `--version`; when its database exists in
  `~/.openmono/graph-db`, `<workdir>/.code-review-graph`, or
  `~/.code-review-graph`, it is registered as the `code-graph` MCP server
  with command `code-review-graph args ["serve"]`) and the `graphify` CLI
  (docs invoke it through Bash against `graphify-out/graph.json`).
- **VS Code extension.** The extension is not in this repo; it speaks the ACP
  HTTP API above (same API as `HostBridge`, which notes it drives "the same
  API as VS Code"). Anything reachable over that API on Mac/Linux works the
  same on Windows once the server binds locally.

## What Windows changes (all in `win/`)

- **ACP** (`WindowsAcpHost`): sets `HOST_WORKSPACE_PATH` to the real Windows
  workspace when the extension has not set it, honors `HOST_ACP_PORT`,
  enables the server on 127.0.0.1:7475, and constructs the real
  `AcpLockFileWriter` with the Windows workspace as the mount so `agent.lock`
  lands next to the open code. The server, routes, SSE, sessions, diffs, and
  host pairing are 100 percent reused; serving still goes through the same
  `openmono --acp-port` entry with these env vars set.
- **MCP shims** (`WindowsMcpLauncher`): upstream `McpClient` spawns
  `config.Command` with `UseShellExecute = false`, which cannot launch
  `.cmd`/`.bat` shims. The resolver maps `code-review-graph` (and any MCP
  command) to its `.exe` when present, or wraps `.cmd`/`.bat` in
  `cmd.exe /c`, so the `code-graph` MCP server starts on Windows.
- **Graph** (`WindowsGraphHost`): probes `code-review-graph.exe` / `.cmd` /
  bare name with `--version`, and routes `graphify` invocations through
  `WindowsShellSelector` (pwsh, powershell, cmd) instead of `/bin/bash`.
- **VS Code extension**: point `StartupHakk.openmono-agent` (VS Code 1.85+,
  also works in Cursor, from the Marketplace) at `http://127.0.0.1:7475`.
  Discovery reads
  `<workspace>/.openmono/agent.lock` (agent id, port, host workspace); turns
  POST to `/api/v1/sessions/{id}/turn` with `{"message": "..."}` and stream
  the SSE response. Image attachments follow `win/docs/VISION.md`.

## Ports on a Windows machine

| Service | Default | Source |
|---|---|---|
| Inference (Foundry, OpenAI-compatible) | `http://127.0.0.1:7474` | `FoundryConstants.DefaultLoopbackEndpoint` |
| Agent ACP server | `http://127.0.0.1:7475` | `AcpServerSettings.Port`, `WindowsAcpHost` |

## Upstream changes needed: none so far

Two candidate tiny upstream edits are worked around in `win/` instead; they
are listed here for Spencer in case we want them later:

1. `AcpLockFileWriter` requires `HOST_WORKSPACE_PATH` and defaults the mount
   to `"/workspace"`. Windows sets the env var and passes the mount
   explicitly. A future upstream default (fall back to the working directory
   when the env var is unset) would remove the workaround, not change behavior.
2. `McpClient` spawns commands with `UseShellExecute = false`. Windows
   resolves wrappers before spawn. A future upstream `cmd.exe /c` fallback
   for `.cmd`/`.bat` would make the resolver unnecessary on Windows.

## Manual check (Windows)

```
$env:HOST_WORKSPACE_PATH = "C:\Users\you\proj"   # only if the extension did not set it
openmono --acp-port 7475                          # ACP on 127.0.0.1:7475
curl http://127.0.0.1:7475/api/v1/discovery       # version, agent_id, status ready
curl http://127.0.0.1:7474/v1/models              # Foundry serving (vision probe runs at host start)
code-review-graph --version                       # graph tool present
```
