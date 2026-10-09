# OpenMono for Windows: Build Plan

This is the implementation plan for the Windows product. It is grounded in the `Windows` branch scaffold at commit `6500469` and in the OMA code that scaffold calls. Approved decisions are fixed and are not reopened here.

Approved decisions:

- All product code stays additive under `windows/`, in its own solution. Reuse OMA through project references and wrappers. Do not edit existing OMA files. Anything that would be cleaner as an upstream edit is listed for Spencer, and is not part of the build until he approves it.
- Work happens only on the `Windows` branch. Never on `main`.
- No embedded terminal. The daily surface is a native in-app chat agent. WinUI 3 is the UI. WPF is the fallback only if WinUI 3 blocks shipping. The OMA agent runs in process. Windows behavior is supplied by replacing services when the host builds the object graph: a PowerShell-first shell tool, clipboard, LSP URI mapping, MCP `.cmd` launching, Windows guardrails, and a prompt overlay.
- Inference is a bundled official llama.cpp Windows build (CUDA, Vulkan, CPU). The app detects GPU, VRAM, and RAM, then picks the same tiers as `scripts/install.sh`: 27B Q4 on 24GB, IQ3 on 16GB, 9B on 12GB, 35B MoE on CPU. `llama-server.exe` listens on `127.0.0.1:7474`.
- Docker Desktop (WSL2 backend) stays in the product for Caddy, SearXNG, and Scrapling. `windows/docker/docker-compose.windows.yml` points Caddy at `host.docker.internal:7474`. The app detects Docker, offers install, and starts and stops that stack. The containerized agent and the containerized llama-server do not start.
- The installer is a per-user Inno Setup `setup.exe`. Publish is self-contained. The installer checks the VC++ redistributable. The installer stays lean: the GPU flavor and the model download on first run. App updates use Velopack. A WiX MSI is the optional enterprise package. Code signing stays off until a certificate exists.
- M1, M2, and M3 are all in scope and are finished end to end.

The older design note that dropped Docker and shipped an embedded terminal first is superseded by the decisions above. The scaffold already follows the approved shape.

## 1. Current state of the scaffold

The scaffold is real code, not an empty tree. `windows/OpenMono.Windows.sln` contains seven projects. `windows/Directory.Build.props` applies only inside `windows/` (nullable, warnings as errors, preview language). It does not import the repo root props, and MSBuild stops at the nearest `Directory.Build.props`, so Windows projects do not pick up the root file. `windows/global.json` pins the .NET SDK to `10.0.100` with `latestMinor` roll-forward, matching `.github/workflows/windows-desktop.yml`.

`OpenMono.Cli` is referenced from `windows/src/OpenMono.AgentHost/OpenMono.AgentHost.csproj` as `..\..\..\src\OpenMono.Cli\OpenMono.Cli.csproj`. That project is not modified. `InternalsVisibleTo` on it names only `OpenMono.Tests`.

### What exists and is substantive

| Area | What is actually implemented |
|---|---|
| Hardware | `GpuDetector` runs `nvidia-smi` with the same CSV query as `scripts/install.sh`, parses it in `ParseNvidiaSmi`, and falls back to `Win32_VideoController` WMI. `MemoryDetector` uses `GC.GetGCMemoryInfo` plus `wmic` for physical cores. `DiskDetector` checks free space with a 10 percent overhead helper. `ModelTierSelector` maps dedicated VRAM to tiers 24, 16, 12, and CPU, and picks CUDA when the vendor is NVIDIA and CUDA is available, otherwise Vulkan, otherwise CPU. |
| Models | `models.json` copies the four `select_model` entries, context constants (`196608`, `180224`, `196608`, `196608`, vision `172032` and `98304`), Hugging Face URLs, and llama.cpp `b9070` Windows zip URLs (CUDA 12.4, Vulkan, AVX2) plus ripgrep `14.1.1`. `ModelRegistry` loads that file and applies `OPENMONO_MODEL_MIRROR`. `ModelDownloader` writes a `.part` file, sends `Range` when the partial exists, renames on success, and retries once after a checksum miss. `ChecksumVerifier` skips verification when the pinned hash is empty. |
| Supervisor | `HealthPoller` polls `/health` for up to 180 seconds, then reads `/props` (`default_generation_settings.model`) and falls back to `/v1/models`. `PortAllocator` probes `127.0.0.1` and falls back through `8081` to `8085` and `9080`. `SupervisorConfig.BuildLlamaCommand` emits the install.sh flag set (`--host 127.0.0.1`, `--ctx-size`, `--n-gpu-layers 99` or `0` on CPU, `--flash-attn on`, KV cache `q8_0` on tier 24 and CPU, `q4_0` on 16 and 12, `--jinja`, `--reasoning off`, `--metrics`, optional `--mmproj`). `LlamaServerSupervisor.StartAsync` starts the process, redirects stdout and stderr, and waits for health. `DockerDetector` runs `docker info` and `docker compose version`. `DockerComposeManager` builds `docker compose -f docker/docker-compose.yml -f windows/docker/docker-compose.windows.yml --profile full up -d caddy searxng scrapling` and sets `LLAMA_UPSTREAM=host.docker.internal:<port>`. |
| Agent host | `WindowsShellTool` is a full `ToolBase` named `Bash`. It runs `pwsh.exe`, then `powershell.exe`, then `cmd.exe`, with timeout, tree kill, and background logs under `%TEMP%\openmono\bg` plus `Get-Content` and `Stop-Process` follow-ups. `WindowsGuardrails` denies `format`, `diskpart`, `bcdedit`, `reg delete`, recursive deletes of `C:\` and `C:\Windows`, and credential-store writes. `WindowsPermissionDefaults` merges allow, deny, and ask globs into `AppConfig`. `LspPathMapper`, `McpLaunchHelper`, `HookShellSelector`, `WindowsSystemPromptOverlay`, `ClipboardService`, and `SlashCommands` exist as libraries. `ChatOutputSink` implements `IOutputSink`. `ChatInputReader` implements `IInputReader` and maps Allow once, Allow for session, and Deny onto `PermissionResponse`. |
| Desktop | Unpackaged WinUI 3 (`net10.0-windows10.0.19041.0`, Windows App SDK `1.7.250606001`, `WindowsPackageType` None, `WindowsAppSDKSelfContained` true). `app.manifest` sets `longPathAware`. Navigation has Chat, Models, Server, Settings. `WizardPage` detects hardware, recommends a tier, detects Docker, and downloads the GGUF (and mmproj when vision is on). `ChatPage` creates an `AgentSession`, streams text into a list, and shows a permission `ContentDialog`. `ServerPage` builds a llama command and calls `StartAsync`. `UpdateService` checks GitHub releases and opens the release page. `DiagnosticsService` writes a redacted text bundle. |
| Installer and CI | `installer/inno/openmono.iss` is a per-user Inno script (`PrivilegesRequired=lowest`, `{localappdata}\Programs\OpenMono`), with an optional VC++ redist check, desktop icon task, and an uninstall page that defaults to keeping models and `%USERPROFILE%\.openmono`. `build/build.ps1` builds the solution, tests, self-contained publishes `win-x64`, copies `models.json`, signs only when `WINDOWS_CERT_PATH` exists, and runs ISCC when Inno Setup 6 is installed. `fetch-llama.ps1`, `fetch-rg.ps1`, and `fetch-node.ps1` download third-party bits. The workflow on `windows-latest` restores, builds, tests, publishes, optionally signs, compiles the installer, runs the smoke test, and uploads `dist/OpenMonoSetup.exe`. |
| Tests | xUnit covers tier thresholds, nvidia-smi parsing, disk overhead, port fallback, health and `/props` parsing, Docker version parsing, shell-tool name precedence, permission defaults, guardrail denies, LSP URI round trip, MCP `.cmd` wrapping, llama flag shape, download, Range resume, and checksum retry. `OpenMono.SmokeTest` starts a stub HTTP server and checks health, SSE chat, tier selection, and the llama command. |

### What compiles

`OpenMono.Hardware`, `OpenMono.Models`, `OpenMono.Supervisor`, `OpenMono.AgentHost`, `OpenMono.Windows.Tests`, and `OpenMono.SmokeTest` target `net10.0` and are the Linux-CI-able set. `OpenMono.AgentHost` compiles only when `OpenMono.Cli` compiles, which it does as a normal SDK project. `OpenMono.Desktop` targets `net10.0-windows` and references the Windows App SDK, so it builds on Windows, which is what `windows-latest` does. Building the whole solution on Linux fails at the desktop project. The workflow does not have a Linux job yet.

The scaffold has not been proven by a green `windows-latest` run in this plan. Treat the first cloud-agent run that executes `dotnet test` on Windows as the compile baseline, and fix only `windows/` if that run is red.

### What is stubbed or unwired

These are the gaps the rest of this plan closes. They are the difference between "the types exist" and "a user can install and chat."

1. `AgentHostFactory.CreateSession` constructs `ConversationLoop` with an LLM client, a tool registry, permissions, the chat sink, and the chat reader. It does not insert a system prompt, does not save sessions, does not start MCP or LSP, does not pass a custom `IToolExecutor`, and does not append `WindowsSystemPromptOverlay`. `SystemPrompt` in OMA is an internal class, and `BuildAsync` is unreachable from `OpenMono.Windows.AgentHost`.
2. Tool registration uses reflection over parameterless `ITool` constructors and skips `BashTool`. That picks up `FileRead`, `FileWrite`, `FileEdit`, `Glob`, `Grep`, `Agent`, `Todo`, `AskUser`, `WebFetch`, `WebSearch`, `ListDirectory`, `ApplyPatch`, plan-mode tools, and `RoslynTool` (its `referenceDirectory` argument is optional). It skips `MemorySaveTool`, `LspTool`, and `PlaybookTool`, because those constructors require services. `CreateSession` records the skip list and does not construct the missing services.
3. `ToolRegistry.Register` overwrites by name. The factory registers `WindowsShellTool` first and then skips any later tool whose name is already present, which is why Bash stays the Windows tool. That only holds if nothing registers `Bash` afterwards. Explicit registration should replace the reflection scan so the set is obvious.
4. `HookShellSelector` is used by `WindowsShellTool` and is not used for hooks. `ConversationLoop` builds a `LocalToolExecutor` whose `HookRunner` hardcodes `FileName = "/bin/bash"`. The factory does not pass an executor, so any configured hook hits that path.
5. `LspPathMapper` and `McpLaunchHelper` have unit tests and are not called by the session factory. `LspClient.StartAsync` still concatenates `file://` plus the raw path.
6. `ChatPage` sends slash text into `RunTurnAsync` as a user message. `SlashCommands` only filters the suggest box. OMA `CommandRegistry` is not constructed. `/init` therefore never runs, and the Unix `head | grep | sed` line in `InitCommand` is not yet replaced.
7. `ClipboardService` is a pair of injected delegates. The chat page does not call the WinUI clipboard API.
8. `WriteSettings` writes `inference.ctxSize` and `acpServer`. `ConfigLoader` deserializes with `JsonNamingPolicy.SnakeCaseLower` (`OpenMono.Config.JsonOptions`). The keys OMA reads are `ctx_size` and `acp_server`. `vision_enabled` happens to match. A Windows-written settings file will not apply context size or ACP settings when OMA reloads it. The in-process host currently sets `AppConfig` in memory, so chat still sees the right context until a restart loads the file.
9. `ServerPage` calls `BuildLlamaCommand` and then `PortAllocator.Allocate`. The command line can contain the old port. `Allocate` treats any busy port as unusable. It does not keep the port when `/health` is already our server. `LlamaServerSupervisor` comments say it restarts with backoff. `StartAsync` does not. The log `StreamWriter` is disposed when `StartAsync` returns, while the process keeps writing.
10. The wizard downloads the GGUF and stops. It does not download `llama-server.exe`, does not gate on disk space, does not pause, and does not start inference or open Chat. Checksums in `models.json` are empty strings, so verification is skipped.
11. `fetch-llama.ps1` is not called by `build.ps1` or by the wizard. The CPU binary is not staged into the publish directory, so the Inno script's "lean installer includes CPU llama-server" claim is not true of the current publish output.
12. `DockerComposeManager` points at the git repo's `docker/docker-compose.yml`. An installed app has no repo. Scrapling is `build: context: ./scrapling`, so the stack also needs the Caddyfile, `docker/searxng`, and `docker/scrapling` copied beside the compose file. None of that is staged.
13. `UpdateService.ApplyUpdateAsync` opens a browser. Velopack is not referenced. There is no WiX project, no tray icon, no single-instance mutex, and no WPF project.
14. `NeedsFirstRun` is "the selected model file is missing." It does not remember that the wizard finished, and it does not check that `llama-server.exe` exists.

## 2. Final `windows/` layout

Keep the solution separate from `OpenMono.sln`. Add files only under `windows/`, plus the already-allowed workflow file when CI needs a Linux job. Do not add a project to the root solution.

```text
windows/
  OpenMono.Windows.sln
  README.md
  VERSION.windows
  global.json
  Directory.Build.props
  build/
    build.ps1                 publish, stage third-party and docker assets, optional sign, ISCC
    fetch-llama.ps1           official llama.cpp Windows zips, SHA256, extract llama-server.exe and CUDA/Vulkan DLLs
    fetch-rg.ps1              official ripgrep rg.exe
    fetch-node.ps1            optional portable Node for MCP and the TypeScript language server
    pin-checksums.ps1         fill sha256 fields in models.json from downloaded files
    stage-docker.ps1          copy docker compose assets into the publish tree (read-only copies)
    smoke-test.ps1
    new-release.ps1           Velopack pack plus GitHub release notes
  src/
    OpenMono.Hardware/        net10.0. No OMA reference.
    OpenMono.Models/          net10.0. No OMA reference. models.json copied to output.
    OpenMono.Supervisor/      net10.0. References Hardware and Models.
    OpenMono.AgentHost/       net10.0. References OpenMono.Cli, Hardware, Models, Supervisor.
    OpenMono.Desktop/         net10.0-windows, WinUI 3, unpackaged. References the four libraries.
    OpenMono.SmokeTest/       net10.0 console. References Hardware, Models, Supervisor.
  installer/
    inno/openmono.iss
    wix/                      M3. Product.wxs, per-machine option. Absent until that milestone.
    assets/                   icon.ico, license.rtf
    deps/                     VC_redist.x64.exe downloaded by build.ps1, not committed
  docker/
    docker-compose.windows.yml
  thirdparty/
    README.md
    SBOM.md
  docs/
    BUILD-PLAN.md
    windows-architecture.md
    first-run-spec.md
    manual-test-plan.md
  tests/
    OpenMono.Windows.Tests/   net10.0 xUnit. References the four libraries.
```

`OpenMono.Desktop.Wpf` is not created up front. Add it only if a WinUI 3 blocker stops M1 (unpackaged bootstrap, window activation, or self-contained publish). It would reference the same four libraries and host the same pages. The agent host must not take a UI dependency.

### Project responsibilities and OMA references

`OpenMono.Hardware` detects GPUs, RAM, CPU cores, and disk space, and selects the tier and llama flavor. It stays free of OMA types so tests and the wizard can run it without the agent.

`OpenMono.Models` owns `models.json`, tier lookup, mirror URLs, the downloader, and checksums. It stays free of OMA types. The numbers and URLs match `scripts/install.sh` `select_model` and the `CTX_*` constants. When those change upstream, update `models.json` in the same Windows-branch change. Do not edit `install.sh`.

`OpenMono.Supervisor` owns process lifetime for `llama-server.exe`, port choice, health, logs, and the Docker Compose process. It references Hardware and Models so it can turn a `ModelTier` into a command line. It does not reference `OpenMono.Cli`. The agent talks to the server over HTTP the same way `OpenAiCompatClient` already does.

`OpenMono.AgentHost` is the only project that references `src/OpenMono.Cli/OpenMono.Cli.csproj`. It constructs the OMA object graph in process:

- `AppConfig`, `ConfigLoader` when loading an existing settings file, `SessionState`, `SessionManager`
- `ToolRegistry`, `ToolBase` tools, `LocalToolExecutor`, `IToolExecutor`
- `PermissionEngine`, `PathGuard` (used as-is inside file tools)
- `OpenAiCompatClient` via `new OpenAiCompatClient(config.Llm)` or `ProviderRegistry.CreateClient`
- `ConversationLoop`
- `MemoryStore`, `LspServerManager` only as a source of language ids, `McpServerManager`, `PlaybookLoader`, `PlaybookRegistry`, `PlaybookExecutor`
- `CommandRegistry` and the OMA command types, with `WindowsInitCommand` registered in place of `InitCommand`
- `HookRunner` only as a no-op instance after hooks have been copied off `AppConfig` (see section 4)
- `AcpHostedService` and the ACP `ServiceCollection` when the loopback toggle is on

`OpenMono.Desktop` is the WinUI shell: wizard, chat, models, server, settings, tray, dialogs. It calls the libraries. It does not reimplement tier math or tool policy.

`OpenMono.SmokeTest` and `OpenMono.Windows.Tests` lock the pure behavior so Windows UI work cannot silently change ports, tiers, or the shell tool name.

No project links OMA source files with `<Compile Include>`. Types that are public are used directly. Types that are internal stay unused, and the workaround lives in `windows/`.

## 3. Ordered work breakdown

Estimates are focused engineering days for one person who already knows the repo. They are sizing, not a calendar. Dependencies are listed on each task. A task starts when its dependencies are merged to `Windows`. Do not compress runs 11 through 15 against the hardware matrix: WinUI polish, Vulkan variance across AMD and Intel, and matrix fallout (M3.5) are the likely overruns.

### M1. Installable app that can chat against native llama-server

Exit: on a clean Windows 11 machine, run `OpenMonoSetup.exe` per user, finish the wizard, reach `/health`, send a chat message, see streamed text and a native permission dialog, restart the app, and continue without a terminal and without hand-editing JSON.

#### M1.1 Lock the compile baseline and the settings file

Files: `windows/src/OpenMono.AgentHost/AgentHostFactory.cs` (`WriteSettings`), a new `OmaSettingsWriter` next to it, `windows/tests/OpenMono.Windows.Tests/SupervisorTests.cs` or a new `SettingsWriterTests.cs`.

Work: write settings with `JsonSerializer` and `OpenMono.Config.JsonOptions.Indented`, so keys are snake_case (`ctx_size`, `acp_server`, `vision_enabled`, `api_key`). Round-trip the file through `ConfigLoader.Load` in a unit test and assert endpoint, model, context size, and ACP port. Keep permission merges. Do not hand-concatenate JSON.

Acceptance: a settings file written by the wizard loads in `ConfigLoader` with `Llm.ContextSize` and `AcpServer.Port` equal to what the wizard chose.

Effort: 1 day. Depends on nothing.

#### M1.2 Explicit tool graph and a system prompt the model actually sees

Files: `AgentHostFactory.cs`, new `WindowsPromptBuilder.cs`, new `ToolGraphBuilder.cs`, `WindowsSystemPromptOverlay.cs`.

Work: delete the reflection scan. Register tools in the same order as `Program.cs` lines 181 to 219, with these substitutions:

- `WindowsShellTool` under the name `Bash`. Do not register `BashTool`.
- `MemorySaveTool(memoryStore)` once `MemoryStore` is constructed.
- `RoslynTool` with the reference directory `Program.cs` already resolves (`ResolveRefDirectory` is private, so pass `null` and let Roslyn use its default, or duplicate the small directory probe in `windows/`).
- `PlaybookTool` after `PlaybookLoader` and `PlaybookExecutor`.
- Skip OMA `LspTool` in M1. C# goes through `RoslynTool`. Other languages land in M2.
- `WebSearchTool` and `WebFetchTool` stay the OMA types. Set `config.Web.Gateway` only when Docker is healthy (M1.8). When the gateway is empty, `GatewayCapabilities` probes `Llm.Endpoint`, gets no `/services` from llama-server, and the tools fall back to DuckDuckGo and direct fetch.

Build the system prompt in `WindowsPromptBuilder` because `OpenMono.Utils.SystemPrompt` is internal. Compose, in this order: a snapshot of the base prompt text (copied once into `windows/src/OpenMono.AgentHost/PromptSnapshot.txt` and embedded), `WindowsSystemPromptOverlay.Build()`, `StackDetector.BuildPromptSection`, `ProjectConfig.Load` (`OPENMONO.md`), `MemoryStore.LoadIndex`, `GitHelper.GetContextAsync`, and the environment block (working directory, OS, date, model). Insert one `Message` with `MessageRole.System` on the session before the first turn. If `%USERPROFILE%\.openmono\system-prompt.md` or the workspace `.openmono/system-prompt.md` exists, `PromptOverrides.LoadSystemPrompt` replaces the snapshot, and the overlay is still appended so Windows rules survive a custom prompt.

Acceptance: a unit test builds a session with a fake workspace, and the first message is a system message that contains the overlay line "Prefer PowerShell syntax" and the workspace path. `ToolRegistry.Resolve("Bash")` returns `WindowsShellTool`. `Resolve("Grep")` returns `GrepTool`. `Resolve("Memory")` or whatever `MemorySaveTool.Name` is returns a live instance. Assert the full registered tool-name set exactly (fail on any unexpected addition), so a future upstream tool with all-default-value constructor parameters cannot silently slip in through the old reflection path or an incomplete explicit list.

Effort: 3 days. Depends on M1.1.

#### M1.3 Session save, slash commands, and `/init` without Unix tools

Files: `AgentHostFactory.cs`, `ChatPage.xaml.cs`, new `WindowsInitCommand.cs`, new `SlashDispatcher.cs`.

Work: construct the same `CommandRegistry` as `Program.cs` (Help, Status, Stats, Undo, Debug, Resume, Export, Clear, Checkpoint, Think, Mode, Prompt, Model, Btw, Retry, Compact, Plan, Playbook). Register `WindowsInitCommand` and do not register `InitCommand`. `WindowsInitCommand` writes `OPENMONO.md` the same way, and reads Makefile targets in C# (lines matching `^[A-Za-z0-9_.-]+:`) instead of `head | grep | sed`. `ProcessRunner` would run that pipeline through `cmd.exe /c` and fail on a stock Windows machine.

`SlashDispatcher` splits the input, resolves the command, and calls `ExecuteAsync` with a `CommandContext` whose renderer is an adapter over `ChatOutputSink` (`IRenderer` extends `IOutputSink`, `IInputReader`, and `ILiveFeedback`). Implement a small `ChatRenderer : IRenderer` that forwards output to the sink and input to `ChatInputReader`, with `BeginTurn` and `EndTurn` as no-ops until the chat page shows a working state.

After each successful turn, call `SessionManager.SaveAsync` the way `Program.cs` does, into `%USERPROFILE%\.openmono\sessions`.

Acceptance: `/help` lists commands in the chat transcript and does not call the model. `/init` in a temp directory that contains a Makefile writes `OPENMONO.md` including a make target, with no `head.exe` on PATH. A turn creates a session file under the data directory.

Effort: 3 days. Depends on M1.2.

#### M1.4 Ripgrep on PATH, single instance, and chat error recovery

Files: `App.xaml.cs`, `fetch-rg.ps1`, `build.ps1`, `ChatPage.xaml.cs`, new `ProcessEnvironment.cs` in the supervisor or agent host.

Work: `GrepTool` starts `FileName = "rg"`. Publish `rg.exe` under `{app}\resources\rg\`. On launch, prepend that directory to the process `PATH` before any tool runs. Surface a chat system line if `rg.exe` is missing, because `GrepTool` already returns "is ripgrep installed?" and the model will loop if the UI stays quiet.

Take a named mutex `Local\OpenMono.Windows.SingleInstance` in `App.OnLaunched`. A second launch activates the existing window.

When a turn throws `HttpRequestException`, the chat page tells the user inference is down and offers Restart. That path calls `LlamaServerSupervisor` only. It must not call `Program.Main`, so `TryRecoverLlamaServerAsync` (which runs `docker compose up llama-server` and tells the user to `cd ~/openmono.ai/docker`) never runs.

Acceptance: a test sets PATH to a temp dir containing a fake `rg` script stand-in where possible, and on Windows the Grep tool finds `resources\rg`. A second process exits without a second window. An HTTP failure string in the chat does not contain `docker compose`.

Effort: 2 days. Depends on M1.2.

#### M1.5 Inference supervisor lifecycle

Files: `LlamaServerSupervisor.cs`, `PortAllocator.cs`, `ServerPage.xaml.cs`, `SupervisorTests.cs`.

Work is specified in section 5. Short version: fix log lifetime, choose the port before building the command, reuse a healthy server on the preferred port, fall back when the port is foreign, capture logs under `%LOCALAPPDATA%\OpenMono\logs`, restart with backoff, stop with a kill of the process tree, and never start a second server if the first is healthy.

Acceptance: unit tests cover port reuse versus fallback using a fake probe. A Windows manual start writes a log file that still receives lines after `StartAsync` returns. Killing the process and pressing Restart reaches `/health` again.

Effort: 3 days. Depends on M1.1. Can proceed in parallel with M1.2.

#### M1.6 GPU flavor and model download on first run

Files: `ModelDownloader.cs`, `WizardPage`, new `FlavorInstaller.cs` in Models or Supervisor, `fetch-llama.ps1`, `models.json`, `pin-checksums.ps1`.

Work is specified in section 5. The wizard calls the same downloader for the GGUF, the mmproj, and the llama.cpp zip. CPU flavor can also be fetched at build time into the publish tree so a machine with no network can still be offered the CPU server if the model was pre-staged. Checksums get real SHA256 values. Empty hash remains a dev-only skip, and Release builds treat an empty hash as a failed pin: gate this on an MSBuild `DefineConstants` symbol (e.g. `WINDOWS_STRICT_CHECKSUMS` in Release), checked by `ChecksumVerifier`, so tests can exercise both paths without a Release-only build.

Acceptance: cancel mid-download, start again, and the `.part` file resumes (the existing test already covers Range). A mismatched checksum deletes the partial and fails after one retry. Free space under model plus mmproj plus 10 percent blocks the download and offers another folder.

Effort: 4 days. Depends on M1.5 for "start server after download." The downloader itself depends on nothing.

#### M1.7 Wizard, server page, and chat wired into one flow

Files: `WizardPage.xaml`, `ServerPage.xaml`, `ChatPage.xaml`, `MainWindow.xaml.cs`, `AppState.cs`, new `FirstRunState.cs` stored at `%LOCALAPPDATA%\OpenMono\app.json`.

Work: implement the steps in section 7. `NeedsFirstRun` becomes "app.json says the wizard finished, the model file exists, and a llama-server binary exists." The wizard's last button starts inference, waits for healthy, writes settings, and navigates to Chat. Server page Start, Stop, and Restart use the fixed supervisor. Chat uses the session from M1.3.

Acceptance: the M1 exit paragraph above, against a stub llama-server in a dev run, and against a real CPU `llama-server` once Spencer has a Windows machine. CI smoke still uses the stub.

Effort: 4 days. Depends on M1.3, M1.5, and M1.6.

#### M1.8 Docker detection and stack start, with a packaged compose tree

Files: `DockerComposeManager.cs`, `DockerDetector.cs`, new `stage-docker.ps1`, `WizardPage`, `ServerPage` or Settings.

Work is specified in section 6. Detection and the skip path ship in M1. A failed Docker start must not block chat. Gateway URL is applied to `AppConfig.Web.Gateway` only after Caddy `/health` succeeds.
Remove the eager assignment in `AgentHostFactory.CreateConfig` (which sets `Web.Gateway`
whenever `DockerServicesEnabled` is true) so a session created before Docker is healthy
never points WebSearch at the llama-server port: leave `Web.Gateway` null until the
health check passes, then set it plus explicit `Web.SearchEnabled`/`Web.ScrapeEnabled`
(this also bypasses the `GatewayCapabilities` static probe cache, which has no
invalidation). Until then, `ResolveGateway` falls back to the LLM endpoint and the
tools use DuckDuckGo and direct fetch.

Acceptance: with Docker absent, the wizard can skip, chat works, and WebSearch does not call port 47480. With Docker present, `docker compose` is invoked with the two `-f` files from the install directory, and llama-server and agent containers stay stopped.

Effort: 3 days. Depends on M1.7 for the wizard step. The compose manager fix can start earlier.

#### M1.9 Per-user installer that contains the app and the CPU server

Files: `build.ps1`, `openmono.iss`, `app.manifest` (already long-path aware).

Work is specified in section 7. Self-contained publish, VC++ check, CPU `llama-server.exe` and `rg.exe` inside the installer, GPU zips and GGUF files outside it, uninstall process kill, keep-data default. Signing step remains the existing skip.

Acceptance: `windows-latest` uploads `windows/dist/OpenMonoSetup.exe` (see the path-unification
note in section 7 — the workflow, the iss `OutputDir`, and the `build.ps1` sign path must
all agree). Installing it on a clean user account does not elevate. The Start Menu shortcut launches `OpenMono.exe`. Uninstall removes the exe and leaves models unless the user checks delete.

Effort: 3 days. Depends on M1.4 (rg staging) and M1.6 (CPU binary staging). Can land before the wizard is pretty.

M1 total is about 26 days of work, with M1.2 and M1.5 parallel after M1.1, and M1.9 overlapping the end of M1.6.

### M2. Daily-driver chat, Windows tool parity, Velopack

Exit: a beta user on an NVIDIA machine and on a CPU machine can chat, approve tools, copy and paste, switch models, search the web through Docker when it is up, and update the app without a manual reinstall. Hooks the user configured run in PowerShell. LSP hover works for C# via Roslyn and for TypeScript via a bundled server.

#### M2.1 Windows hook executor

Files: new `WindowsToolExecutor.cs`, `HookShellSelector.cs`, `AgentHostFactory.cs`.

Work is specified in section 4. Pass this executor into `ConversationLoop` so `HookRunner` is not the process that runs user hooks.

Acceptance: a pre-tool hook whose command is `exit 2` in PowerShell blocks the tool and the chat shows the hook stderr. A post-tool hook appends a line. No test process is started with `FileName` `/bin/bash`. Read `AgentTool`'s child-`ConversationLoop` construction path and verify in code review that the child loop cannot receive our executor (the sub-agent hook limitation in section 4 rests on this). Sub-agent limitation in section 4 is covered by a test that child config hooks are empty.

Effort: 3 days. Depends on M1.2.

#### M2.2 Chat UI parity

Files: `ChatPage.xaml`, new view models under `windows/src/OpenMono.Desktop/ViewModels/`, `ChatOutputSink.cs`.

Work: message list with markdown (Markdig is already an OMA dependency; the desktop project can reference the same package), streaming that updates one bubble, thinking collapsed after the first text token (`CollapseThinking` already fires), tool calls as expandable cards (`WriteToolStart`, `WriteToolSuccess`, `WriteToolError`, `WriteToolDiff`), permission dialog with Allow once, Allow for session, Deny, and Deny for session (the enum exists; the dialog only has three buttons today), slash palette that inserts the command and dispatches it, Stop that cancels the turn `CancellationTokenSource`, and a status line with tok/s from `TurnMetrics.GenTokensPerSecond`. Marshal every sink event to the UI thread. The sink events currently fire on the model-read thread.

Acceptance: a scripted turn against the stub server shows a growing assistant bubble, a collapsed thinking row, a tool card, and a dialog that returns Allow for session. Stop leaves the session usable for the next turn.

Effort: 5 days. Depends on M1.7.

#### M2.3 Clipboard

Files: `ClipboardService.cs`, `ChatPage.xaml.cs`.

Work: implement the delegates with `Windows.ApplicationModel.DataTransfer.Clipboard` (WinUI). Copy on the assistant bubble and paste into the input box. Optionally watch `%USERPROFILE%\.openmono\.clipboard-out` and mirror new contents, matching the Linux bridge file name in `ClipboardService.BridgeFilePath`. The native chat path does not use `AnsiInputReader`, so OSC 52 is out of scope.

Acceptance: copy from a message and paste into Notepad. Paste from Notepad into the composer.

Effort: 1 day. Depends on M2.2.

#### M2.4 LSP and MCP for real

Files: new `WindowsLspTool.cs`, new `WindowsLspClient.cs`, `LspPathMapper.cs`, `McpLaunchHelper.cs`, `ToolGraphBuilder.cs`, `fetch-node.ps1`.

Work is specified in section 4. Roslyn stays the C# path. TypeScript uses a bundled `typescript-language-server.cmd` launched through the MCP-style `.cmd` rule, with URIs from `LspPathMapper`. MCP configs are rewritten before `McpServerManager.InitializeAsync`.

Acceptance: a unit test maps `C:\code\App.cs` to `file:///C:/code/App.cs` and back. A fixture `.cmd` config becomes `cmd.exe /d /c`. On a Windows machine with Node, hover on a `.ts` file returns text. A missing server produces one warning line and the turn continues.

Effort: 4 days. Depends on M1.2.

#### M2.5 Model switch, custom GGUF, diagnostics, tray

Files: `ModelsPage`, `LlamaServerSupervisor`, `DiagnosticsService`, new `TrayIcon.cs`.

Work: switching models stops the server, rewrites the command, starts, and waits for `/health` before the next turn. Custom `.gguf` files in the models directory appear in the list. Vision mmproj is optional per model. Diagnostics writes the existing bundle plus the Docker status line and the last 80 server log lines. A tray icon shows starting, ready, busy, and error, and offers Start, Stop, Restart, and Open.

Acceptance: switch from the CPU model alias to a second small fixture file against the stub server (the stub ignores the model, so this test checks process restart and settings write). The real GGUF switch is on Spencer's hardware matrix.

Effort: 3 days. Depends on M1.5 and M1.7.

#### M2.6 Velopack updates

Files: `UpdateService.cs`, `new-release.ps1`, desktop csproj package reference, `build.ps1`.

Work: add the Velopack package. Check on launch and once a day. Prompt with the release notes. Download the delta. Apply on next start. Never restart mid-turn. Models and GPU zips stay on the in-app downloader, not in the Velopack feed. `openmono upgrade` in the CLI stays untouched.

Acceptance: a local Velopack feed with two versions upgrades a test install. A running turn is not aborted when the check returns.

Effort: 3 days. Depends on M1.9.

#### M2.7 Guardrail and prompt polish

Files: `WindowsGuardrails.cs`, `WindowsPermissionDefaults.cs`, `WindowsToolExecutor.cs`, `PromptSnapshot.txt`.

Work: the executor pre-check in section 4 denies credential paths and ADS writes before `LocalToolExecutor`. Strict versus Standard is a wizard choice that adds or omits the ask globs. Re-sync `PromptSnapshot.txt` if `SystemPrompt.Base` has changed since the snapshot.

Acceptance: the existing guardrail tests still pass, plus new cases for `Remove-Item -Recurse C:\Users`, `reg add`, and a filename containing `:stream`. Standard mode asks for `reg add`. The deny list still denies `format E:`.

Effort: 2 days. Depends on M2.1.

M2 total is about 21 days. M2.2 and M2.4 can run in parallel after M1.

### M3. 1.0 hardening and optional enterprise MSI

Exit: soak notes from the hardware matrix are addressed, the installer is what a beta user can keep installed, and the WiX package exists for Spencer to hand to an enterprise pilot if he wants it. Web search through Docker is reliable. Signing is still a no-op without a certificate.

#### M3.1 Soak, warmup, and crash recovery

Files: `LlamaServerSupervisor.cs`, new `WarmupClient.cs` in the supervisor, `ChatPage`.

Work: port the behavior of `IsServerWarmAsync` and `SendWarmupAsync` in `Program.cs` (GET `/metrics`, look for `llamacpp:prompt_tokens_total`, otherwise POST a one-token completion with tools). Show tok/s from `/metrics` on the server page. On llama-server crash, restart with backoff (2s, 4s, 8s, stop after 3) and post a chat system line. Confirm the OMA loop still compacts by running a long fixture conversation against the stub and asserting a compact marker or a shortened history. Doom-loop handling stays inside `ConversationLoop`. Do not reimplement it.

Acceptance: kill `llama-server.exe` during idle and see one restart that becomes healthy. A stub that returns the same tool call three times ends the turn with the OMA doom-loop message rather than a hung UI.

Effort: 4 days. Depends on M2.2 and M1.5.

#### M3.2 Language servers beyond TypeScript, and multi-workspace

Files: `WindowsLspClient.cs`, Settings page, `AppState.cs`.

Work: detect `pylsp.exe`, `gopls.exe`, and `rust-analyzer.exe` on PATH. Do not bundle Python, Go, or Rust. Remember the last workspace and offer it on launch. A workspace change rebuilds the session (LSP root, path guard root, prompt). One workspace is active at a time.

Acceptance: a missing `gopls` leaves Go files on the file tools only, with a single settings warning. Switching workspace updates `AppConfig.WorkingDirectory` and the next turn's system prompt.

Effort: 3 days. Depends on M2.4.

#### M3.3 WiX MSI per machine, offline model import, Intune notes

Files: new `windows/installer/wix/Product.wxs`, `docs/enterprise.md`, Models page import button.

Work: WiX v5 builds an optional MSI that installs under Program Files and requires elevation. Shared models live in `%ProgramData%\OpenMono\models` with a modify ACL for Users. Per-user settings stay in `%USERPROFILE%\.openmono`. The Inno per-user package remains the default download. Document the Intune line-of-business app flow in `docs/enterprise.md` without claiming it was tested in a tenant. Offline import copies a local `.gguf` into the models directory and runs the checksum if one is pinned.

Acceptance: `dotnet build` or the WiX CLI produces an MSI on `windows-latest`. Installing it side by side with the per-user app is not required. The import button accepts a local file and lists it.

Effort: 4 days. Depends on M1.9.

#### M3.4 Docker failure UX and gateway health

Files: `DockerComposeManager.cs`, Settings page, `HealthPoller.cs`.

Work: section 6 failure states, a Repair action that runs `docker compose up` again, and a clear line when WSL2 is not enabled. Poll Caddy `/health` on port 47480 before setting `Web.Gateway`.

Acceptance: stopping Docker Desktop mid-session flips the server page to "web services stopped, search is using DuckDuckGo" without failing the next chat turn.

Effort: 2 days. Depends on M1.8.

#### M3.5 Manual matrix fixes

No fixed file list. Budget time to patch whatever the NVIDIA, AMD, Intel, and CPU runs in section 8 find. Typical fixes are driver-version parsing, Vulkan layer load order, ubatch size on 12GB, and log paths when the models drive is not the system drive.

Effort: 5 days, after Spencer returns the matrix notes. Depends on M2 being installed on those machines.

M3 total is about 18 days, plus whatever the matrix adds.

## 4. Agent integration

### How the agent is hosted in process

The desktop process does not start `openmono.exe`. `AgentHostFactory.CreateSession` builds the same graph `Program.cs` builds, then `ChatPage` calls `ConversationLoop.RunTurnAsync`.

Construction order:

1. `SupervisorConfig` from `%LOCALAPPDATA%\OpenMono\app.json` plus the OMA settings file.
2. `AppConfig` with `WorkingDirectory`, `DataDirectory` (`%USERPROFILE%\.openmono`), `Llm.Endpoint` (`http://127.0.0.1:<port>`), `Llm.Model` (alias without `.gguf`), `Llm.ContextSize`, `Llm.ApiKey` when set, `VisionEnabled`, and `Web.Gateway` only after the Docker health check.
3. `WindowsPermissionDefaults.Apply(config)`.
4. Copy `config.Hooks` into a `WindowsHookCatalog`, then set `config.Hooks` to empty lists so OMA `HookRunner` has nothing to run.
5. `MemoryStore`, playbook loader, registry, and executor.
6. `WindowsPromptBuilder` system message on a new `SessionState`.
7. `ToolGraphBuilder` registry.
8. `ChatRenderer` (sink plus input plus live feedback).
9. `PermissionEngine(config, renderer, renderer)`.
10. `OpenAiCompatClient`. Set `OnDebug` to the sink's `WriteDebug`. Set `OnModelReported` to update `config.Llm.Model` when `/props` disagrees with the alias, matching `Program.cs`.
11. `WindowsToolExecutor` wrapping a `LocalToolExecutor`. The inner executor gets a `HookRunner` constructed after hooks were cleared, so its pre and post hooks return immediately.
12. `ConversationLoop` with that executor, the compactor, the checkpointer, and the memory store. Leave `maxIterations` at the OMA default.
13. `McpServerManager.InitializeAsync` with configs passed through `McpLaunchHelper.Resolve`.
14. If the ACP toggle is on, build the same `ServiceCollection` as `Program.cs` (config, llm, tools, sink, reader, `ConversationLoopFactory`, `AcpHostedService`) and call `StartAsync`. Bind localhost only. `AcpServerSettings.BindAllInterfaces` stays false. Ignore `/.dockerenv`.

`AgentTool` copies tools from `context.ToolRegistry`, so the sub-agent inherits `WindowsShellTool` and does not construct `BashTool`. The child `ConversationLoop` does not receive our executor. Because `config.Hooks` is empty, the child `HookRunner` also no-ops. User hooks do not run inside sub-agents. That is an accepted limitation until the upstream shell change in the list below.

There is no Microsoft.Extensions.DependencyInjection container for tools. OMA uses `ServiceCollection` only for the ACP host. "Replace via DI" in this product means pass a different `ITool`, `IToolExecutor`, `IOutputSink`, and `IInputReader` into the public constructors. `ToolRegistry.Register` replaces a tool by name because the dictionary assignment overwrites.

### Services the host replaces

| OMA type the CLI would use | What the Windows host passes | Why |
|---|---|---|
| `BashTool` | `WindowsShellTool` registered as `Bash` | `BashTool` sets `FileName = "/bin/bash"` in both the foreground and background paths, and sets `HOME` and `PATH` to Unix defaults. |
| `HookRunner` execution | `WindowsToolExecutor` plus a cleared `HookConfig` | `ExecuteHookAsync` is private and not virtual. A subclass cannot change the shell. Clearing hooks makes the concrete runner a no-op. |
| `InitCommand` | `WindowsInitCommand` | The Makefile probe is a bash pipeline. `CommandRegistry.Register` overwrites by name, so the Windows command is the one that runs. |
| `LspTool` and `LspClient` | `WindowsLspTool` and `WindowsLspClient` | URI strings are built inside `LspClient` with `$"file://{path}"`. The type is sealed in behavior (private constructor, static start). Replacing the tool avoids that client. |
| `McpClient` launch | Rewritten `McpServerConfig` before `McpServerManager` | `McpClient.ConnectAsync` assigns `FileName = config.Command` and puts args in `ArgumentList`. If `Command` is already `cmd.exe` and the args are `/d /c <shim> ...`, the unmodified client works. |
| `IRenderer` terminal | `ChatRenderer` | The loop only needs `IOutputSink`, `IInputReader`, and optional `ILiveFeedback`. |
| System prompt builder | `WindowsPromptBuilder` | `SystemPrompt` is internal. |
| llama recovery in `Program.cs` | Not invoked | Recovery lives in the supervisor. |

`PermissionEngine`, `PathGuard`, `SanityCheck`, `LocalToolExecutor`'s schema and cache pipeline, `OpenAiCompatClient`, `Compactor`, `Checkpointer`, `SessionManager`, `WebSearchTool`, `WebFetchTool`, and `RoslynTool` stay the OMA types.

### Each Windows blocker

**`/bin/bash` in `BashTool`.** Do not register `BashTool`. `WindowsShellTool` uses `HookShellSelector.SelectShell`: `pwsh.exe` if `where.exe` finds it, otherwise `powershell.exe`, otherwise `cmd.exe`. Foreground commands redirect stdin, stdout, and stderr, close stdin, and kill the tree on timeout (`Process.Kill(entireProcessTree: true)`), with the same 300000 ms default and 600000 ms cap as `BashTool`. Background mode writes `%TEMP%\openmono\bg\bg-*.log` and returns `Get-Content -Tail` and `Stop-Process` text. Working directory is the workspace for every call. The tool does not set `HOME` to `/root`.

**`/bin/bash` in `HookRunner`.** `WindowsHookCatalog` holds the user's `HookConfig`. `WindowsToolExecutor.ExecuteAsync` runs pre-hooks before `LocalToolExecutor` and post-hooks after it. The shell is the same selector. Template replacement matches `HookRunner`: `{{tool_name}}`, `{{tool_input}}`, `{{tool_output}}`. Exit code 2 blocks the tool. Timeout is 30 seconds. Session-start hooks run once from the factory before the first turn. The `HookRunner` instance inside `LocalToolExecutor` sees an empty list.

**`rg` for `GrepTool`.** Bundle `rg.exe` and prepend its directory to `PATH` at startup. `GrepTool` is unchanged and already errors clearly when `rg` is missing. The chat page turns that error into a repair line that points at Settings, Repair tools.

**Clipboard.** The chat page calls WinUI `Clipboard` for copy and paste. `ClipboardService` keeps the delegates so tests can pass fakes. The file bridge watcher is optional and only mirrors `.clipboard-out` into the WinUI clipboard. `AnsiInputReader` is not on this path.

**LSP file URIs.** `WindowsLspClient` calls `LspPathMapper.ToFileUri` for `rootUri`, `textDocument/didOpen`, hover, definition, and references, and `FromFileUri` when reading results. `ToFileUri` is `new Uri(Path.GetFullPath(path)).AbsoluteUri`, which yields `file:///C:/...` with escaped spaces. Language commands are absolute paths: `omnisharp.exe` is not required for C# because `RoslynTool` loads the file into an `AdhocWorkspace`. TypeScript uses the bundled `typescript-language-server` via `cmd.exe /d /c` when the resolved file ends in `.cmd`. Python, Go, and Rust wait until M3 and only start if the exe is on PATH. `LspServerManager.DefaultServers` stays unused so it cannot spawn a bare `omnisharp` or `pylsp` name.

**MCP `.cmd` shims.** `McpLaunchHelper.Resolve` already returns `cmd.exe` with `/d /c` and a quoted command line for `.cmd`, `.bat`, and `.ps1`. The factory maps every `McpServerSettings` through that helper, then passes `McpServerConfig`. `where.exe` plus the known `%ProgramFiles%\nodejs` location resolves `node` and `npx`. `fetch-node.ps1` can drop a portable Node under `{app}\resources\node` and the helper checks that folder before PATH. `UseShellExecute` stays false inside OMA, which is what we want once the command is `cmd.exe`.

**Guardrail paths.** `WindowsShellTool.RequiredPermission` returns `PermissionLevel.Deny` when `WindowsGuardrails.IsDestructive` or `SanityCheck.IsDestructiveCommand` is true, and `Ask` otherwise. `WindowsPermissionDefaults` merges globs into `config.Permissions.Tools["Bash"]`: allow `git *`, `dotnet *`, `npm *`, `rg *`, `Get-ChildItem *`, `Get-Content *`; deny `format *`, `diskpart*`, `bcdedit*`, `reg delete*`, recursive `C:\` deletes, `*.env`, `*.pem`; ask for `runas`, `reg add`, `sc`, `schtasks`, and `-EncodedCommand`. User globs already in the file are kept. `WindowsToolExecutor` adds a pre-check for file tools: reject paths that contain an alternate data stream (`name:stream` with a colon after the drive colon), and reject writes whose full path sits under known credential locations (`\AppData\Local\Microsoft\Credentials`, browser profile directories, `NTUSER.DAT`, `SAM`). `PathGuard` already handles case, UNC, and device names (`CON`, `PRN`, and the rest). File tools keep calling it. The executor pre-check covers the cases `PathGuard` does not.

**`InitCommand` `head`, `grep`, and `sed`.** `WindowsInitCommand` parses the Makefile in managed code. The rest of init (stack detection, git branch, conventions) can call the same public helpers `InitCommand` uses (`StackDetector`, `GitHelper`) or shell out through `ProcessRunner`, which already selects `cmd.exe /c` on Windows. Do not pass the Makefile pipeline to `ProcessRunner`.

**Localhost and Docker recovery assumptions.** `TryRecoverLlamaServerAsync` lives in `Program.cs` and runs only from the CLI turn loop. The Windows host never calls it. Endpoint is the supervisor port. Chat HTTP failures call `LlamaServerSupervisor.StartAsync` again. Docker is for Caddy, SearXNG, and Scrapling only. `GatewayCapabilities.ResolveGateway` returns `Web.Gateway` when set, otherwise the LLM endpoint. Leaving `Web.Gateway` null until Caddy is healthy prevents WebSearch from treating llama-server port 7474 as a search gateway.

**Prompt overlay.** The overlay text in `WindowsSystemPromptOverlay` is appended to every system message: PowerShell syntax, native `git` and `dotnet` and `npm` and `rg`, `Get-Content` and `Stop-Process`, drive-letter paths, and a ban on credential stores. It does not replace the base coding rules.

**Other OMA behavior that already works on Windows and needs no wrapper.** `FileWriteTool` and `FileEditTool` branch on `OperatingSystem.IsWindows()` for `attrib -r`. `DesktopNotifier` already shells to PowerShell for toasts. `ProcessRunner` already uses `cmd.exe`. `AppConfig` data directory uses `SpecialFolder.UserProfile`, which is `%USERPROFILE%\.openmono`.

### Upstream changes that would be cleaner

These are not scheduled. They need an edit outside `windows/`, which this product does not do unless Spencer approves a separate change. The additive workaround is in place for each.

1. `BashTool` and `HookRunner` hardcode `/bin/bash`. A public `IShellLauncher` or an `OPENMONO_SHELL` environment variable, defaulting to `/bin/bash` on Unix and `pwsh` or `powershell` on Windows, would let sub-agents run hooks too. The Windows executor cannot be injected into `AgentTool`'s child loop without editing `AgentTool`.
2. `SystemPrompt` is internal, and `InternalsVisibleTo` lists only `OpenMono.Tests`. Making `SystemPrompt` public, or adding `InternalsVisibleTo` for `OpenMono.Windows.AgentHost`, would remove the prompt snapshot and the drift risk.
3. `LspClient` should build URIs with `new Uri(path).AbsoluteUri` and parse them with `LocalPath`. The Windows client can then be deleted and `LspTool` can be registered with `LspServerManager.Configure` pointing at `.exe` paths.
4. `McpClient` should, on Windows, launch `.cmd` and `.bat` through `cmd.exe /d /c`. The config rewrite in `McpLaunchHelper` would then be a path resolver only.
5. `SanityCheck` patterns are Unix (`dd`, `/etc`, `chmod`). Adding the Windows deny list there would protect any host, including the CLI on Windows, not only this app.
6. `InitCommand` should read Makefile targets in managed code, or ask `ProcessRunner` only for commands that exist on the current OS.
7. `WriteSettings` compatibility is entirely on our side. No upstream change is required once we serialize with `JsonOptions`.

Spencer decides whether any of these land. None of them block M1 if the workarounds above are finished.

## 5. Inference supervisor

### Process lifecycle

`LlamaServerSupervisor` owns one `Process`.

Start:

1. Resolve the flavor directory: `%LOCALAPPDATA%\OpenMono\bin\llama-server\{cuda|vulkan|cpu}\llama-server.exe`. The installer may also ship a CPU copy under `{app}\resources\llama-server\cpu` as a fallback when the user has not downloaded a flavor yet.
2. Require the GGUF and, when vision is on, the mmproj. Missing files throw a message that names the wizard, which is what the scaffold already does.
3. Choose the port (below) and only then call `BuildLlamaCommand`, so `--port` matches the port we will probe.
4. Set `LLAMA_API_KEY` in the process environment when an API key exists. Also pass `--api-key` as the scaffold does.
5. Working directory is the folder that contains `llama-server.exe` and its CUDA or Vulkan DLLs. Official zips ship `cublas64_*.dll` and `cudart64_*.dll` beside the CUDA server, and `vulkan-1.dll` beside the Vulkan server. `fetch-llama.ps1` already copies those next to the exe. Do not depend on a system CUDA toolkit.
6. Redirect stdout and stderr to a `StreamWriter` on `%LOCALAPPDATA%\OpenMono\logs\llama-server-<utc>.log` that lives until `StopAsync`. The current `await using` inside `StartAsync` closes the log when start returns. Move the writer to a field and dispose it in `StopAsync`.
7. Poll `/health` every 5 seconds for 180 seconds via `HealthPoller`. On success, read the model from `/props` then `/v1/models`.
8. Optional warmup from M3: skip if `/metrics` shows `llamacpp:prompt_tokens_total` greater than zero, otherwise POST one token.

Stop: there is no HTTP shutdown in the llama.cpp server we pin. `StopAsync` calls `Kill(entireProcessTree: true)`, waits 10 seconds, then disposes. Uninstall also runs `taskkill /F /IM llama-server.exe`, which the Inno script already has.

Restart and crash: `Exited` handler restarts after 2s, then 4s, then 8s, then stops and sets state to error. A user Restart resets the counter. Do not restart while a stop was requested.

Busy versus ready: the tray and server page read `IsRunning` plus the last health result. The chat page sets busy for the duration of `RunTurnAsync`.

### Health checks

`HealthPoller.IsHealthyAsync` treats any HTTP success on `/health` as healthy. `WaitForHealthyAsync` returns attempts and elapsed time. Model detection matches `TryDetectActualModelAsync` in `Program.cs`: `/props` field `default_generation_settings.model`, then `model`, then `/v1/models` data[0].id.

Before a chat turn, if the last health check is older than 30 seconds, probe once. On failure, try one supervisor restart, then show the log tail (last 40 lines) in the chat system line. Include the llama-server line that mentions CUDA, Vulkan, or `out of memory` when it is present. Do not suggest `docker logs`.

### Port conflicts

Preferred port is 7474. ACP is 7475. Gateway is 47480. All three are configurable in `app.json`.

Algorithm in `PortAllocator.SelectInferencePort`:

1. If nothing listens on 7474, use it.
2. If something listens, GET `/health`. On HTTP 200, GET `/props`. If the body parses as our server (the props JSON shape `HealthPoller` already accepts) or the port was recorded in `app.json` as ours, keep 7474 and do not start a second process. `LlamaServerSupervisor` attaches by storing the endpoint and skipping `Process.Start`.
3. If the listener is not ours, walk `8081`, `8082`, `8083`, `8084`, `8085`, `9080` and take the first free port. Write it to `app.json` and to `settings.json` `llm.endpoint`.
4. If every fallback is taken, show a blocking error that names the busy ports. Do not scan the whole ephemeral range.

`ServerPage` must pass the chosen port into `SupervisorConfig.LlamaPort` before `BuildLlamaCommand`. The scaffold does this in the wrong order.

ACP uses the same helper with preferred 7475 and fallbacks `7476` through `7480`. Never bind `0.0.0.0`.

### GPU flavor selection and fallback

`ModelTierSelector.Select` is the policy. Feed it GPUs from this order:

1. `nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits`. Dedicated bytes are MiB times 1024 squared. CUDA is available when `nvidia-smi` exits 0. Record the driver version for the diagnostics bundle.
2. If that returns nothing, DXGI adapters (a small helper in `GpuDetector`, P/Invoke `CreateDXGIFactory1` and `EnumAdapters1`, dedicated and shared video memory). Keep adapter parsing as a pure function over a record the detector fills, so the Linux unit tests cover vendor mapping and dedicated-versus-shared splitting without P/Invoke. This splits AMD and Intel dedicated VRAM from shared memory better than WMI `AdapterRAM`, which often reports a bogus 4GB cap. Map vendor ids `0x10DE` NVIDIA, `0x1002` AMD, `0x8086` Intel.
3. WMI `Win32_VideoController` remains the last resort, which the scaffold already has.

Flavor:

- Tier 24, 16, or 12 and NVIDIA with CUDA: CUDA build, `--n-gpu-layers 99`.
- Tier 24, 16, or 12 and AMD, Intel, or NVIDIA without a working CUDA server: Vulkan build, `--n-gpu-layers 99`. On 12GB set `--ubatch-size 512`. The scaffold currently always sends 1024.
- Under 12GB dedicated, or no adapter: CPU build, `--n-gpu-layers 0`, threads equal to physical cores (`MemoryDetector.RecommendThreads`). Warn when RAM is under 20GB, matching `install.sh`.

KV cache matches `install.sh`: `q8_0` at tier 24 and on CPU, `q4_0` at 16 and 12. Context matches the constants in section 1, and vision uses `172032` at 24GB and CPU and `98304` below 24GB, which is what `install.sh` does in the mmproj branch (`CTX_VISION` versus `CTX_VISION_16G`).

Fallback when the chosen server exits before `/health`:

1. If CUDA failed, write the log tail, download or select the Vulkan zip if it is not present, and try once.
2. If Vulkan failed, try the CPU build and the CPU model tier, and tell the user the accuracy and size change before downloading the 35B MoE file.
3. Do not loop flavors forever. Three attempts, then stop at the error state with the log path.

Driver missing: do not bundle GPU drivers. Link to NVIDIA, AMD, and Intel download pages from the wizard. CUDA selection is blocked with a sentence that names the missing `nvidia-smi`. Vulkan and CPU stay available.

### Model download

Source URLs are the Hugging Face `unsloth` links in `models.json`, identical to `select_model`. `OPENMONO_MODEL_MIRROR` prefixes the path the way `ModelRegistry.ApplyMirror` already does.

Files:

- `%LOCALAPPDATA%\OpenMono\models\<file>.gguf`
- the matching `mmproj-*.gguf` when vision is on
- `%LOCALAPPDATA%\OpenMono\bin\llama-server\<flavor>\` for the zip contents

The downloader already writes `destination + ".part"`, sends `Range` when the partial is non-empty, and if the server answers 200 instead of 206 it deletes the partial and starts over. Add a pause flag: a `CancellationToken` for user pause that is distinct from wizard cancel, leaving the `.part` in place. Cancel from the wizard can keep the partial too. Only checksum failure deletes it.

Checksum: `pin-checksums.ps1` downloads each URL once, computes SHA256, and writes `sha256`, `mmprojSha256`, `cudaSha256`, `vulkanSha256`, `cpuSha256`, and the ripgrep hash into `models.json`. Commit those pins on the Windows branch. `ChecksumVerifier` fails the file when the hash does not match. Release configuration treats an empty expected hash as an error so a forgotten pin cannot ship. Debug keeps the current skip so local fixtures work.

Disk: before the first byte, `DiskDetector.HasRoom` requires `modelBytes + mmprojBytes` times 1.10, plus the zip size (use the Content-Length from a HEAD, or 200MB if HEAD fails). If the drive is short, the wizard asks for another folder and persists `ModelsDirectory` in `app.json`. Models stay out of `%USERPROFILE%\.openmono` so sessions and settings are not mixed with multi-gigabyte files.

Progress UI: file name, received bytes, total, percent, and a simple ETA from the last 10 seconds of throughput. One file at a time (flavor zip, then model, then mmproj) so a pause has one partial.

### Switching models

Models page lists every `.gguf` in the models directory that is not an mmproj, plus the four known tiers. Selecting one:

1. Refuse while a turn is running.
2. `StopAsync` on llama-server.
3. Update `SelectedTier` or the custom file name, context, and mmproj flag.
4. `WriteSettings` so `llm.model` and `inference.ctx_size` match.
5. `StartAsync` and wait for healthy.
6. Rebuild the agent session so the system prompt names the new model. Keep the transcript in the UI, and start a new `SessionState` so the old context window does not carry into a smaller model. Tell the user the transcript stayed on screen and the model context was reset.

Deleting a model requires a confirm dialog and refuses to delete the file that is loaded.

## 6. Docker stack management

Docker Desktop with the WSL2 backend runs three services. Native llama-server stays on the host so CUDA and Vulkan see the GPU. The containerized `llama-server` and `agent` services are moved to profile `never-start-on-windows` by `windows/docker/docker-compose.windows.yml` and are not named on the `up` command.

### What the override contains

The file already:

- Sets `llama-server` and `agent` to profile `never-start-on-windows` and `restart: "no"`.
- Sets Caddy `LLAMA_UPSTREAM` to `host.docker.internal:${LLAMA_PORT:-7474}`.
- Sets `SEARXNG_UPSTREAM=searxng:8080` and `SCRAPLING_UPSTREAM=scrapling:5000`.
- Defaults `WEB_SEARCH_ENABLED` and `WEB_SCRAPE_ENABLED` to true (the upstream compose file defaults them to false, and the override replaces those keys).
- Adds `extra_hosts: host.docker.internal:host-gateway` so the Linux VM can route to the Windows host. The upstream Caddy service already has this line. The override repeats it so a future compose merge cannot drop it.

`DockerComposeManager.BuildEnvironment` also sets `LLAMA_UPSTREAM`, `LLAMA_PORT`, `GATEWAY_PORT` (47480), and the two web flags. Caddy publishes `${GATEWAY_PORT:-47480}:8080`. The agent then uses `http://127.0.0.1:47480` as `Web.Gateway`. WebSearch calls `{gateway}/search`. WebFetch calls `{gateway}/scrape`. Both already fall back when the gateway errors.

### Packaged compose tree

`stage-docker.ps1` copies, without modifying, into `{publish}\resources\docker\`:

- `docker/docker-compose.yml`
- `docker/Caddyfile`
- `docker/searxng/` (settings the SearXNG image expects)
- `docker/scrapling/` (build context for the Scrapling image)

Copy `windows/docker/docker-compose.windows.yml` to `{publish}\resources\docker\docker-compose.windows.yml`.

`DockerComposeManager` uses those paths when `AppContext.BaseDirectory` contains `resources\docker\docker-compose.yml`, which is the installed layout. When that folder is missing (a dev build from the repo), it uses the repo paths the scaffold uses today. Working directory for the `docker` process is the folder that contains the base compose file, so `./Caddyfile` and `./scrapling` resolve.

Scrapling is a local image build, not a pulled image. The first `up` needs network and several minutes. Say that on the Docker wizard step. Do not vendor the built image into the installer.

### Detection

`DockerDetector.DetectAsync` runs `docker info --format {{.Server.Version}}` and `docker compose version --short`. Results:

- Engine up and Compose up: offer Start web services.
- Engine missing: Docker Desktop is not installed or not running. Offer Download Docker Desktop (`https://docs.docker.com/desktop/setup/install/windows-install/`) and a Skip button. Skip stores `DockerWanted=false` and shows `DockerDetector.SkippedExplanation`: search uses DuckDuckGo, fetch uses direct HTTP.
- Engine up, Compose missing: tell the user to update Docker Desktop. Do not try `docker-compose` v1.

The app does not silently install Docker Desktop. The user runs Docker's installer. The wizard polls `DetectAsync` every 3 seconds while the user is on that step so a finished install flips the text to "found" without a restart.

WSL2: if `docker info` stderr contains `WSL` or the engine is installed but `docker info` fails with the Docker Desktop "starting" state, show "Docker Desktop is starting" and keep polling for 120 seconds, then show the stderr tail.

### Start and stop

Start, after llama-server is healthy:

```text
docker compose -f "<resources>\docker\docker-compose.yml" -f "<resources>\docker\docker-compose.windows.yml" --profile full up -d caddy searxng scrapling
```

Environment from `BuildEnvironment`, with `LLAMA_PORT` equal to the port actually chosen. Then poll `http://127.0.0.1:47480/health` for 180 seconds. On success, set `AppConfig.Web.Gateway` and `Web.Search` and `Web.Scrape` to null so `GatewayCapabilities` probes `/services` and follows Caddy. On failure, leave the gateway null and keep chatting.

Stop:

```text
docker compose -f ... -f ... stop caddy searxng scrapling
```

Do not `down -v` on ordinary stop. That would drop SearXNG state. Uninstall stop uses `stop`, then `taskkill` is only for `llama-server.exe` and `OpenMono.exe`. Offer "Remove web containers" in Settings, which runs `down` without `-v` unless the user also checks delete volumes.

App exit stops llama-server. It leaves Docker containers running so the next launch is fast, and the server page shows "web services running." A setting "Stop web services when OpenMono exits" defaults off.

### Failure UX

| State | What the user sees | What still works |
|---|---|---|
| Docker not installed | Wizard step with download link and Skip | Chat, tools, DuckDuckGo search, direct fetch |
| Docker installed, engine stopped | "Start Docker Desktop" button that launches `Docker Desktop.exe` from the default install path, then polls | Same fallbacks |
| WSL2 kernel missing | Docker's own message quoted in the details expander, plus the Microsoft WSL install doc link | Same fallbacks |
| Compose build of Scrapling failed | Log tail, Retry, and "Continue without scraping" | Search if Caddy and SearXNG are up, otherwise DuckDuckGo |
| Port 47480 busy | Settings offers 47481 and the next free port, then rewrites `GATEWAY_PORT` and recreates Caddy | Inference on 7474 |
| Docker stops mid-session | Server page status flips, gateway cleared, one chat info line | Next WebSearch uses DuckDuckGo |
| Commercial license | No technical block. Settings shows a note that Docker Desktop's subscription terms are Spencer's decision for company machines | |

The agent must not be told to fix Docker by starting the containerized llama-server. The system overlay can include one line: web search uses the local gateway when it is up, and DuckDuckGo otherwise.

## 7. Installer, update, and first-run wizard

### Build and publish

On a Windows machine or `windows-latest`:

1. `dotnet test` the test project.
2. `dotnet publish windows/src/OpenMono.Desktop/OpenMono.Desktop.csproj -c Release -r win-x64 --self-contained true` into `windows/publish/win-x64`. Self-contained means the machine does not need a separate .NET 10 install. Windows App SDK is self-contained (`WindowsAppSDKSelfContained`).
3. `fetch-rg.ps1` into `publish/win-x64/resources/rg`.
4. `fetch-llama.ps1 -Flavor cpu` into `publish/win-x64/resources/llama-server/cpu` so the installer can boot a CPU server before any download. CUDA and Vulkan zips are not in the installer.
5. `stage-docker.ps1` into `publish/win-x64/resources/docker`.
6. Copy `models.json` and `VERSION.windows`.
7. Download `VC_redist.x64.exe` into `windows/installer/deps` if it is not there. The Inno script already installs it only when the registry key `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64` is missing.
8. If `WINDOWS_CERT_PATH` points at a PFX, `signtool` every exe and dll and then the setup exe, with an RFC 3161 timestamp. If the variable is empty, print the skip line and continue. That is the current script, and it stays that way until a certificate exists.
9. `ISCC.exe windows/installer/inno/openmono.iss`, output `windows/dist/OpenMonoSetup.exe`.
Path unification (fix before run 9): the iss `OutputDir` (`..\..\dist` relative to
`windows/installer/inno/`) already resolves to `windows/dist/`, but
`.github/workflows/windows-desktop.yml` uploads `dist/OpenMonoSetup.exe` (repo root)
and `build.ps1` signs `root/dist/OpenMonoSetup.exe`. Those three must agree on
`windows/dist/OpenMonoSetup.exe`: update the workflow upload path and the `build.ps1`
sign path to `windows/dist/`, and keep the iss file as the source of truth.
CI is red on this until the paths match.

Per-user directory is `%LOCALAPPDATA%\Programs\OpenMono`. No elevation. Start Menu shortcut. Desktop shortcut is an unchecked task. The wizard offers launch on finish.

Uninstall, already sketched in the iss `[Code]` block and to be finished:

1. `taskkill /F /IM OpenMono.exe` and `taskkill /F /IM llama-server.exe`.
2. Ask, default unchecked: delete `%LOCALAPPDATA%\OpenMono\models`, delete `%USERPROFILE%\.openmono`.
3. Also delete `%LOCALAPPDATA%\OpenMono\bin` and `logs` with the app, because those are not the user's models or sessions. The current script only deletes models and `.openmono` when checked, and relies on Inno to remove `{app}`.
4. Do not edit the system PATH. The app only prepends `rg` for its own process.

### Velopack

M2 replaces the browser handoff in `UpdateService`. `new-release.ps1` runs `vpk pack` on the publish directory and uploads to a GitHub release. The app calls `UpdateManager.CheckForUpdatesAsync` on launch and on a 24 hour timer. The prompt shows version and notes. Download runs in the background. Apply is "restart to finish," and it is disabled while `RunTurnAsync` is in progress. GPU zips and GGUF files are not in the pack. They stay in `%LOCALAPPDATA%\OpenMono`.

### First-run wizard, step by step

`MainWindow` navigates to `WizardPage` when `FirstRunState.IsComplete` is false. The page is a single WinUI page with a step index, not eight separate windows. Back is allowed until the download step has started. After a partial download, Back does not delete the `.part` file.

1. Welcome. Short text: local model, no token fee after download, the download size for the recommended tier, and the license RTF. Privacy line: no telemetry is sent. Diagnostics bundles are written only when the user clicks the button.
2. Hardware. Call `HardwareReport.Collect` on a background thread. Show GPU name, dedicated VRAM, RAM, physical cores, and free space on the models drive. Show `ModelTierSelector` warnings verbatim (under 12GB, under 20GB RAM on CPU, missing NVIDIA driver).
3. Model. Preselect `registry.ForTier((int)selection.Tier)`. Show label, accuracy (`full`, `standard`, `lower`), size, context, and whether vision will also download the mmproj (about 900MB). The user may pick a smaller tier. Picking a larger tier than VRAM shows the warning and still allows it.
4. Storage. Default `%LOCALAPPDATA%\OpenMono\models`. Show free space and the required bytes from `DiskDetector.RequiredBytes`. Change folder uses a folder picker. Block Next when `HasRoom` is false.
5. Docker. Run `DockerDetector`. If the engine is up, default is "start Caddy, SearXNG, and Scrapling after the model is ready." If not, show the download link and Skip. Skip is always available. The explanation string already states what is lost.
6. Download. GPU flavor zip if the flavor is not already unpacked, then the GGUF, then mmproj if vision is on. Pause, resume, and cancel. Checksum line at the end of each file. Next stays disabled until the model file exists and, if a flavor was required, `llama-server.exe` exists. The CPU binary shipped in the installer satisfies this when the selected flavor is CPU.
7. Workspace. Folder picker, default `%USERPROFILE%\code` or the user profile if `code` does not exist. Explain that file tools stay inside this folder and the temp scratch directory, which is `PathGuard`'s behavior. Store the path in `app.json`.
8. Settings. Endpoint display (the chosen port), model alias, vision on or off, Standard or Strict permissions, ACP loopback off by default with one sentence about a future editor on `127.0.0.1:7475`.
9. Ready. Start writes settings with the snake_case writer, starts llama-server, waits for healthy, starts Docker if it was requested and the engine is up, creates the agent session, sets `FirstRunState.IsComplete`, and navigates to Chat.

If the user closes the app during download, the next launch returns to step 6 and resumes the `.part` files.

### Daily UI after the wizard

Chat is the home page. Models lists installed files, size, context, switch, add custom GGUF, and delete. Server shows a status dot, port, model from `/props`, tok/s, Start, Stop, Restart, log tail, and Copy diagnostics. Settings edits endpoint (a remote URL turns off local llama-server and becomes dual-box client mode), API key, context override, permission lists, a read-only hooks viewer with a PowerShell note, MCP server toggles, Docker start and stop, and the data directory path.

## 8. Testing strategy

### Unit tests that can run on Linux

`OpenMono.Hardware`, `OpenMono.Models`, `OpenMono.Supervisor`, `OpenMono.AgentHost`, and `OpenMono.Windows.Tests` are `net10.0`. Add a workflow job `build-test-linux` on `ubuntu-latest` that builds those projects and runs `dotnet test`, and does not build `OpenMono.Desktop`. The existing Windows job stays.

Cover with xUnit, extending the tests that already exist:

- Tier boundaries at 12, 16, and 24 GiB, CPU below 12, low RAM warning, CUDA versus Vulkan.
- nvidia-smi CSV parsing, including a second GPU line. DXGI parsing can be a pure function over a record the detector fills.
- Disk overhead math.
- `PortAllocator` reuse versus fallback, using a delegate probe so the test does not bind 7474 if the machine is busy. The current `SelectPort` test is the pattern.
- `HealthPoller` against an `HttpListener`, including a non-200 health and a `/v1/models` fallback when `/props` is 404.
- Downloader Range resume, checksum mismatch retry, and mirror URL joining. Already present. Add the empty-hash-fails-in-release behavior behind a flag the test sets.
- Settings round-trip through `ConfigLoader`.
- `ToolRegistry` resolves `Bash` to `WindowsShellTool` and resolves `Grep` to `GrepTool`.
- `WindowsInitCommand` Makefile parse.
- `LspPathMapper` drive letters, spaces, and backslashes. Already present.
- `McpLaunchHelper` `.cmd` wrapping without calling `where.exe` (the pure branch). Already present.
- Hook catalog: given a hook string, `HookShellSelector.WrapCommand` produces a PowerShell `-Command` and a block exit code is interpreted as deny. Run the real `pwsh` only on Windows; on Linux assert the command string.
- Llama command contains the tier's ctx, KV cache, and `--n-gpu-layers`.
- `WindowsPromptBuilder` includes the overlay and the workspace path.

Do not boot WinUI, llama-server, or Docker in these tests.

### `windows-latest` GitHub Actions

Keep `.github/workflows/windows-desktop.yml` as the Windows gate. It already restores, builds the solution, tests, publishes self-contained `win-x64`, signs only when the secret exists, compiles Inno Setup, runs `OpenMono.SmokeTest`, and uploads `OpenMonoSetup.exe`.

Add to that job, without requiring a GPU:

- The smoke test remains the stub HTTP server. Extend it to call `OmaSettingsWriter` and `WindowsPromptBuilder` if those stay out of the WinUI project.
- A step that runs `fetch-rg.ps1` only when the workflow should prove the script. Cache the zip. Do not download the 15GB model.
- Confirm `publish/win-x64/OpenMono.exe` exists and `models.json` sits beside it.
- Confirm the installer artifact exists at `windows/dist/OpenMonoSetup.exe` (not repo-root `dist/`). A silent install on the runner (`OpenMonoSetup.exe /VERYSILENT /NORESTART`) is worth doing once the iss file supports `/VERYSILENT`, then launching `OpenMono.exe` is not, because WinUI on a GitHub-hosted session has no interactive desktop we can trust. Stop at "setup exe produced and, if silent install is added, files land in the runner's LocalAppData."

The workflow triggers on pushes and pull requests to `Windows` when `windows/**` changes. Do not add a trigger on `main`.

### Manual hardware matrix Spencer runs

CI cannot see a GPU. Spencer runs `docs/manual-test-plan.md` on four machines. The plan file already lists the product checks. This build plan adds the pass bar for each machine.

| Machine | Expected tier | Expected flavor | Pass bar |
|---|---|---|---|
| NVIDIA, 24GB or more dedicated | 24, Qwen3.8-27B Q4 | CUDA | `/health` in 180s, `/props` model id matches the alias, a short chat returns text, tok/s shown, vision mmproj loads if enabled and context is 172032 |
| NVIDIA, 16GB | 16, IQ3, lower accuracy warning visible | CUDA | Same, context 180224 without vision and 98304 with vision, no CPU fallback |
| NVIDIA, 12GB, or any 12GB card | 12, Qwen3.5-9B | CUDA if NVIDIA, otherwise Vulkan | Context 196608, ubatch 512, lower accuracy label visible |
| AMD Radeon or Intel Arc | The tier that matches dedicated VRAM, not shared memory | Vulkan | Dedicated VRAM in the wizard matches GPU-Z or Task Manager's dedicated number within 1GB. If Vulkan fails, the app offers CPU once, and the log tail is visible |
| CPU only, 32GB RAM | 0, 35B MoE | CPU | Loads on CPU, threads equal physical cores, warning absent |
| CPU only, 16GB RAM | 0 | CPU | The under-20GB warning is visible before download. The user can still continue |

On every machine, also run: fresh per-user install without an admin prompt, wizard resume after a killed download, port 7474 already taken by a dummy listener (app moves to 8081), Docker Desktop running (search hits the gateway; stop Docker and the next search still returns DuckDuckGo), Docker skipped at install, copy and paste both ways, `format E:` denied, uninstall keep-data default, uninstall delete-models checkbox.

Record driver version, Windows build, and the diagnostics bundle for any failure. Those notes feed M3.5.

## 9. Risks, open questions, and what only Spencer can provide

### Risks

1. Windows uses more VRAM than a quiet Linux box because of the compositor and other apps. The 16GB and 12GB tiers exist for that reason. The wizard shows the lower-accuracy label and makes a smaller model easy to pick. Vision context is already reduced.
2. CUDA fails opaquely when the driver is older than the CUDA 12.4 build in the `b9070` zip. Bundle the DLLs from that zip, show the driver version, and fall back to Vulkan. Pinning `b9070` matches the tag `install.sh` documents for the Vulkan container image. Move the pin only by editing `models.json` and the SBOM together.
3. Vulkan behavior differs across AMD and Intel. CPU remains a one-click fallback. The diagnostics bundle includes the GPU name and driver so failures are comparable.
4. Model downloads are 5GB to 18GB plus about 900MB for mmproj. Resume, checksum, pause, and another drive are the mitigation. Metered networks should get a confirm before step 6. `NetworkInformation` or a simple "I am on a metered connection" checkbox is enough.
5. SmartScreen will warn on an unsigned `OpenMonoSetup.exe`. Signing stays off until a certificate exists. Early testers need the "More info, Run anyway" path. Do not block the build on signing.
6. Hooks and shell habits written for bash will fail in PowerShell. The overlay tells the model to prefer PowerShell. User hooks run through PowerShell, and the settings page says so. Sub-agents do not run hooks (section 4).
7. `PromptSnapshot.txt` can drift from `SystemPrompt.Base`. Re-sync it when OMA prompt text changes, or take upstream item 2 if Spencer wants the drift gone.
8. WinUI 3 unpackaged self-contained publish is the M1 UI risk (bootstrapper DLL layout, Windows App SDK version versus the runner). If publish fails on `windows-latest` and a short fix does not land, add the WPF fallback project and keep the libraries. Do not start that project before a real failure.
9. Scrapling's first Docker build is slow and needs a Dockerfile context we must copy verbatim. If the upstream Dockerfile changes, `stage-docker.ps1` picks it up only when the Windows branch is rebuilt. A stale copy is a packaging bug, not a reason to edit `docker/`.
10. `GatewayCapabilities` caches probes for the process lifetime. After Docker starts late, the cache may still say search is off. Clear that cache or set `Web.Search` explicitly to `true` when our health check passes, so the cache is not required. Setting the explicit flag is a one-line config write and avoids relying on the private cache.
11. Long paths still need the Windows long-path policy for some tools even with `longPathAware` in the manifest. The wizard can mention it when a workspace path is very deep. Do not flip the machine-wide registry key from the per-user app.

### Open questions

These do not block M1. Defaults are the approved decisions.

1. Telemetry stays off. Confirm that crash dumps are also opt-in, explicitly including Windows Error Reporting (WER) local dumps and any upload consent path — the OS-level exfil path a reviewer will ask about. The plan assumes no automatic upload.
2. ACP loopback ships as a settings toggle, default off. An editor extension that speaks ACP is not part of M1 to M3.
3. Remote endpoint in Settings is the dual-box client. Tunnel, frpc, and hosting for other machines are out of the product.
4. Docker Desktop's paid subscription for larger companies is a legal choice, not a code choice. The app works with Docker skipped.
5. Whether to re-sync model URLs automatically when `install.sh` changes, or only by a deliberate `models.json` edit. The plan says deliberate edit.

### What only Spencer can provide

- A code signing certificate (PFX) and the password, as `WINDOWS_CERT_PATH` and `WINDOWS_CERT_PASSWORD`, if and when he wants SmartScreen reputation. Until then the pipeline skips signing. He also chooses OV versus EV and who holds the key. EV builds reputation faster. Neither is required to write the code.
- The hardware matrix in section 8. NVIDIA 24GB, NVIDIA 16GB, AMD or Intel Vulkan, and a CPU-only machine. CI cannot substitute for those runs.
- The Docker Desktop commercial license decision for any company-owned machines that will install Docker with this app.
- A GitHub release token or Velopack feed location when M2.6 turns updates on. The app can ship M1 with the existing "open the release page" updater.
- Approval, on a separate change outside this plan, if he wants any item in the upstream list in section 4. The Windows branch build does not wait on that approval.

## 10. Suggested cloud-agent runs

Each run stays on `Windows`, pushes to `Windows`, and leaves the tree compiling. None of them edit files outside `windows/` except run 1, which may add a Linux job to `.github/workflows/windows-desktop.yml`. That workflow file is the one exception the scaffold already established. Do not open a pull request against `main`. Do not merge to `main`.

1. **Settings writer and Linux test job.** Snake_case `OmaSettingsWriter`, `ConfigLoader` round-trip test, workflow job that tests the `net10.0` projects on `ubuntu-latest`. Done when both CI jobs are green or the new tests pass locally and the workflow file is valid.
2. **Explicit tool graph and prompt builder.** Replace reflection, embed `PromptSnapshot.txt`, append the overlay, unit test the system message and the Bash tool type. No UI yet.
3. **Slash commands, Windows `/init`, session save.** `ChatRenderer`, `SlashDispatcher`, `WindowsInitCommand`, `SessionManager` after a turn. Test Makefile parsing with a temp directory.
4. **Supervisor lifecycle.** Log writer lifetime, port-before-command, healthy-port reuse, backoff fields, tests with a fake probe. Fix `ServerPage` call order.
5. **Flavor download and checksum pins.** Wire `fetch-llama.ps1` into a `FlavorInstaller` the wizard can call, add pause, fail Release builds on empty hashes, run `pin-checksums.ps1` once and commit the hashes. This run needs network and a large download. Keep the GGUF files out of git.
6. **Wizard flow to a healthy stub.** `FirstRunState`, disk preflight, step UI, start supervisor against a stub server in a dev hook, navigate to Chat. Real GGUF download stays behind the existing downloader and is not required for the automated check.
7. **Ripgrep, single instance, HTTP failure copy.** Stage `rg.exe`, PATH prepend, mutex, chat error text that does not mention docker compose for llama recovery.
8. **Docker asset staging and gateway flag.** `stage-docker.ps1`, install-directory compose paths, skip path, set `Web.Gateway` only after port 47480 health. Use a stub listener on 47480 in tests. Do not require Docker Desktop on the runner.
9. **Installer contents.** CPU llama-server and `rg.exe` and docker resources inside the publish tree, Inno silent-install switches, VC++ redist download in `build.ps1`, uninstall keep-data verified by reading the iss script in review. Unify the three installer paths on `windows/dist/OpenMonoSetup.exe` (iss `OutputDir`, workflow upload path, `build.ps1` sign path) as specified in section 7. CI uploads the unified artifact.
10. **Hook executor.** `WindowsToolExecutor`, cleared `HookConfig`, PowerShell pre and post hooks, tests for exit code 2. Read `AgentTool` child-loop wiring to confirm the executor cannot leak into sub-agents. Sub-agent hook limitation documented in a code comment and covered by a test that the config passed to the loop has empty hooks.
11. **Chat UI.** Streaming bubble, thinking collapse, tool cards, four permission buttons, Stop, tok/s line. Verify on a Windows session with the stub server if the runner cannot show UI. State in the run summary what was not visually verified.
12. **Clipboard, LSP client, MCP rewrite.** WinUI clipboard delegates, `WindowsLspClient` using `LspPathMapper`, factory passes resolved MCP configs, Node path probe. TypeScript server download is part of this run if `fetch-node.ps1` is small enough. Otherwise split Node into the next run.
13. **Model switch, tray, diagnostics.** Restart server on switch, custom GGUF list, tray status, diagnostics bundle includes Docker and hardware.
14. **Velopack.** Package reference, local feed smoke test, no mid-turn restart. Needs Spencer's feed URL only to point at production. A filesystem feed is enough to finish the run.
15. **M3 hardening.** Warmup probe, crash backoff, gateway cache bypass via explicit `Web.Search`, WiX project that produces an MSI, enterprise note, offline GGUF import.

Runs 2 and 4 can be swapped. Runs 11 and 12 can be swapped. Do not start run 9 before run 5 has a CPU binary staging path, or the installer will claim to contain `llama-server.exe` and will not. Do not start run 14 before run 9, because Velopack packs the same publish output the installer uses.

After run 15, Spencer runs the hardware matrix. A follow-up run applies those fixes and does not expand scope.
