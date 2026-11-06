# OpenMono on Windows (Foundry Local)

The same OpenMonoAgent.ai, on Windows. The agent runs in its unchanged Linux
Docker container, exactly as on Mac/Linux. The one intentional platform
difference is inference: Microsoft Foundry Local on the Windows host at
`http://127.0.0.1:7474` instead of llama.cpp, reached from the container as
`http://host.docker.internal:7474`. Everything under `win/` is install and
host control, in PowerShell.

## Quick start

Prerequisites: Windows 11, .NET SDK 10.0.403+ (same as `install.ps1`), winget
(`setup` uses it to install Foundry Local and, when missing, Docker Desktop
with WSL 2), an NVIDIA GPU (24 GB VRAM for `qwen3.8-27b`; smaller cards get
the Qwen3.5 ladder below), and CUDA plus cuDNN on `PATH` (Foundry Local does
not install them).

```powershell
.\win\openmono.ps1 setup     # Foundry CLI and Docker Desktop (winget), CUDA check, agent image, start inference, doctor
cd C:\path\to\project
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent              # the agent TUI, sandboxed in its container
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent --acp-only   # ACP on 127.0.0.1:7475 for the VS Code extension
```

| Command | What it does |
| --- | --- |
| `setup` | Installs or checks prerequisites (Foundry Local; Docker Desktop and WSL 2 when missing, then starts Docker Desktop and waits for its engine), checks the model ladder against the Foundry catalog (`catalog check`), builds `openmono-win` and the agent image, starts inference, runs `doctor`. Prints numbered steps for what only you can finish (restart, first-launch agreement, sign-out) |
| `start` | Adopts a running Foundry daemon or starts one on 7474, loads the model for this GPU |
| `stop` | Stops Foundry only if OpenMono started it (`--force` to override). `--agent` or `--all` also removes agent containers |
| `status` | Inference endpoint, served models, and agent containers |
| `agent [args]` | `docker compose run --rm ... agent [args]`, the same launch as bash `openmono agent`. `--vision` starts an image session on this machine's model and fails loudly if it refuses images (no second model is loaded) |
| `catalog [check]` | `catalog` lists the Foundry Local catalog from https://foundrylocal.ai/api/models (`--device cuda\|cpu\|webgpu`) and marks the ladder rungs. `catalog check` validates every ladder alias against that API and `foundry model list`, then names this machine's model. `--refresh` skips the one-hour cache, `--strict` fails if the public catalog lacks any rung, `--json` for scripts |
| `logs` | `foundry server logs --follow` |
| `doctor` | PowerShell, Docker, compose, agent image, Foundry CLI, GPU, model catalog, CUDA and cuDNN, server health, the container-to-host route, ACP, graph |
| `diag` | Writes a zip for bug reports: GPU and VRAM, Foundry version, status and last server log, CUDA `PATH` entries, doctor output, inference state, settings with secrets and the home path redacted |

Runs on PowerShell 7 (`pwsh.exe`) or Windows PowerShell 5.1 (`powershell.exe`).

## Models

One model per machine, picked by dedicated VRAM. Every alias is a real entry in
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

A 24 GB+ card always serves `qwen3.8-27b`: never a different alias for images,
`--vision`, or a missing catalog entry. Below 24 GB, if a rung is missing from
the catalog the next smaller Qwen3.5 model is used and the launcher says which
alias was skipped. `--model ALIAS` on `inference up` overrides the pick, and is
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
