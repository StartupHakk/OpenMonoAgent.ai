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
- **ACP layer.** The in-container `AcpServer.Build` hosts Kestrel on port
  7475 (`AcpServerSettings.Port`; `Program.cs` sets `BindAllInterfaces` only
  when `/.dockerenv` exists, so the published host port stays the boundary) with
  routes under `/api/v1/...` (`discovery`, `sessions`, `messages`, `turn` with
  `message`/`permission`/`user_input`/`playbookPermission`/`plan_decision`
  bodies streamed back as SSE, plus `diffs`/`undo`/`redo` and `host` pairing).
  `AcpLockFileWriter` writes `<workspace>/.openmono/agent.lock` (needs
  `HOST_WORKSPACE_PATH`; `HOST_ACP_PORT` is the published port) so editors can
  discover the server.
- **Launchers.** Bash `openmono agent` (`cmd_agent_sandbox`) runs
  `docker compose run --rm --user uid:gid -e HOME -e WORKSPACE <git mounts> agent "$@"`
  after exporting compose variables (`WORKSPACE`, `OPENMONO_ENDPOINT`, web
  settings). The TUI runs in the foreground with ACP in the background on
  7475. The extension's DockerManager instead runs the image with
  `-p 127.0.0.1:{port}:7475 -e HOST_ACP_PORT -e HOST_WORKSPACE_PATH`
  (`scripts/acp-smoke.sh` exercises that shape).
- **Graph technology.** `code-review-graph` (probed with `--version`; when its
  database exists in `~/.openmono/graph-db`, `<workdir>/.code-review-graph`, or
  `~/.code-review-graph`, it is registered as the `code-graph` MCP server) and
  the `graphify` CLI (invoked through the in-container Bash tool against
  `graphify-out/graph.json`). Both run inside the agent container.
- **VS Code extension.** `StartupHakk.openmono-agent` (VS Code 1.85+, also
  works in Cursor, from the Marketplace). It speaks the ACP HTTP API, the same
  API the HostBridge notes drive ("the same API as VS Code").

## What Windows changes (all in `win/`)

- **Launcher** (`win/openmono.ps1` over `openmono-win agent-plan`, built by
  `WindowsContainerStack.Build`): the same `docker compose run --rm` of the
  same `agent` service as `cmd_agent_sandbox`, the same `-e` and git mounts,
  the same compose variables, plus:
  - `OPENMONO_ENDPOINT=http://host.docker.internal:7474` (the macOS
    native-inference rewrite), so the container reaches host Foundry;
  - `--user 1000:1000` (Windows has no uid; the image makes `/home/agent`
    1777 and `/etc/passwd` writable for any uid, so the sandbox stays
    non-root);
  - `HOST_WORKSPACE_PATH=<Windows path>` as the extension's DockerManager
    sets it, so `agent.lock` names the folder VS Code has open;
  - `OPENMONO_MODEL=<served Foundry id>` and `CTX_SIZE=<context for this GPU>`;
  - with `--acp-only`, `-p 127.0.0.1:{port}:{port}` and `HOST_ACP_PORT`
    (the `AgentContainer` shape). The default TUI run publishes nothing, as
    on Linux.
- **Endpoint mapping** (`WindowsContainerEndpoint`): loopback roots map to
  `host.docker.internal` with the port kept and `/v1` paths preserved; remote
  hosts pass through. Mirrors `AgentContainer.MapEndpoint`.
- **ACP** (`WindowsAcpWiring`): no server code duplicated. Discovery is at
  `http://127.0.0.1:7475/api/v1/discovery`; `openmono doctor` reports it.
- **Graph** (`WindowsGraphArtifacts`): the same artifact paths upstream checks,
  read on the host copy of the mounted workspace, reported by `doctor`. The
  tools run in-container, unchanged; `~/.code-review-graph` is created up
  front so Docker does not create the bind source itself.
- **Host shell** (`WindowsShellSelector`): host controls are PowerShell
  (`pwsh.exe`, then `powershell.exe`). The agent's Bash tool is untouched
  Linux bash inside the container.
- **Host guardrails** (`WindowsGuardrails`): Windows-destructive command
  shapes, ready for the host-bridge `host.deny_list` once host mode exists on
  Windows. The agent pipeline is unchanged.
- **Vision** (`WindowsVisionConfig`, `win/docs/VISION.md`): the probe outcome
  flows via `OPENMONO_VISION_ENABLED`; `--vision` sessions use the Foundry
  vision alias. Images are never silently dropped.

## Ports on a Windows machine

| Service | Address | Source |
|---|---|---|
| Inference (Foundry, host) | `http://127.0.0.1:7474` | `FoundryConstants.DefaultLoopbackEndpoint` |
| Inference (from containers) | `http://host.docker.internal:7474` | `WindowsContainerEndpoint` |
| Agent ACP server | `http://127.0.0.1:7475` | `AcpServerSettings.Port`, `WindowsAcpWiring` |

## Upstream changes needed

None for the launcher path. For Spencer, three optional follow-ups:

1. `AgentContainer.MapEndpoint` (HostBridge) has macOS and Linux branches; a
   Windows branch identical to macOS would let `openmono agent --host` work
   once host mode is ported. `win/` already has the same mapping.
2. The VS Code extension's DockerManager starts its own container. Its
   source is not in this repo, so whether it rewrites a loopback
   `llm.endpoint` for the container is unverified. If it does not, a
   container it starts on Windows dials its own loopback instead of Foundry.
   Until verified, run `openmono agent --acp-only` from the project folder
   and let the extension attach on 7475.
3. Per-turn vision model routing (`inference.vision_model`), described in
   `win/docs/VISION.md`.

## Manual check (Windows)

```powershell
.\win\openmono.ps1 setup                       # prerequisites, agent image, Foundry up, doctor
cd C:\path\to\project
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 doctor             # every check ok or info; "container route" must be ok
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent --acp-only   # ACP published on 127.0.0.1:7475
curl.exe http://127.0.0.1:7475/api/v1/discovery  # host_workspace is the Windows path
Get-Content .openmono\agent.lock                 # same port and workspace
# open the folder in VS Code, open the OpenMono Agent panel, send a turn
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 stop --agent       # remove the ACP container; Foundry keeps serving
```
