# OpenMono on Windows (Foundry Local)

The same OpenMonoAgent.ai, on Windows. The agent runs in its unchanged Linux
Docker container, exactly as on Mac/Linux. The one intentional platform
difference is inference: Microsoft Foundry Local on the Windows host at
`http://127.0.0.1:7474` instead of llama.cpp, reached from the container as
`http://host.docker.internal:7474`. Everything under `win/` is install and
host control, in PowerShell.

## Quick start

Prerequisites: Windows 11, Docker Desktop (WSL 2 backend), .NET SDK 10.0.403+
(same as `install.ps1`), an NVIDIA GPU with 24 GB VRAM for `qwen3.8-27b`
(smaller GPUs get a smaller model), and CUDA plus cuDNN on `PATH` (Foundry
Local does not install them).

```powershell
.\win\openmono.ps1 setup     # Foundry CLI (winget), CUDA check, agent image, start inference, doctor
cd C:\path\to\project
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent              # the agent TUI, sandboxed in its container
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent --acp-only   # ACP on 127.0.0.1:7475 for the VS Code extension
```

| Command | What it does |
| --- | --- |
| `setup` | Installs or checks prerequisites, builds `openmono-win` and the agent image, starts inference, runs `doctor` |
| `start` | Adopts a running Foundry daemon or starts one on 7474, loads the model for this GPU |
| `stop` | Stops Foundry only if OpenMono started it (`--force` to override). `--agent` or `--all` also removes agent containers |
| `status` | Inference endpoint, served models, and agent containers |
| `agent [args]` | `docker compose run --rm ... agent [args]`, the same launch as bash `openmono agent`. `--vision` serves the Foundry vision model for the session |
| `logs` | `foundry server logs --follow` |
| `doctor` | PowerShell, Docker, compose, agent image, Foundry CLI, GPU, CUDA and cuDNN, server health, the container-to-host route, ACP, graph |

Runs on PowerShell 7 (`pwsh.exe`) or Windows PowerShell 5.1 (`powershell.exe`).

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
  gate, GPU/VRAM sizing, catalog alias resolution, adopt-or-start on
  `127.0.0.1:7474`, served model id resolution, health checks, vision probe.
- `src/OpenMono.Win.AgentHost`: the container run plan (mirrors
  `cmd_agent_sandbox`), endpoint mapping to `host.docker.internal`, ACP wiring,
  PowerShell shell selection, host guardrail patterns, vision flag mapping,
  graph artifact checks.
- `src/OpenMono.Win.Launcher`: `openmono-win`, the tested decision logic
  `openmono.ps1` calls (`inference up|down|status`, `agent-plan`, `doctor`).
- `tests/OpenMono.Win.Tests`: GPU-free tests with fakes for CLI, GPU, and HTTP.
- `docs/BUILD-PLAN.md` (plan and adaptation notes), `docs/VISION.md`,
  `docs/WINDOWS-ACP-GRAPH-VSCODE.md`.

## Branch rules

This code lives only on the `win` branch. Never touch `main`, never merge to
`main`, never open a pull request. Do not use the old `Windows` branch or the
legacy `windows/` folder as a starting point.
