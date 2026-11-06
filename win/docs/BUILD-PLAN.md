# OpenMonoAgent for Windows: Foundry Local plan

Approved plan snapshot for the `win` branch. This replaces the earlier Windows
plans (bundled llama.cpp, Docker Desktop stack, LM Studio style installer under
`windows/`). Those ideas are tabled. This is a Windows branch of OpenMonoAgent.ai
that runs the existing agent against Microsoft Foundry Local, with all
Windows-specific code in the `/win/` folder at the repo root.

## Win-branch adaptation notes

These notes record where this snapshot differs from the original plan text, or
where the scaffold makes an explicit provisional choice. The plan body below is
otherwise unchanged in intent.

0. Container decision (Spencer): the agent stays in Docker. The agent runs in
   the unchanged Linux agent container (bash inside); oma-bot stays Linux too.
   `win/` is Windows install and host control in PowerShell: Docker Desktop
   checks, Foundry Local lifecycle, stack start/stop, and the endpoint mapping
   containers use to reach host Foundry (`host.docker.internal:7474`). There
   is no native Windows in-process agent. The BUILD-PLAN body below still
   describes in-process hosting in places; where it does, this note wins.

These notes record where this snapshot differs from the original plan text, or
where the scaffold makes an explicit provisional choice. The plan body below is
otherwise unchanged in intent.

1. Foundry Local C# SDK package: at scaffold time the exact shipping package
   name and version for `Microsoft.AI.Foundry.Local` is not pinned, so the
   `OpenMono.Win.Foundry` project does NOT take a hard NuGet dependency yet.
   The host drives the Foundry CLI for M1 and probes for the SDK assembly at
   runtime (`FoundryConstants.SdkAssemblyName`). When the package coordinate is
   confirmed on a Windows machine, add the `PackageReference` where the csproj
   placeholder comment indicates and promote the probe to a typed client.
2. Fallback model alias: the plan leaves the under-24-GB fallback open. The
   scaffold uses `qwen3-4b` as a provisional constant (`FallbackModelAlias`)
   and always resolves the real catalog spelling at runtime, so a wrong guess
   surfaces as a clear "not in catalog" error, never a bad download.
3. Endpoint host spelling: the plan binds `127.0.0.1:7474`. OMA defaults use
   `localhost:7474`. These are the same loopback interface; the Windows host
   uses the `127.0.0.1` spelling and OMA config normalization treats them as
   equivalent roots.
4. CI: the `win.yml` workflow builds and tests on both `windows-latest` and
   `ubuntu-latest`. All unit tests use fakes, so no GPU or Foundry install is
   needed in CI.
5. Model auto-detect: OMA already probes `{endpoint}/props` (llama.cpp) and
   falls back to `{endpoint}/v1/models`. Foundry Local serves `/v1/models`,
   so the existing detection path covers the Foundry endpoint with no upstream
   change.

## Goal

Ship a branch of [OpenMonoAgent.ai](https://github.com/StartupHakk/OpenMonoAgent.ai)
that a Windows developer can run locally: the OMA agent, on-device inference, no
cloud required for the model. Inference uses Foundry Local and targets Qwen3.8-27B,
following Microsoft's November 5, 2026 Foundry Local coding announcement.

The existing Mac and Linux paths stay unchanged. `main` is not touched. The older
`Windows` branch and `windows/` folder are left alone and are not the source of
this work.

## Why Foundry Local

OMA already talks to an OpenAI-compatible server (today that is `llama-server` on
`127.0.0.1:7474`). Foundry Local exposes the same kind of API
(`/v1/chat/completions`, `/v1/models`, streaming SSE, tool and function calling)
either through its CLI server or through the C# SDK's optional web service.

That means Windows inference can swap the backend without rewriting the agent loop.

Microsoft's current coding target is Qwen3.8-27B, optimized with ONNX Runtime:

- INT4 weights are about 18 GB, so it runs on consumer NVIDIA GPUs with 24 GB of VRAM.
- 24 GB cards support context up to 64K tokens. 32 GB and above can use the model's full 262K context.
- Foundry Local 0.13.0 and later include DFlash2 speculative decoding, CUDA graph fusions, KV-cache quantization, prefix caching, and continuous batching. Microsoft's published numbers show it faster than llama.cpp on the same 4-bit Qwen3.8-27B, especially past 131K tokens, on an RTX 5090 (32 GB) and a DGX Spark.
- Install on Windows: `winget install Microsoft.FoundryLocal`, then `foundry model load qwen3.8-27b` and `foundry server status` for the local API URL.
- NVIDIA GPU execution needs CUDA and a compatible cuDNN runtime installed separately, with those DLL directories on `PATH`. Foundry Local does not download those runtimes.
- Foundry Local also has native SDKs (including C#, `Microsoft.AI.Foundry.Local`) that can download, cache, load, and serve a model in-process, then optionally start a local OpenAI-compatible web service.
- Hardware selection (NPU, GPU, CPU) is handled by the runtime. Microsoft says Qwen3.8-27B support will expand to Macs and AMD GPUs later. Until then, NVIDIA 24 GB+ is the first-class path, and smaller catalog models remain a fallback on weaker machines.

Sources: Foundry Local plus Qwen3.8-27B blog (devblogs.microsoft.com/foundry), foundrylocal.ai, microsoft/foundry-local on GitHub.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  win/  (Windows-only)                                   │
│  - Foundry host: detect or install Foundry Local,       │
│    download and load qwen3.8-27b (or a smaller fallback)│
│    start OpenAI-compatible server on 127.0.0.1:7474     │
│  - Agent host: run OpenMono.Cli in process with         │
│    Windows tool overrides (PowerShell shell, clipboard, │
│    LSP paths, MCP .cmd, guardrails, prompt overlay)     │
│  - Launcher / first-run: check CUDA, VRAM, Foundry CLI  │
│    or SDK, model cache, then open chat                  │
└───────────────────────────┬─────────────────────────────┘
                            │  OpenAI HTTP on 127.0.0.1:7474
                            ▼
              Foundry Local (ONNX Runtime)
              Qwen3.8-27B INT4 on NVIDIA GPU
```

Preferred host mode: the `win/` C# host uses Foundry tooling to download and
load `qwen3.8-27b`, then serves the OpenAI-compatible endpoint bound to
`http://127.0.0.1:7474` so the existing agent keeps its default base URL.

Fallback host mode: if the Foundry Local CLI (0.13.0+) is already installed and
serving, read `foundry server status` and point the agent at that endpoint (do
not start a second server on a conflicting port).

Do not bundle llama.cpp. Do not require Docker Desktop, Caddy, SearXNG, or
Scrapling for the first Windows milestone. Search and browse tools can come back
later as optional extras.

## Branch and folder rules

- Create a new branch from current `main`. Suggested name: `win`.
- Do not start from, merge, or rewrite the existing `Windows` branch.
- All Windows-specific code lives under `/win/` at the repo root (solution, host projects, scripts, docs, tests).
- Reuse existing OMA projects (`src/OpenMono.Cli` and friends) by project reference. Do not move or restructure those projects.
- Prefer not to edit existing OMA files. If Windows cannot work without an upstream change (for example hardcoded `/bin/bash`), implement a workaround in `win/` first and list the upstream edit separately.
- Push only to the `win` branch. Do not open a pull request against `main`. Do not merge to `main`.

## Proposed `/win/` layout

```
win/
  OpenMono.Win.sln
  Directory.Build.props
  global.json                  # pin the same .NET SDK family as the rest of OMA
  docs/BUILD-PLAN.md           # this plan
  src/
    OpenMono.Win.Foundry/      # Foundry Local wrapper: catalog, download, load,
                               # start/stop web service, health, model alias mapping
    OpenMono.Win.AgentHost/    # DI graph that hosts OpenMono.Cli with Windows tools
    OpenMono.Win.Desktop/      # native chat UI (WinUI 3; WPF only if WinUI blocks)
    OpenMono.Win.Launcher/     # first-run checks, CUDA/Foundry detection, model pick
  scripts/
    bootstrap.ps1              # winget Foundry Local if missing, PATH checks, version
    check-cuda.ps1
  tests/
    OpenMono.Win.Tests/
```

## Agent work (so it actually runs on Windows)

These are the known blockers in current OMA. Handle them from `win/` without
editing upstream files where possible:

| Blocker | Windows approach |
|---|---|
| `BashTool` and `HookRunner` hardcode `/bin/bash` | Register a Windows `ToolBase` named `Bash` that runs `pwsh.exe`, then `powershell.exe`, then `cmd.exe`. Hooks use the same shell selector. |
| `GrepTool` needs `rg` | Stage `rg.exe` next to the host and prepend that directory to `PATH`. |
| Clipboard is `pbcopy` / `xclip` | WinUI or Windows clipboard service. |
| LSP `file://` URIs | Path mapper that produces correct Windows file URIs. |
| MCP cannot launch `.cmd` shims | Launch helper that resolves `.cmd` / `.exe` and probes Node if needed. |
| Guardrails assume Unix paths | Deny list for `format`, `diskpart`, `bcdedit`, `reg delete`, recursive deletes of `C:\` and `C:\Windows`, credential store writes. |
| Init / slash commands use `head`/`grep`/`sed` | Windows `/init` that parses files with .NET. |
| Recovery copy assumes Docker / llama compose | Error text talks about Foundry Local (is the server up, is the model loaded, is CUDA on PATH). |
| System prompt Unix assumptions | Prompt overlay describing Windows paths, PowerShell, and Foundry Local. |

The agent runs in process behind a native chat window. No embedded terminal.

## Inference work

1. Detect Foundry Local CLI (`foundry --version` >= 0.13.0) or use the NuGet SDK.
2. Detect NVIDIA GPU and VRAM. Require 24 GB dedicated VRAM for Qwen3.8-27B. If VRAM is lower, pick a smaller catalog alias and tell the user why.
3. Confirm CUDA and cuDNN DLLs are discoverable. If not, show install steps rather than failing with a blank error.
4. Download `qwen3.8-27b` on first run (resume, progress, disk-space preflight). Cache stays where Foundry Local caches models.
5. Load the model and start the OpenAI-compatible service on `127.0.0.1:7474`.
6. Health-check `/v1/models` (and `/health` if present). Reuse a healthy existing server instead of binding twice.
7. Point `OpenMono.Cli` at that base URL the same way it points at llama-server today.
8. On exit, unload or leave the CLI server running if the user started it themselves.

Model alias is `qwen3.8-27b` unless Foundry's catalog spelling differs on the
installed version. Resolve the alias from the catalog at runtime rather than
hardcoding a file hash.

## Milestones

### M1: Foundry host and a compiling `/win/` tree

- Solution, projects, and docs under `/win/`.
- Foundry wrapper that can detect CLI or drive the C# SDK.
- Bootstrap script for `winget install Microsoft.FoundryLocal`.
- Bind or adopt a server on `127.0.0.1:7474`.
- Unit tests that mock catalog and HTTP so Linux or Windows CI can run without a GPU.
- Acceptance: `dotnet test` on the `win/` test project is green. On a 24 GB NVIDIA machine, `qwen3.8-27b` loads and `/v1/models` lists it.

### M2: Agent on Windows

- Windows shell tool, guardrails, clipboard, LSP mapper, MCP helper, prompt overlay, slash `/init`.
- Native chat UI: streaming, permissions, stop, tool cards.
- Agent session against the Foundry endpoint.
- Acceptance: a Windows session can complete a simple coding turn (read a file, run a PowerShell command, write a file) using Qwen3.8-27B locally.

### M3: First-run, packaging, hardening

- First-run wizard: hardware check, Foundry install, CUDA reminder, model download, server start, open chat.
- Context window: 64K on 24 GB, longer when VRAM allows (up to 262K).
- Diagnostics bundle (redacted): GPU, VRAM, Foundry version, CUDA PATH, last server log.
- Optional installer later (not a blocker for the branch to be usable from source).
- Acceptance: clone the `win` branch, run the bootstrap, and reach a working chat on a qualifying NVIDIA machine.

## Testing

- Unit tests for the Foundry wrapper, Windows tools, and URL or port logic. These must run without NVIDIA hardware.
- A `windows-latest` GitHub Actions workflow that lives only on the `win` branch, under `win/` scripts or `.github/workflows/win.yml` if a workflow file is required. It builds and tests what it can. It will not load Qwen3.8-27B.
- Manual matrix Spencer runs: NVIDIA 24 GB (64K), NVIDIA 32 GB+ (longer context), a machine without enough VRAM (must refuse Qwen3.8-27B clearly), and a machine with Foundry CLI already installed (must adopt the existing server).

## What only Spencer can provide

- A Windows machine with an NVIDIA GPU and at least 24 GB of VRAM to prove Qwen3.8-27B.
- CUDA and cuDNN installed per Foundry Local 0.13.0+ release notes.
- Later: AMD and Intel or NPU runs, once Microsoft expands Qwen3.8-27B support.
- A code signing certificate, only if we add a shipped installer.
- A yes before any pull request to `main`.

## Open questions (do not block M1)

- Whether web search (SearXNG) and page fetch return as optional Windows extras after M2.
- Whether we ever expose Foundry Local through GitHub Copilot BYOK as well, or keep the endpoint private to OMA.
- Default fallback model when VRAM is under 24 GB.

## Build sequence for the cloud agent

1. Create branch `win` from current `main`. Write this plan to `win/docs/BUILD-PLAN.md`. Scaffold `win/OpenMono.Win.sln` and the Foundry and test projects. Push. No PR.
2. Implement the Foundry wrapper (SDK and CLI detect, download, load, bind `127.0.0.1:7474`, health, fallback alias). Tests with fakes.
3. Implement Windows agent host overrides and wire `OpenMono.Cli` to the Foundry base URL.
4. Native chat UI and first-run checks.
5. CI workflow on the `win` branch and M3 diagnostics.

Commit and push often on `win` only.
