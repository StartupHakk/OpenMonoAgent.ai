# ACP, graph, and VS Code extension on Windows

The Windows port runs the whole OMA stack with Windows hosting the inference
and the existing Linux container running the agent: Foundry Local inference,
the agent in Docker, ACP, graph tools, and the VS Code extension against that
local stack. The Mac/Linux paths are untouched; Windows install and host
control code lives in `win/`. oma-bot stays Linux, same as the core agent.

## How each piece works today (Mac/Linux, from the real code)

- **Inference contract.** `OpenMono.Llm.OpenAiCompatClient` posts to
  `{Endpoint}/v1/chat/completions`; `OpenMono.Llm.ModelCatalog` reads
  `{Endpoint}/v1/models`; `Program.cs` polls `{Endpoint}/health` and
  `{Endpoint}/props` (llama.cpp) with `/v1/models` as the fallback, and
  `ConfigLoader` strips one trailing `/v1`. Default root:
  `http://localhost:7474`. Env overrides: `OPENMONO_ENDPOINT`,
  `OPENMONO_MODEL`, `OPENMONO_API_KEY`.
- **Agent container.** `OpenMono.HostBridge.AgentContainer` starts the
  `agent` compose service with `-p 127.0.0.1:{hostPort}:7475` (ACP),
  `-v workdir:/workspace`, the `~/.openmono` mount, and env including
  `-e OPENMONO_ENDPOINT={mapped}` (loopback rewritten per OS), `-e HOST_ACP_PORT`,
  `-e OPENMONO_HOST_OS`, plus a bare `-e OPENMONO_API_KEY` whose value travels
  in the child process environment, never on the command line. Linux maps
  loopback to the `llama-server` compose service; macOS maps it to
  `host.docker.internal`.
- **ACP layer.** The in-container `AcpServer.Build` hosts Kestrel on
  `localhost:7475` (`AcpServerSettings.Port`, `BindAllInterfaces = false`) with
  routes under `/api/v1/...` (`discovery`, `sessions`, `messages`, `turn` with
  `message`/`permission`/`user_input`/`playbookPermission`/`plan_decision`
  bodies streamed back as SSE, plus `diffs`/`undo`/`redo` and `host` pairing).
  `AcpLockFileWriter` writes `<workspace>/.openmono/agent.lock` so editors can
  discover the server.
- **Graph technology.** `code-review-graph` (probed with `--version`; when its
  database exists in `~/.openmono/graph-db`, `<workdir>/.code-review-graph`, or
  `~/.code-review-graph`, it is registered as the `code-graph` MCP server) and
  the `graphify` CLI (invoked through the in-container Bash tool against
  `graphify-out/graph.json`). Both run inside the agent container.
- **VS Code extension.** `StartupHakk.openmono-agent` (VS Code 1.85+, also
  works in Cursor, from the Marketplace). It speaks the ACP HTTP API, the same
  API the HostBridge notes drive ("the same API as VS Code").

## What Windows changes (all in `win/`)

- **Endpoint mapping** (`WindowsContainerEndpoint`): loopback inference roots
  map to `http://host.docker.internal:{port}` (the Docker Desktop route to the
  Windows host, same as macOS), `/v1`-prefixed paths preserved, remote hosts
  untouched. Mirrors `AgentContainer.MapEndpoint` for Windows; no upstream edit.
- **Stack config** (`WindowsContainerStack` + `win/scripts/start-stack.ps1` /
  `stop-stack.ps1`): same compose service, same ACP publish on loopback, same
  volume and env shape as `AgentContainer.EnsureAsync`, except
  `OPENMONO_ENDPOINT` points at host Foundry, `OPENMONO_HOST_OS=Windows`, no
  `--user uid:gid`, and no llama-server/caddy profiles (inference is
  host-native). API keys stay out of the command line.
- **ACP** (`WindowsAcpWiring`): no server code duplicated. The host needs the
  published address (`http://127.0.0.1:7475`, discovery at
  `/api/v1/discovery`) and the lock path `<workspace>/.openmono/agent.lock`.
- **Graph** (`WindowsGraphArtifacts`): same artifact paths upstream checks
  (`graph-db` dirs, `graphify-out/graph.json` or `GRAPH_REPORT.md`), evaluated
  on the host copy of the mounted workspace. The tools themselves run
  in-container, unchanged.
- **Host shell** (`WindowsShellSelector`): install, bootstrap, first-run,
  Foundry lifecycle, and stack management are PowerShell (`pwsh.exe`, then
  `powershell.exe`). The agent's Bash tool is untouched Linux bash.
- **Host guardrails** (`WindowsGuardrails`): Windows-destructive shapes
  (`format`, `diskpart`, `bcdedit`, `reg delete`, recursive deletes of `C:\`
  and `C:\Windows`) as candidates for the host daemon's `host.deny_list`,
  which is checked first. The agent pipeline is unchanged.
- **Vision** (`WindowsImagePolicy`, `WindowsVisionConfig`, `win/docs/VISION.md`):
  the model name for image turns flows via `OPENMONO_MODEL`; the vision flag
  flows via `OPENMONO_VISION_ENABLED`. Images are never silently dropped.

## Ports on a Windows machine

| Service | Address | Source |
|---|---|---|
| Inference (Foundry, host) | `http://127.0.0.1:7474` | `FoundryConstants.DefaultLoopbackEndpoint` |
| Inference (from containers) | `http://host.docker.internal:7474` | `WindowsContainerEndpoint` |
| Agent ACP server | `http://127.0.0.1:7475` | `AcpServerSettings.Port`, `WindowsAcpWiring` |

## Upstream changes needed: none

One candidate tiny upstream edit is worked around in `win/` instead and listed
here for Spencer:

1. `AgentContainer.MapEndpoint` has macOS and Linux branches. Windows hosts
   need the `host.docker.internal` branch (same as macOS). `win/` implements
   the identical mapping in `WindowsContainerEndpoint`; a future upstream
   `OperatingSystem.IsWindows()` branch would make the wrapper a thin call,
   changing no behavior on other platforms.

## Manual check (Windows)

```
.\win\scripts\bootstrap.ps1     # Foundry CLI, CUDA/cuDNN visibility
.\win\scripts\start-stack.ps1   # Docker check, Foundry adopt, agent container start
curl http://127.0.0.1:7475/api/v1/discovery   # version, agent_id, status ready
curl http://127.0.0.1:7474/v1/models          # Foundry serving (vision probe runs at host start)
.\win\scripts\stop-stack.ps1    # remove the agent container; Foundry keeps serving
```
