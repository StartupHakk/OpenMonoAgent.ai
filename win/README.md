# OpenMono on Windows (Foundry Local)

The same OpenMonoAgent.ai, on Windows. The agent runs in its unchanged Linux
Docker container, exactly as on Mac/Linux. The one intentional platform
difference is inference: Microsoft Foundry Local on the Windows host at
`http://127.0.0.1:7474` instead of llama.cpp, reached from the container as
`http://host.docker.internal:7474`. Everything under `win/` is install and
host control, in PowerShell.

## Quick start

Prerequisites: Windows 11, winget (`setup` uses it to install Git, the .NET
SDK, Foundry Local and Docker Desktop with WSL 2 when they are missing), and
for GPU inference an NVIDIA GPU (24 GB VRAM for
`qwen3.8-27b`; smaller cards get the Qwen3.5 ladder below) with CUDA plus
cuDNN on `PATH` (Foundry Local does not install them). Without an NVIDIA GPU,
OpenMono runs `qwen3.5-2b` on the CPU.

Git and the .NET SDK 10.0.403+ (the version in `win/global.json`, same as
`install.ps1`) do not need to be installed first. `setup` (and
`win\scripts\bootstrap.ps1`) checks for them on the host and, when one is
missing or too old, installs it silently with winget (`Git.Git`,
`Microsoft.DotNet.SDK.10`). Without winget it uses the official installers
instead, per user and without administrator rights: MinGit from the latest Git
for Windows release into `%LOCALAPPDATA%\Programs\MinGit`, and
`dotnet-install.ps1 -Channel 10.0` into `%LOCALAPPDATA%\Microsoft\dotnet`.
The new directories are added to the user `PATH` and to the running session,
so setup carries on in the same window. An install that fails stops setup
with the installer's exit code. On a machine without Git, download the `win`
branch as a ZIP from GitHub, run `Get-ChildItem -Recurse | Unblock-File` in
the unpacked folder, then run `setup`.

```powershell
.\win\openmono.ps1 setup     # Git, .NET SDK, Foundry CLI, Docker Desktop (winget), CUDA check, agent image, start inference, doctor
cd C:\path\to\project
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent              # the agent TUI, sandboxed in its container
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent --acp-only   # ACP on 127.0.0.1:7475 for the VS Code extension
```

| Command | What it does |
| --- | --- |
| `setup` | Installs or checks prerequisites (Git and the .NET SDK; Foundry Local; Docker Desktop and WSL 2 when missing, then starts Docker Desktop and waits for its engine), checks the model ladder against the Foundry catalog (`catalog check`), builds `openmono-win` and the agent image, starts inference, runs `doctor`. Prints numbered steps for what only you can finish (restart, first-launch agreement, sign-out) |
| `start` | Adopts a running Foundry daemon or starts one on 7474, loads the model for this GPU. A model the daemon already served that is not that model (for example a DeepSeek-R1-Distill OpenVINO model on an Intel machine) is unloaded and replaced, and the log says so; `--model` and `llm.model` in `settings.json` are honored |
| `stop` | Stops Foundry only if OpenMono started it (`--force` to override). `--agent` or `--all` also removes agent containers |
| `status` | Inference endpoint, served models, and agent containers |
| `agent [args]` | `docker compose run --rm ... agent [args]`, the same launch as bash `openmono agent`. Files under `docker\` are kept on LF line endings first, and an agent image built from CRLF files is rebuilt (see Line endings below). `--vision` starts an image session on this machine's model and fails loudly if it refuses images (no second model is loaded) |
| `catalog [check]` | `catalog` lists the Foundry Local catalog from https://foundrylocal.ai/api/models (`--device cuda\|cpu\|webgpu`) and marks the ladder rungs. `catalog check` validates every ladder alias against that API and `foundry model list`, then names this machine's model. `--refresh` skips the one-hour cache, `--strict` fails if the public catalog lacks any rung, `--json` for scripts |
| `logs` | `foundry server logs --follow` |
| `doctor` | PowerShell, Git, .NET SDK, Docker, compose, agent image, Foundry CLI, GPU, model catalog, CUDA and cuDNN, server health, the container-to-host route, ACP, graph |
| `diag` | Writes a zip for bug reports: GPU and VRAM, Foundry version, status and last server log, CUDA `PATH` entries, doctor output, inference state, settings with secrets and the home path redacted |

Runs on PowerShell 7 (`pwsh.exe`) or Windows PowerShell 5.1 (`powershell.exe`).

### Requests to Foundry Local

The agent knows it talks to Foundry (the launcher passes
`OPENMONO_LLM_BACKEND=foundry`) and sends standard OpenAI chat fields only:
`model`, `messages`, `temperature`, `top_p`, `max_tokens`, `stream`, and
`tools`/`tool_choice`. The llama.cpp extensions it sends on Mac/Linux (`top_k`,
`min_p`, `presence_penalty`, `repetition_penalty`, `chat_template_kwargs`,
`reasoning_effort`, `stream_options`, ...) are left out, because Foundry
answers them with a 400 (a nonzero `presence_penalty` or `frequency_penalty`
included).

Some Foundry variants cannot take structured tools even when the catalog says
"Tools: Yes" (`qwen3.5-2b-generic-gpu:4`, `qwen3.5-9b-cuda-gpu:4`). Foundry then
fails with `structured tool calling requires model-published opening and
closing tool markers`, as an HTTP 400, an HTTP 500, or an error in the stream.
The agent does not resend that request. It retries the turn once without
`tools`/`tool_choice`, describes the tools in the system prompt in Qwen XML
form (`<tool_call><function=name><parameter=key>value</parameter></function></tool_call>`),
and reads tool calls back from the reply text (`<function=...>`,
`<tool_call>...</tool_call>`, `<tools>...</tools>`). Tool results go back as
`<tool_response>` text in a user message, never as `role: tool`. That mode lasts
for the rest of the session. When the catalog entry of the served variant id
already says it cannot call tools, the launcher passes `OPENMONO_TOOL_CALLING=0`
and the agent starts in that mode.

When Foundry rejects a request, the TUI shows Foundry's reply and points at
`.\win\openmono.ps1 status`, `start` and `foundry server logs` on the host.
Other request errors are not resent with the same body either. "Foundry Local is
not reachable" is shown only when no connection could be made.

### Line endings

Everything the Linux containers run or read (`docker\entrypoint.sh`, the
Dockerfiles, compose files, Caddy and SearXNG configs, shell scripts) must have
LF line endings. A CRLF entrypoint fails at container start with
`/usr/bin/env: 'bash\r': No such file or directory`. The repository's
`.gitattributes` pins these files to LF in every checkout, whatever
`core.autocrlf` says. For checkouts made before that, `setup` and `agent`
rewrite any CRLF file under `docker\` to LF before Docker uses it, and
`agent` rebuilds an `openmono-agent` image whose entrypoint still has CR
(checked once per image).

## Models

One model per machine, picked by dedicated NVIDIA VRAM. Every alias is a real entry in
the Foundry Local catalog (https://foundrylocal.ai/models), checked through
https://foundrylocal.ai/api/models and `foundry model list` before anything
downloads. If an alias is missing, nothing is downloaded and the launcher says
so.

| Dedicated VRAM | Model | Input | Context |
| --- | --- | --- | --- |
| 24 GB and up | `qwen3.8-27b` | text | 64K (up to 262K at 32 GB+) |
| 12 GB | `qwen3.5-9b` | text and images | 64K |
| 8 GB | `qwen3.5-4b` | text and images | 64K |
| 4 GB | `qwen3.5-2b` | text and images | 64K |
| under 4 GB | `qwen3.5-0.8b` (last resort) | text and images | 64K |
| no NVIDIA GPU (CPU only) | `qwen3.5-2b`, its generic CPU variant (for example `qwen3.5-2b-generic-cpu:3`) when Foundry offers one | text and images | 64K |

A 24 GB+ card always serves `qwen3.8-27b`: never a different alias for images,
`--vision`, or a missing catalog entry. Below 24 GB, if a rung is missing from
the catalog the next smaller Qwen3.5 model is used and the launcher says which
alias was skipped. A CPU-only machine never tries `qwen3.8-27b`, `qwen3.5-9b`,
or `qwen3.5-4b`; its only step down is `qwen3.5-0.8b`. The CPU variant id
comes from the CPU catalog (cached for an hour); when it is not listed, the
alias is loaded and Foundry picks the variant. `--model ALIAS` on `inference up` overrides the pick, and is
refused unless the alias is in the catalog. `OPENMONO_FOUNDRY_CATALOG_URL`
points the catalog lookup elsewhere (`off` disables it; `foundry model list`
still applies).

## Principles

This is not a separate app. The agent loop, its tools, the PermissionEngine,
ACP on 7475, graph tools (code-review-graph, graphify), the VS Code extension,
`settings.json` keys (`llm.endpoint`, `llm.model`, `inference.vision_enabled`),
and environment overrides (`OPENMONO_ENDPOINT`, `OPENMONO_MODEL`,
`OPENMONO_VISION_ENABLED`) behave as on Mac/Linux. Local-first: Foundry and
ACP bind loopback only, and no prompt, image, or file content leaves the
machine. A remote `llm.endpoint` is used as-is, as the bash launcher does.
oma-bot stays Linux.

## Layout

- `openmono.ps1`: the Windows launcher (host controls only).
- `scripts/bootstrap.ps1`, `scripts/check-cuda.ps1`: Foundry CLI install and GPU/CUDA visibility.
- `src/OpenMono.Win.Foundry`: Foundry Local wrapper. CLI detection and version
  gate, the model ladder and GPU/VRAM sizing, the Foundry catalog API client
  and alias resolution, adopt-or-start on
  `127.0.0.1:7474`, served model id resolution, health checks, vision probe.
- `src/OpenMono.Win.AgentHost`: the container run plan (mirrors
  `cmd_agent_sandbox`), endpoint mapping to `host.docker.internal`, ACP wiring,
  PowerShell shell selection, host guardrail patterns, vision flag mapping,
  graph artifact checks.
- `src/OpenMono.Win.Launcher`: `openmono-win`, the tested decision logic
  `openmono.ps1` calls (`inference up|down|status`, `catalog`, `agent-plan`, `doctor`).
- `tests/OpenMono.Win.Tests`: GPU-free tests with fakes for CLI, GPU, and HTTP.
- `docs/BUILD-PLAN.md` (plan and adaptation notes), `docs/VISION.md`,
  `docs/WINDOWS-ACP-GRAPH-VSCODE.md`.

## Branch rules

This code lives only on the `win` branch. Never touch `main`, never merge to
`main`, never open a pull request. Do not use the old `Windows` branch or the
legacy `windows/` folder as a starting point.
