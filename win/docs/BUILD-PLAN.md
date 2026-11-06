# OpenMonoAgent for Windows: plan and decisions

The `win` branch of OpenMonoAgent.ai runs the existing agent on Windows, with
all Windows-specific code in the `/win/` folder at the repo root. It does not
start from the older `Windows` branch or the `windows/` folder.

## Goal

A Windows developer can clone the `win` branch, run one setup command, and use
the OMA agent against local inference, with no cloud model. The Mac and Linux
paths stay unchanged, and `main` is not touched.

## Decisions

1. Containers (Spencer): the agent stays in Docker. It runs in the unchanged
   Linux agent container (bash inside); oma-bot stays Linux too. `win/` is
   Windows install and host control in PowerShell: Docker Desktop checks,
   llama.cpp lifecycle, stack start/stop, and the endpoint mapping containers
   use to reach the host (`host.docker.internal:7474`). There is no native
   Windows in-process agent and no WinUI app; the Windows UX is the agent TUI
   in the container and the VS Code extension over ACP.
2. Inference is llama.cpp, as on Linux. llama-server runs natively on the
   Windows host from the official llama.cpp Windows release (Docker Desktop
   GPU passthrough is not needed), bound to `127.0.0.1:7474`. Backend order:
   CUDA, then Vulkan, then CPU. The CUDA build matching the driver's CUDA
   version is used with its `cudart` zip, so no CUDA toolkit install is
   needed. A CUDA build that crashes at start falls back to the next backend.
3. Model ladder: one model per machine by dedicated NVIDIA VRAM, in
   `scripts/models.json` (`platforms.windows.tiers`, next to the Linux and
   macOS tables). 24 GB and up is the Linux 24 GB tier: the same GGUF
   (`Qwen3.8-27B-UD-Q4_K_M`), context (`CTX_24G` 196608, `CTX_VISION` 172032
   with the projector) and q8_0 KV cache as `scripts/install.sh` (lines
   73-78 for the contexts, 380-470 for tier detection). Below 24 GB the rungs
   are Qwen3.5 GGUFs of the same family: 12 GB about 9B
   (`Qwen3.5-9B-UD-Q4_K_XL`), 8 GB about 4B, 4 GB about 2B, under 4 GB about
   0.8B as the last resort. No NVIDIA GPU: about 2B on the CPU build with a
   q8_0 KV cache, like the Linux CPU path. Every rung has an `mmproj`
   projector, so vision works on every rung.
4. llama-server flags mirror the compose services `install.sh` writes (GPU:
   lines 890-915, CPU: lines 1062-1088): `--alias`, `--ctx-size`,
   `--flash-attn on`, `--cache-type-k/v`, `--batch-size 2048`,
   `--ubatch-size 1024`, `--parallel 1`, `--jinja`, `--reasoning off`,
   `--metrics`, `--n-gpu-layers 99` on a GPU, `--threads-batch` on the CPU,
   the `--mmproj` image token flags, and `--api-key` when `LLAMA_API_KEY` is
   set. The host binds `127.0.0.1` instead of `0.0.0.0`.
5. Lifecycle: `start` reuses the llama-server it started when the build, model
   and flags match, and restarts it otherwise. A server on 7474 that OpenMono
   did not start is reused only when it already serves the selected model;
   otherwise `start` fails before downloading anything and names what that
   server serves. Only a build that exits while loading falls back to the
   next backend; a server still loading is waited for (up to 10 minutes). `stop` only
   stops a server OpenMono started (`--force` to override). State lives in
   `~/.openmono/win/inference.json`, the llama.cpp builds in
   `~/.openmono/win/llama.cpp/`, the GGUFs in the repository's `models/`
   folder (as on Linux).
6. Endpoint host spelling: the host binds `127.0.0.1:7474`. OMA defaults use
   `localhost:7474`. These are the same loopback interface, and OMA config
   normalization treats them as equivalent roots.
7. VRAM classes: cards report slightly under their marketed size (RTX 4090:
   24564 MiB, RTX 5090: 32607 MiB), so each class is matched from 1 GiB below
   nominal.
8. Host launcher: `win/openmono.ps1` (pwsh 7 or Windows PowerShell 5.1) has
   the bash launcher's subcommands (`setup`, `start`, `stop`, `status`,
   `agent`, `logs`, `doctor`) plus `models` and `diag`. Decision logic lives in
   the tested `OpenMono.Win.Launcher` console app (`openmono-win`), which
   PowerShell calls; `agent` runs the same `docker compose run --rm ... agent`
   as `cmd_agent_sandbox`, with
   `OPENMONO_ENDPOINT=http://host.docker.internal:7474`, the model alias as
   `OPENMONO_MODEL`, and the context as `CTX_SIZE`.
9. Host prerequisites: setup needs Git and the .NET SDK 10.0.403+
   (`win/global.json`, the same as `install.ps1`) and installs them when
   missing (`win/scripts/prereqs.ps1`): winget `Git.Git` and
   `Microsoft.DotNet.SDK.10`, or without winget MinGit from the latest Git for
   Windows release and `dotnet-install.ps1 -Channel 10.0`, both per user. The
   session `PATH` is then rebuilt from the machine and user `PATH`, so setup
   continues in the same window; a failed install stops setup. Docker Desktop
   and WSL 2 are installed with winget when missing.
10. Vision: llama-server takes images exactly when it was started with the
    model's projector, so the launcher sets `OPENMONO_VISION_ENABLED` from
    that, unless `inference.vision_enabled` is set. `agent --vision` fails
    rather than drop images or load a second model. See `docs/VISION.md`.
11. Line endings: files the Linux containers execute or parse are LF in every
    checkout (root `.gitattributes`, `eol=lf` for `docker/**`, `*.sh`,
    Dockerfiles, YAML, `openmono`). Without it, Git for Windows
    (`core.autocrlf=true`) checked out `docker/entrypoint.sh` with CRLF and
    the agent image's ENTRYPOINT failed with `/usr/bin/env: 'bash\r'`. For
    older checkouts, `openmono.ps1 setup` and `agent` rewrite CRLF under
    `docker/` to LF (`win/scripts/line-endings.ps1`), and `agent` rebuilds an
    agent image whose entrypoint has CR. A unit test fails if any
    container-bound file has a CR.
12. Not yet on Windows: host mode (`openmono agent --host`, the HostBridge
    daemon runs host commands and would need a PowerShell executor), the
    clipboard image bridge, and the optional local web gateway services
    (Caddy, SearXNG, Scrapling). The agent's built-in web fallbacks apply.
13. The agent (`src/`) is the same code as `main`: the Windows port needs no
    agent change, because the container talks to the same llama-server API.

## Layout

```
win/
  OpenMono.Win.sln
  Directory.Build.props
  global.json
  openmono.ps1                 # the Windows launcher
  docs/                        # this plan, VISION.md, WINDOWS-ACP-GRAPH-VSCODE.md
  scripts/
    bootstrap.ps1              # prerequisites, GPU check, Docker Desktop
    prereqs.ps1                # Git and .NET SDK install
    line-endings.ps1
  src/
    OpenMono.Win.Inference/    # GPU probe, model ladder, llama.cpp install,
                               # GGUF downloads, llama-server start and health
    OpenMono.Win.AgentHost/    # container run plan, endpoint mapping, ACP,
                               # shell selection, guardrails, vision flag
    OpenMono.Win.Launcher/     # openmono-win: inference up/down/status,
                               # models, agent-plan, doctor, diag
  tests/
    OpenMono.Win.Tests/        # GPU-free unit tests with fakes
    OpenMono.Win.FakeTools/    # fake docker, nvidia-smi, winget, llama-server
    smoke/windows-smoke.ps1    # openmono.ps1 end to end on Windows CI
```

## Testing

See the Testing section of `win/README.md` for the commands.

- Unit tests (`dotnet test win/OpenMono.Win.sln`) cover the ladder and VRAM
  boundaries, the backend pick, the llama.cpp release pick and install with
  its failure paths, downloads with resume and error pages, the llama-server
  command line for every rung, the health wait, crash fallback, reuse,
  foreign servers, stop, the agent launch, doctor and diag. They run on
  Windows and Linux CI without a GPU, llama.cpp or network.
- `win/tests/smoke/windows-smoke.ps1` drives `openmono.ps1` end to end under
  PowerShell 7 and 5.1 with fake tools and a fake llama-server.
- `win/tests/live/llama-live.ps1` runs the real llama.cpp build and GGUF:
  start, reuse, a chat, the read_file tool round trip, an image, stop.
- `.github/workflows/win.yml` (only on `win`) runs all of the above; the live
  check and `models check` are non-blocking jobs because they depend on
  GitHub and Hugging Face.
- Manual matrix Spencer runs: NVIDIA 24 GB+, a smaller NVIDIA card, a machine
  without an NVIDIA GPU (CPU build), and a machine already running a
  llama-server on 7474 (the README lists the steps).

## What only Spencer can provide

- Windows machines with NVIDIA GPUs (24 GB and smaller) to prove the ladder.
- A code signing certificate, only if we add a shipped installer.
- A yes before any pull request to `main`.

## Open questions

- Whether web search (SearXNG) and page fetch return as optional Windows
  extras.
- Whether host mode gets a PowerShell executor.
