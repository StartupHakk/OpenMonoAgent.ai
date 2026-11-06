# OpenMono on Windows (llama.cpp)

The same OpenMonoAgent.ai, on Windows. The agent runs in its unchanged Linux
Docker container, exactly as on Mac/Linux, and inference is the same
llama.cpp `llama-server` with the same Qwen GGUF models. The one platform
difference is where llama-server runs: natively on the Windows host at
`http://127.0.0.1:7474` (the official llama.cpp Windows build) instead of in a
compose service, reached from the container as
`http://host.docker.internal:7474`. Everything under `win/` is install and
host control, in PowerShell.

## Quick start

Prerequisites: Windows 11 and winget (`setup` uses it to install Git, the
.NET SDK and Docker Desktop with WSL 2 when they are missing). An NVIDIA GPU is
optional: with one, llama-server runs on CUDA (the NVIDIA driver is enough,
the CUDA runtime DLLs come with the llama.cpp download); without one, it runs
on the CPU.

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
.\win\openmono.ps1 setup     # Git, .NET SDK, Docker Desktop (winget), agent image, llama.cpp + GGUF download, llama-server, doctor
cd C:\path\to\project
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent              # the agent TUI, sandboxed in its container
C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent --acp-only   # ACP on 127.0.0.1:7475 for the VS Code extension
```

| Command | What it does |
| --- | --- |
| `setup` | Installs or checks prerequisites (Git and the .NET SDK; Docker Desktop and WSL 2 when missing, then starts Docker Desktop and waits for its engine), builds `openmono-win` and the agent image, installs the newest llama.cpp Windows build and downloads this machine's GGUF (see Inference below), starts llama-server, runs `doctor`. Prints numbered steps for what only you can finish (restart, first-launch agreement, sign-out) |
| `start` | Starts llama-server on 127.0.0.1:7474 with this machine's model, or reuses the one OpenMono already started when its settings match. A first run downloads llama.cpp and the model. `--model` and `llm.model` in `settings.json` pick another ladder model |
| `stop` | Stops the llama-server OpenMono started (`--force` also stops one it adopted). `--agent` or `--all` also removes agent containers |
| `status` | Inference endpoint, served model, and agent containers |
| `agent [args]` | `docker compose run --rm ... agent [args]`, the same launch as bash `openmono agent`. Starts llama-server first when it is not running. Files under `docker\` are kept on LF line endings first, and an agent image built from CRLF files is rebuilt (see Line endings below). `--vision` starts an image session on this machine's model and fails loudly if its vision projector is not loaded (no second model is loaded) |
| `models [check]` | `models` prints the Windows model ladder and marks this machine's model (`--json` for scripts). `models check` checks online that the newest llama.cpp release still has the CUDA, Vulkan and CPU Windows zips and that every ladder GGUF still downloads |
| `logs` | Follows the llama-server log (`~\.openmono\win\llama-server.log`) |
| `doctor` | PowerShell, Git, .NET SDK, Docker, compose, agent image, model ladder, GPU, llama.cpp build, model files, server health, the container-to-host route, ACP, graph |
| `diag` | Writes a zip for bug reports: GPU and VRAM (`nvidia-smi`), the installed llama.cpp builds, the last llama-server log, doctor output, inference state, settings with secrets and the home path redacted |

Runs on PowerShell 7 (`pwsh.exe`) or Windows PowerShell 5.1 (`powershell.exe`).

## Inference

`start` (and `setup`, and `agent` when nothing is serving) does what
`scripts/install.sh` does on Linux, without Docker for the server:

1. Pick the backend from `nvidia-smi`: CUDA when an NVIDIA GPU and a driver
   with a CUDA version are present, then Vulkan, then CPU. With no NVIDIA GPU
   the CPU build is the only one.
2. Install llama.cpp: the newest `b<N>` release from
   https://github.com/ggml-org/llama.cpp/releases, the official Windows x64 zip
   for that backend (`llama-<tag>-bin-win-cuda-<X.Y>-x64.zip` plus the
   matching `cudart-llama-bin-win-cuda-<X.Y>-x64.zip`, with the highest CUDA
   version the driver supports; `llama-<tag>-bin-win-vulkan-x64.zip`; or
   `llama-<tag>-bin-win-cpu-x64.zip`). It is unpacked into
   `~\.openmono\win\llama.cpp\<tag>-<backend>\`. `start` reuses the installed
   build; `setup` updates it when a newer release is out.
   `OPENMONO_LLAMA_CPP_RELEASE=b<N>` pins a release.
3. Download the GGUF for this machine (and its `mmproj` vision projector)
   from Hugging Face into the repository's `models\` folder, the same folder
   and the same file names the Linux install uses. Downloads resume.
4. Start `llama-server.exe` with the Linux flags: `--host 127.0.0.1 --port 7474
   --alias <model> --ctx-size <rung> --flash-attn on --cache-type-k/v <rung>
   --batch-size 2048 --ubatch-size 1024 --parallel 1 --jinja --reasoning off
   --metrics`, plus `--n-gpu-layers 99 --threads <up to 14>` on CUDA and Vulkan or
   `--threads/--threads-batch <half the logical processors>` on the CPU, and the `--mmproj`
   flags when the projector is present. Then wait for `/health`.
5. If the CUDA build crashes at start (an old driver, for example), the
   launcher says so and falls back to Vulkan, then to the CPU build.

The launcher records what it started in `~\.openmono\win\inference.json`.
A server on 7474 that OpenMono did not start is reused only when it already
serves this machine's model; otherwise `start` stops and says which model it
serves. Environment overrides: `LLAMA_API_KEY` (passed as `--api-key`, and to
the agent), `LLAMA_THREADS`, `OPENMONO_LLAMA_CPP_DIR`, `OPENMONO_MODELS_DIR`,
`OPENMONO_MODEL_MIRROR` (a server with the Hugging Face file names under
`/models/`), `OPENMONO_LLAMA_CPP_RELEASES_URL`.

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

One model per machine, picked by dedicated NVIDIA VRAM. The ladder lives in
`scripts/models.json` under `platforms.windows.tiers` of the default model,
next to the Linux and macOS tables. The 24 GB rung is the Linux 24 GB tier
(same GGUF, context and KV cache as `CTX_24G`/`CTX_VISION` in
`scripts/install.sh`); the smaller rungs are the Qwen3.5 GGUFs of the same
family at about 9B, 4B, 2B and 0.8B.

| Dedicated VRAM | Model (GGUF, Hugging Face `unsloth`) | Context (with vision) | KV cache |
| --- | --- | --- | --- |
| 24 GB and up | `Qwen3.8-27B-UD-Q4_K_M` | 196608 (172032) | q8_0 |
| 12 GB | `Qwen3.5-9B-UD-Q4_K_XL` | 196608 (98304) | q4_0 |
| 8 GB | `Qwen3.5-4B-UD-Q4_K_XL` | 131072 (98304) | q4_0 |
| 4 GB | `Qwen3.5-2B-UD-Q4_K_XL` | 65536 | q4_0 |
| under 4 GB | `Qwen3.5-0.8B-UD-Q4_K_XL` (last resort) | 32768 | q4_0 |
| no NVIDIA GPU (CPU only) | `Qwen3.5-2B-UD-Q4_K_XL` on the CPU build | 65536 | q8_0 |

Every model takes images through its `mmproj-F16.gguf` projector (see
`docs/VISION.md`). `inference up --model <name>` (or `llm.model` in
`settings.json`, when it names a ladder model) overrides the pick.

## Principles

This is not a separate app. The agent loop, its tools, the PermissionEngine,
ACP on 7475, graph tools (code-review-graph, graphify), the VS Code extension,
`settings.json` keys (`llm.endpoint`, `llm.model`, `inference.vision_enabled`),
and environment overrides (`OPENMONO_ENDPOINT`, `OPENMONO_MODEL`,
`OPENMONO_VISION_ENABLED`) behave as on Mac/Linux. Local-first: llama-server and
ACP bind loopback only, and no prompt, image, or file content leaves the
machine. A remote `llm.endpoint` is used as-is, as the bash launcher does.
oma-bot stays Linux.

## Layout

- `openmono.ps1`: the Windows launcher (host controls only).
- `scripts/bootstrap.ps1`: host prerequisites, GPU check, Docker Desktop.
- `src/OpenMono.Win.Inference`: llama.cpp on the host. GPU/VRAM probe, the
  model ladder from `scripts/models.json`, the llama.cpp release pick and
  install, GGUF downloads, the llama-server command line, start or reuse on
  `127.0.0.1:7474`, health checks.
- `src/OpenMono.Win.AgentHost`: the container run plan (mirrors
  `cmd_agent_sandbox`), endpoint mapping to `host.docker.internal`, ACP wiring,
  PowerShell shell selection, host guardrail patterns, vision flag mapping,
  graph artifact checks.
- `src/OpenMono.Win.Launcher`: `openmono-win`, the tested decision logic
  `openmono.ps1` calls (`inference up|down|status`, `models`, `agent-plan`, `doctor`, `diag`).
- `tests/OpenMono.Win.Tests`: GPU-free tests with fakes for CLI, GPU, HTTP and
  the llama-server process. `tests/smoke/windows-smoke.ps1` drives
  `openmono.ps1` end to end on Windows CI with a fake llama-server.
- `docs/BUILD-PLAN.md` (plan and adaptation notes), `docs/VISION.md`,
  `docs/WINDOWS-ACP-GRAPH-VSCODE.md`.

## Branch rules

This code lives only on the `win` branch. Never touch `main`, never merge to
`main`, never open a pull request. Do not use the old `Windows` branch or the
legacy `windows/` folder as a starting point.
