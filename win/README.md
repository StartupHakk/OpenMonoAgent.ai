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
| `stop` | Stops the llama-server OpenMono started, and only that one: a server it adopted keeps running (`--force` stops OpenMono's server even when the state file says it adopted one). `--agent` or `--all` also removes agent containers |
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
5. Before starting a CUDA or Vulkan build, ask it for its devices
   (`llama-server --list-devices`). These builds load `ggml-cuda.dll` or
   `ggml-vulkan.dll` at run time and quietly run on the CPU when it cannot
   load, so a build that lists no GPU is skipped. If the build exits while
   loading (an old driver, for example), the launcher says so too. Either way
   it falls back to Vulkan, then to the CPU build. A server
   that is still loading is waited for (up to 10 minutes), not replaced; one
   that never becomes healthy is stopped and reported, without a fallback.

The launcher records what it started in `~\.openmono\win\inference.json`.
A server on 7474 that OpenMono did not start is reused only when it already
serves this machine's model; otherwise `start` stops before downloading
anything and says which model that server serves. Environment overrides: `LLAMA_API_KEY` (passed as `--api-key`, and to
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

## Testing

Run these from the repository root. The first two run anywhere with the .NET
SDK 10; the rest need Windows.

| Suite | Command | What it covers |
| --- | --- | --- |
| Windows unit tests | `dotnet test win/OpenMono.Win.sln -c Release` | The ladder and VRAM boundaries, CUDA/Vulkan/CPU pick (including a missing `nvidia-smi`), the llama.cpp release pick and install (bad zip, 404, missing cudart, offline update), GGUF and mmproj downloads (404, error page, resume), the llama-server command line and vision flags for every rung, health wait, the GPU device check, crash fallback, reuse, foreign servers, stop, the container plan, doctor and diag. No GPU, network or llama.cpp needed |
| Agent tests | `dotnet test src/OpenMono.Tests -c Release --filter "FullyQualifiedName!~ResolveBaseUrl_EmptyHostOs_StillKeepsLiteralDockerGatewayName"` | The agent in the container (the skipped test needs `/.dockerenv`) |
| Smoke test | `.\win\tests\smoke\windows-smoke.ps1 -Shell pwsh` and `-Shell powershell` | `openmono.ps1` end to end under PowerShell 7 and 5.1 with fake docker, nvidia-smi, winget, a fake llama-server and a fake download server: setup paths, agent launch and quoting, CPU-only, foreign servers, CUDA crash fallback, a CUDA build that sees no GPU, no `nvidia-smi`, failed model download, restart on a setting change, and every command. No GPU, Docker or downloads |
| Live check | `.\win\tests\live\llama-live.ps1` | The real llama.cpp build and this machine's real GGUF through `openmono.ps1 start`: the model is on the GPU when there is one, listens on 127.0.0.1:7474 only, reuse from the other PowerShell, status, a chat, the agent's `read_file` tool round trip, an image, stop. Downloads the model on first run. `-ApiOnly -Endpoint <url>` runs the chat, tool and image checks against any running llama-server |
| Download check | `dotnet win/src/OpenMono.Win.Launcher/bin/Release/net10.0/openmono-win.dll models check` (or `.\win\openmono.ps1 models check`) | The newest llama.cpp release still has the Windows CUDA, Vulkan and CPU zips, and every ladder URL answers |

CI (`.github/workflows/win.yml`) runs the unit tests on Windows and Linux, the
agent tests, the PowerShell parse check and the smoke test under both shells,
and two non-blocking jobs: `models-live` (the download check) and
`llama-live` (the live check on a GPU-less Windows runner, so the CPU build
and Qwen3.5-2B).

### On a real Windows machine

The checks only a real box can do. Run them on an NVIDIA machine and on one
without NVIDIA, under both `pwsh` and `powershell.exe`:

1. `git pull`, then `.\win\openmono.ps1 setup`. It ends with `doctor`; every
   line should be `[ ok ]` or `[info]`. `.\win\openmono.ps1 models` names the
   model for this card; compare it with the table above.
2. `.\win\tests\live\llama-live.ps1`. It prints prompt and generation
   speed; on the 24 GB CUDA rung expect far more than the CPU numbers from CI.
   On NVIDIA, `.\win\openmono.ps1 status` should show a `cuda-` build.
3. A real agent turn that reads a file, from a project folder:

   ```powershell
   cd C:\path\to\project
   Set-Content notes.txt 'The secret word is BLUEBERRY.'
   C:\path\to\OpenMonoAgent.ai\win\openmono.ps1 agent
   ```

   In the TUI, ask `Read notes.txt and tell me the secret word.` The agent
   should call its read tool on the file in the container's workspace and
   answer BLUEBERRY. Then attach an image and ask what it shows to check
   vision (it is on when `status` says `vision on`).
4. VS Code: `openmono.ps1 agent --acp-only`, open the folder, send a turn
   from the OpenMono panel (see `docs/WINDOWS-ACP-GRAPH-VSCODE.md`).
5. Stop rules: `openmono.ps1 stop` ends llama-server and the port frees.
   Start a llama-server of your own on 7474 with another model; `start`
   must refuse at once (no download) and `stop` must leave it running.
6. Fallback: `openmono.ps1 stop`, then in
   `~\.openmono\win\llama.cpp\<tag>-cuda-*\` rename `cudart64_*.dll` to
   `cudart64_*.dll.off`. `openmono.ps1 doctor` should warn that the build sees
   no CUDA device, and `openmono.ps1 start` should say so and serve the same
   model on Vulkan (`status` shows a `vulkan` build). Rename the file back
   and `start` again to return to CUDA.
7. `openmono.ps1 diag` and attach the zip to any report.

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
