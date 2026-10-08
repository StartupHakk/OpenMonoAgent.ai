# Windows Port Plan: Native Agent (Milestone 1) and Inference Server (Milestone 2)

Status: planning only. No application code was changed for this plan.
Scope per maintainer direction: Milestone 1 (primary, most detail) ports the AGENT to run
natively on Windows 10/11 with inference remote on Mac or Linux. Milestone 2 (less detail,
still concrete) covers what it would take to also run the inference and model server on
Windows. WSL is documented as a fallback only, not the target.

Note on style: this doc avoids em dashes entirely, using periods, commas, hyphens, or
parentheses instead.

## 1. How the agent is built, installed, and run today

This section is scoped to what matters for a Windows port.

### 1.1 Language, runtime, and packaging

- Agent: C# on .NET 10. `Directory.Build.props` pins `net10.0` with nullable and implicit
  usings enabled. `global.json` pins SDK `10.0.100` with `latestMinor` roll forward.
- Project: `src/OpenMono.Cli/OpenMono.Cli.csproj` builds an `Exe` named `openmono`.
  It references `Microsoft.AspNetCore.App` (used for the ACP HTTP/SSE server) and NuGet
  packages that are themselves cross platform: Spectre.Console, Markdig, YamlDotNet,
  Microsoft.CodeAnalysis.CSharp.Workspaces (Roslyn), Terminal.Gui, TiktokenSharp, and
  SixLabors.ImageSharp.
- Tests: `src/OpenMono.Tests/OpenMono.Tests.csproj` (xUnit style tests observed under
  `src/OpenMono.Tests/`, covering LLM clients, playbooks, MCP, ACP, integration smoke).
- There is no published single file Windows binary today, no `win-x64` publish profile,
  no installer (MSI, MSIX, winget, Chocolatey, or zip), and no `windows-latest` CI job.
  (No `.github/workflows` directory was found in this checkout.)
- `install.ps1` exists but is a developer loop, not an installer. It checks for the .NET
  SDK, runs `dotnet build`, and optionally runs `dotnet run ... -- --workdir ... --acp-only`.
  It does not install dependencies, put anything on PATH, or configure an endpoint.

### 1.2 How the user installs and runs things today (Linux and macOS)

- One line bootstrap: `get-openmono.sh` clones to `~/openmono.ai` and execs the `openmono`
  bash launcher with `setup`.
- The `openmono` bash script (repo root, about 1440 lines) is the real CLI for lifecycle:
  `setup`, `start`, `stop`, `restart`, `logs`, `status`, `agent`, `graph`, `graphify`,
  `upgrade`, `config`, `tunnel`, `version`, `help`. It assumes bash, core Unix tools
  (`grep`, `sed`, `awk`, `curl`, `jq`, `openssl`, `ss` or `lsof`, `python3`), `systemctl`
  on Linux or Homebrew services on macOS, and Docker.
- `scripts/install_prereqs.sh` (Ubuntu) installs Docker, git, cmake, curl, jq, .NET 10,
  ripgrep, and the NVIDIA stack. `scripts/install.sh` downloads models, writes
  `docker/docker-compose.override.yml` (GPU, AMD iGPU Vulkan, or CPU), builds images, and
  starts `llama-server`.
- The agent itself normally runs inside Docker (`docker/Dockerfile.agent`, service `agent`
  in `docker/docker-compose.yml`), with `${WORKSPACE}` mounted at `/workspace` and
  `~/.openmono` mounted at `/home/agent/.openmono`. The container entrypoint is
  `docker/entrypoint.sh`.
- Inference: `llama-server` from `ghcr.io/ggml-org/llama.cpp` images (base `server`,
  plus `server-cuda` and `server-vulkan` overrides), fronted optionally by Caddy plus
  SearXNG and Scrapling services. On Apple Silicon, inference runs natively with Metal
  and the agent container reaches it via `host.docker.internal`.
- The agent talks to inference over HTTP as an OpenAI compatible endpoint, default
  `http://localhost:7474` (see `src/OpenMono.Cli/Config/AppConfig.cs`, class `LlmConfig`).
  The VS Code and Cursor extension talks to the agent over ACP on port 7475
  (`--acp-only --acp-port 7475`).

### 1.3 Agent architecture in one paragraph

`Program.cs` parses flags, probes the LLM server (`/props`, then `/v1/models`), wires
dependency injection, picks a renderer (full screen Spectre.Console TUI when interactive,
scrolling classic renderer when redirected or with `--classic`), and runs
`ConversationLoop`. Each turn streams from `ILlmClient`, dispatches tool calls through a
12 step pipeline (parse, schema validate, sanity check, plan mode guard, capability and
permission check, cache, pre hook, execute, post hook, artifact store, cache write,
invalidation), and persists JSONL sessions under the data directory. Tools include file
tools, `Bash`, `Grep` (ripgrep), `Glob` (managed matcher), Roslyn, LSP, MCP (subprocess
over stdio), playbooks, hooks, sub agents, web search and fetch, and image input.

---

## 2. Milestone 1: agent running natively on Windows, inference remote

### 2.1 Goal and non goals

Goal: a Windows 10/11 user installs the agent natively (no WSL required, no Docker
required for the agent process), points it at a remote inference endpoint on Mac or
Linux (`openmono config set llm.endpoint ...`), and runs `openmono agent` in PowerShell
and in cmd with the TUI, file tools, Bash tool, permissions, sessions, MCP, LSP,
playbooks, hooks, and the VS Code extension over ACP all working.

Non goals for Milestone 1: running `llama-server`, the Caddy gateway, SearXNG, or
Scrapling on Windows (see Milestone 2). Requiring WSL or Docker for the agent itself.
Changing model selection, context budgets, or inference behavior.

### 2.2 What already works on Windows (good news)

The codebase is closer than a typical Unix first CLI. The following are already
Windows aware and were verified by reading the code:

- Data and config directories use `Environment.SpecialFolder.UserProfile` plus
  `.openmono`, which resolves to `%USERPROFILE%\.openmono` on Windows. Files:
  `src/OpenMono.Cli/Config/AppConfig.cs` (line 37), `src/OpenMono.Cli/Config/ConfigLoader.cs`,
  `src/OpenMono.Cli/Session/SessionManager.cs` (line 14), `src/OpenMono.Cli/History/FileHistory.cs` (line 17).
- `PathGuard` compares paths case insensitively on Windows, blocks UNC paths, and blocks
  Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `COM0` to `COM9`, `LPT1` to `LPT9`).
  File: `src/OpenMono.Cli/Permissions/PathGuard.cs` (lines 5 to 8, 154 to 168).
- `ProcessRunner` (used by `GitHelper` and the playbook template engine) already selects
  `cmd.exe /c` on Windows and `/bin/bash -c` elsewhere, and fails open with exit 127.
  File: `src/OpenMono.Cli/Utils/ProcessRunner.cs` (lines 14 to 50).
- Desktop notifications already have a Windows branch that shells to `powershell` with a
  toast notification script. File: `src/OpenMono.Cli/Utils/DesktopNotifier.cs` (lines 93 to 114).
- Read only file error messages already suggest `attrib -r` on Windows and `chmod u+w`
  elsewhere. Files: `src/OpenMono.Cli/Tools/FileWriteTool.cs` (lines 94 to 98),
  `src/OpenMono.Cli/Tools/FileEditTool.cs` (lines 177 to 181).
- The `docker.exe` binary name is already handled in one recovery path. File:
  `src/OpenMono.Cli/Program.cs` (line 790).
- `GlobTool` uses `Microsoft.Extensions.FileSystemGlobbing` (managed code, no subprocess),
  so it should work unchanged on Windows. File: `src/OpenMono.Cli/Tools/GlobTool.cs`.
- Session files are JSONL plus JSON with `Path.GetInvalidFileNameChars` validation on load.
  File: `src/OpenMono.Cli/Session/SessionManager.cs` (line 63). No symlinks, no file modes,
  no Unix sockets observed in the agent code.

### 2.3 Prioritized blockers for Milestone 1

Ordered by severity. Each item names the file and line, what breaks on Windows, and the
proposed approach with alternatives where there is a real tradeoff. Line numbers refer to
this checkout.

#### B1. `BashTool` hardcodes `/bin/bash` (foreground and background)

- Files: `src/OpenMono.Cli/Tools/BashTool.cs` (line 94, `FileName = "/bin/bash"`) and
  (line 207, background worker `FileName = "/bin/bash"`).
- What breaks: on Windows without git bash, `Process.Start` throws and every `Bash` call
  fails. Even with git bash present, `HOME` defaults to `/root` (lines 104, 216) and
  `PATH` defaults to `/usr/local/bin:/usr/bin:/bin` (lines 105, 217), both wrong on
  Windows. The background wrapper builds a bash specific string,
  `exec >>'<log>' 2>&1; <command>` (line 203), which cmd and PowerShell do not accept.
  The success message then tells the model to run `tail -n`, `tail -f`, `kill`, and
  `kill -9` (lines 238 to 241), which do not exist in cmd.
- Proposed approach: introduce a shell abstraction with two implementations.
  On Windows, default to PowerShell (`powershell.exe -NoProfile -NonInteractive -Command`)
  when available, falling back to `cmd.exe /c`. Keep bash on Unix. Route both the
  foreground and background paths through it, and generate the background redirect and
  the follow up hints per shell (PowerShell: `Start-Process` or output redirect to the
  log file; hint at `Get-Content -Tail`, `Stop-Process`). Also fix the `HOME` and `PATH`
  fallbacks to be OS aware (on Windows, leave `PATH` alone and do not invent `HOME`;
  shells and git resolve the profile from `%USERPROFILE%`).
- Alternative: require git bash on Windows and keep one shell everywhere. This is less
  work but contradicts the goal of working in plain cmd, and it still leaves the `HOME`
  and `PATH` defaults and the Unix only hints wrong. Recommended only as a short term
  fallback, not the end state.
- Also update the tool description strings that promise bash and `/tmp/openmono/bg/`
  (lines 10 to 25), and the system prompt text that assumes bash (see B7).

#### B2. `HookRunner` hardcodes `/bin/bash`

- File: `src/OpenMono.Cli/Hooks/HookRunner.cs` (lines 99 to 108).
- What breaks: every configured hook (`SessionStart`, `PreToolUse`, `PostToolUse`) fails
  to start on Windows. This silently degrades policy enforcement and audit logging.
- Proposed approach: reuse the same shell abstraction from B1. Document the hook shell
  per platform in `docs/CONFIG.md` and add one Windows specific test (a hook that runs
  `echo` or `Write-Output` and an exit code 2 block test).
- Alternative: none worth considering. This is a small change once B1 exists.

#### B3. No Windows launcher or installer for the agent lifecycle

- Files: `openmono` (bash, entire file), `get-openmono.sh`, `scripts/install.sh`,
  `scripts/install_prereqs.sh`, `scripts/lib/shell-integration.sh`, `install.ps1`.
- What breaks: everything a Windows user types first. `openmono setup|start|stop|status`
  is bash only, `get-openmono.sh` needs bash and curl, `install.ps1` only builds the ACP
  dev loop and does not install anything. There is no PATH setup for PowerShell or cmd,
  no dependency bootstrap (.NET runtime, Docker Desktop only if the user wants sandboxing,
  ripgrep, git, Node based language servers), and no signed or versioned artifact.
- Proposed approach (recommended): ship the agent as a self contained single file
  `openmono.exe` published for `win-x64` (plus `win-arm64` if cheap), and move the small
  lifecycle surface (`config`, `agent`, `status`, `--help`, `--version`) into the .NET
  CLI itself so PowerShell and cmd both work with zero shell scripts. Provide a simple
  zip plus a `winget` package, and a short PowerShell bootstrap that installs the .NET 10
  runtime only if the build is framework dependent. Keep `install.ps1` as the developer
  script and add `install-windows.ps1` (or extend it) as the user installer.
- Alternative: write a parallel `openmono.ps1` that mirrors the bash launcher. This is
  faster at first but doubles every future lifecycle change and still leaves dependency
  setup split across two languages. If chosen, limit the PowerShell launcher to agent
  role flows only (config plus agent plus status) and explicitly not inference.
- Note: `setup`, `start`, `stop`, `restart`, `logs`, `tunnel`, `graph`, and `graphify`
  either assume Docker or systemd or Homebrew. For Milestone 1, these should detect the
  agent only role on Windows and either become no ops with a clear message or be hidden
  from `--help` on Windows. Do not port the inference lifecycle to PowerShell in
  Milestone 1.

#### B4. Agent to inference coupling assumes same machine or container network

- Files: `src/OpenMono.Cli/Config/AppConfig.cs` (`LlmConfig.Endpoint` default
  `http://localhost:7474`, line 64); `src/OpenMono.Cli/Program.cs`
  (`TryRecoverLlamaServerAsync`, lines 782 to 843; health hint strings at lines 661 to 677
  and 895); `openmono` bash function `cmd_agent` (lines 793 to 911, maps any localhost
  endpoint to `http://llama-server:7474` and rewrites the gateway to `http://caddy:8080`,
  lines 816 to 823); `src/OpenMono.Cli/Acp/AcpLockFileWriter.cs` (default workspace mount
  `/workspace`, line 24; required `HOST_WORKSPACE_PATH`, lines 37 to 40).
- What breaks: on a native Windows agent with a remote endpoint, the auto recovery path
  offers to run `docker compose --profile full up -d llama-server` (line 820) against a
  compose file resolved from the working directory (line 787), which will not exist on
  Windows. Hint strings tell the user to run `docker logs llama-server` and
  `curl http://localhost:7474/health`, which point at the wrong machine. The
  `/workspace` default and the mandatory `HOST_WORKSPACE_PATH` make sense only inside
  the agent container for the VS Code extension Docker flow.
- Proposed approach: make remote endpoint a first class mode. When the endpoint is not
  loopback, skip the local docker recovery entirely and print remote oriented hints
  (check the endpoint URL, the API key, and reachability from this machine). When the
  endpoint is loopback on Windows in Milestone 1, print a short message that local
  inference is not supported on Windows yet and show the two config commands to point at
  a Mac or Linux box. For the ACP lock file, default the workspace mount to the real
  working directory when not running in Docker, and only require `HOST_WORKSPACE_PATH`
  when actually containerized.
- Alternative: keep the recovery path but gate it behind an explicit flag. Weaker,
  because the default path still misleads Windows users. Prefer the endpoint based
  branching above.

#### B5. `GrepTool` shells out to a `rg` binary

- File: `src/OpenMono.Cli/Tools/GrepTool.cs` (line 60, `FileName = "rg"`).
- What breaks: clean Windows machines have no `rg` on PATH, so `Grep` returns
  "Grep error (is ripgrep installed?)". The agent's main code search path then fails.
- Proposed approach: bundle `rg.exe` with the Windows distribution (it has official
  Windows builds) and add a managed fallback for when it is missing (a simple
  recursive regex search over the workspace, reusing `PathGuard`). The fallback does not
  need full ripgrep parity, only correct behavior with a clear "slow path" note.
- Alternative: pure managed search only, no binary. Simpler packaging but a large
  performance regression on big repos. The hybrid (binary plus fallback) is recommended.

#### B6. Clipboard integration is macOS and Linux only

- Files: `src/OpenMono.Cli/Rendering/AnsiInputReader.cs` (`CopyToClipboard`, lines 1011
  to 1035; `ReadClipboard`, lines 1050 to 1064).
- What breaks: copy works on Windows only via the OSC52 escape and the `.clipboard-out`
  bridge file. Host clipboard copy through `pbcopy`, `xclip`, and paste through
  `pbpaste`, `xclip` never resolve on Windows. `ReadClipboard` unconditionally falls
  back to `xclip`, which will always fail on Windows (caught, returns null, but noisy
  and misleading).
- Proposed approach: add a Windows branch using `powershell -NoProfile -NonInteractive`
  with `Set-Clipboard` and `Get-Clipboard`, guarded with short timeouts exactly like the
  existing branches. Keep OSC52 and the bridge file as they are. Gate the Unix branches
  behind OS checks so Windows never attempts `xclip`.
- Effort is small. Test with clipboard read and write on `windows-latest`.

#### B7. Prompts, hints, and guardrails assume Unix

- Files: `src/OpenMono.Cli/Utils/SystemPrompt.cs` (lines 211 to 216, platform line);
  `src/OpenMono.Cli/Agents/AgentDefinition.cs` (line 121, "You MAY write ephemeral test
  scripts to /tmp only."); `src/OpenMono.Cli/Tools/BashTool.cs` (lines 10 to 25, tool
  description); `src/OpenMono.Cli/Permissions/PermissionEngine.cs` (lines 92, 164, 407,
  prompt text about chmod and attrib); `src/OpenMono.Cli/Tools/SanityCheck.cs`
  (protected paths list, lines 20 to 30; `HOME` credential check, line 184;
  `IsRegularFileTarget`, lines 141 to 149).
- What breaks: the model is told the wrong shell, wrong temp dir, and wrong commands,
  and the guardrails under protect Windows paths. Specifically: `SanityCheck` protects
  `/etc/`, `/usr/bin/`, `/proc/`, and similar but not `C:\Windows\`, `C:\Program Files\`,
  or the registry; the credential directory check reads `HOME`, which is often unset on
  Windows (should also check `%USERPROFILE%`); `IsRegularFileTarget` excludes `/dev/*`
  but not `NUL`; and the echo and printf redirection rule does not cover PowerShell
  redirection idioms (lower priority, keep as follow up).
- Proposed approach: make the prompt OS aware. Emit the shell name, temp dir
  (`Path.GetTempPath()`), and follow up commands per OS. Extend the protected path list
  with Windows system locations using case insensitive comparison, add the `USERPROFILE`
  fallback to the credential check, and treat `NUL` as a device target. Keep the
  permission modifying command block list as is (it already includes `icacls`,
  `takeown`, and `attrib`).
- Alternative: one static prompt for all platforms. Not recommended, because the model
  will keep emitting `tail`, `kill -9`, and `/tmp` paths on Windows.

#### B8. `InitCommand` build detection pipes through Unix text tools

- File: `src/OpenMono.Cli/Commands/InitCommand.cs` (lines 70 to 80).
- What breaks: `head -20 Makefile | grep -E ... | head -5 | sed ...` runs through
  `ProcessRunner`, which on Windows uses `cmd.exe /c`. `head`, `grep`, and `sed` do not
  exist in cmd, so Makefile target detection silently returns nothing. Non fatal, but it
  is user visible output quality.
- Proposed approach: replace the pipeline with managed code (read the Makefile, match
  the target regex in C#). Delete the shell out. Same output, all platforms.

#### B9. LSP servers: command resolution, install story, and a `file://` URI bug

- Files: `src/OpenMono.Cli/Lsp/LspServerManager.cs` (default server table, lines 17 to
  25); `src/OpenMono.Cli/Lsp/LspClient.cs` (`StartAsync`, lines 27 to 61; `rootUri`
  built as `$"file://{workspaceRoot}"`, line 44; `file://{filePath}` at line 71).
- What breaks: bare commands (`omnisharp`, `typescript-language-server`, `pylsp`,
  `gopls`, `rust-analyzer`) resolve on Unix via PATH shims but on Windows need `.cmd`,
  `.exe`, or Node script resolution, and most will simply not be installed. The `rootUri`
  and document URIs are built by string concatenation, so `C:\proj` becomes
  `file://C:\proj`, which is malformed (backslashes, unescaped spaces). The client also
  never sets a working directory or environment for the server process.
- Proposed approach: build URIs with `new Uri(path).AbsoluteUri`, set
  `WorkingDirectory = workspaceRoot` on the server process, and resolve Windows command
  names with `where.exe` semantics plus `.cmd` probing (or document that the user must
  put the servers on PATH and fail with an actionable message naming the missing
  server). Publish a per language install table for Windows in the docs (winget, npm,
  pip, or the OmniSharp zip). Confirm Roslyn behavior separately, since the C# path
  also has the in process `RoslynTool` which needs no server at all.
- Alternative: disable LSP on Windows in Milestone 1. Possible, but it removes hover,
  definition, references, completion, and diagnostics, which the plan should avoid losing
  if the fix is this small.

#### B10. MCP servers: `.cmd` and `.ps1` launch and Unix only example scripts

- Files: `src/OpenMono.Cli/Mcp/McpClient.cs` (process start, around line 35);
  `src/OpenMono.Cli/Mcp/McpServerManager.cs`; `src/OpenMono.Cli/Config/AppConfig.cs`
  (`McpServerSettings`, lines 48 to 55).
- What breaks: with `UseShellExecute = false`, launching `my-server.cmd`, a `.bat`, or a
  `.ps1` directly fails on Windows. Node based MCP servers installed via npm expose
  `.cmd` shims, which is exactly the common case. Separately, the repo's own playbook
  example scripts are bash (17 `.sh` files under `docs/playbooks-examples/`), and the
  auto detected `code-review-graph` and `graphify` helpers are pip packages whose
  console scripts may resolve differently on Windows.
- Proposed approach: add Windows launch routing in `McpClient` (if the command ends in
  `.cmd`, `.bat`, or `.ps1`, spawn via `cmd.exe /c` or `powershell -File` respectively;
  otherwise try direct start and fall back to PATH resolution with a clear error).
  Document the Windows MCP story (Node on PATH, `code-review-graph` and `graphify`
  install via pip with `%USERPROFILE%` scripts dir on PATH). Convert or duplicate the
  most used example scripts later; for Milestone 1 it is enough that user configured MCP
  servers work and that shipped examples are labeled by platform.
- Alternative: require `.exe` MCP servers on Windows. Too restrictive, npm shims are
  the norm. Not recommended.

#### B11. TUI, console, and filesystem edge cases to verify (not assumed broken)

These were reviewed but need a real Windows run to close out:

- Spectre.Console and Terminal.Gui are cross platform in principle, but the custom
  `AnsiInputReader`, `AnsiPainter`, and `AnsiTuiRenderer` do heavy escape sequence and
  clipboard work. Verify on Windows Terminal, conhost (`cmd`), and PowerShell, including
  Ctrl+C handling (`Console.CancelKeyPress` in `Program.cs` lines 349 and 450, and the
  reader at `AnsiInputReader.cs` line 1000), Unicode and emoji rendering, and
  `--classic` fallback when redirected.
- ACP server binding: `AcpServerSettings.BindAllInterfaces` defaults to false and is set
  true only when `/.dockerenv` exists (`Program.cs` line 269). On native Windows the ACP
  server binds loopback, which is correct for the local extension, but confirm the port
  7475 path through Windows Defender Firewall prompts and that the VS Code extension
  connects without the Docker lock file contract (see B4).
- Line endings: the agent reads and writes files with .NET defaults. Confirm that
  `FileRead`, `FileWrite`, `FileEdit`, `ApplyPatch` (unified diff with `\n`), session
  JSONL, and playbook YAML round trip CRLF repos without noise, and decide on a
  normalization policy (recommend: preserve file style, do not force LF).
- Long paths and case: confirm `MAX_PATH` behavior (Windows 10/11 long path opt in),
  case insensitive workspace containment (already handled by `PathGuard`), and temp
  paths containing spaces.
- `ImageSharp` (vision compression) and `TiktokenSharp` are managed and expected fine.
  Verify with one image attach test against the remote endpoint.

### 2.4 What Milestone 1 deliberately does NOT port

- `llama-server`, Caddy, SearXNG, Scrapling, `docker-compose` lifecycle, `frpc` tunnel
  service setup, NVIDIA driver and CUDA setup, and the model download flow. These stay
  Mac and Linux only. The Windows agent treats inference as a URL plus API key.
- The `openmono` bash launcher as a whole. Only the agent role surface is replaced on
  Windows (see B3). The bash launcher remains the path on Mac and Linux.

### 2.5 Suggested phased rollout for Milestone 1

- Phase 1A: minimum viable native Windows run. Shell abstraction (B1, B2), `HOME` and
  `PATH` fallbacks, OS aware prompt and hints (B7 core), remote endpoint mode with the
  docker recovery path disabled for remote endpoints (B4), managed Makefile parsing
  (B8), `rg.exe` bundling or fallback (B5), `win-x64` self contained publish plus a zip,
  and a `windows-latest` CI job that builds and runs unit tests. Exit criteria: a Windows
  user can configure a remote endpoint and complete a file read, edit, grep, and Bash
  turn in both PowerShell and cmd.
- Phase 1B: installer and service quality. Winget package (or documented zip install
  with PATH setup), dependency bootstrap (git, rg, Node for MCP and LSP), clipboard
  (B6), LSP URI and resolution fixes (B9), MCP launch routing (B10), full guardrail
  updates (B7 remainder), CRLF and long path verification, VS Code extension over ACP
  against the native agent, docs (`SETUP.md` Windows section, `CONFIG.md` hook shell
  note, per language LSP table).
- Phase 1C: parity hardening. Playbook example scripts labeled or ported for Windows,
  `graph` and `graphify` on Windows or explicit unsupported messages, permission and
  hook test suite green on `windows-latest`, signed binary, and release automation.

### 2.6 Effort estimates for Milestone 1

Estimates are sized by invasiveness and dependency risk, not calendar time. Assumption:
one engineer familiar with this repo and with access to a Windows 10/11 test machine.

- Phase 1A: small to medium. Touches `BashTool`, `HookRunner`, one new shell module,
  prompt strings, `Program.cs` recovery branching, publish wiring, and CI. The main
  risk is PowerShell quoting and escaping in the agent to shell path, which needs real
  Windows testing, not more design.
- Phase 1B: medium. Packaging (winget, signing), dependency bootstrap, LSP and MCP
  install matrices, and guardrail updates each have long tails of small issues.
- Phase 1C: small to medium. Mostly verification, docs, and release work plus
  playbook script ports.

### 2.7 What to test on Windows and how

All tests run on `windows-latest` (plus a local Windows 10 or 11 spot check in both
PowerShell and cmd, since CI only proves one shell well).

- Build and unit tests: `dotnet build` and the `OpenMono.Tests` suite on
  `windows-latest`. Gate the draft on this first.
- New or extended tests (all runnable on Windows without a model server):
  shell abstraction (foreground echo, failing exit code, timeout kill, background log
  file creation), hook allow and block (exit code 2) through the Windows shell, `Grep`
  with and without `rg` on PATH, `Glob` with Windows separators, `PathGuard` UNC and
  device name cases, session save and load round trip with CRLF content, `ApplyPatch`
  on a CRLF file, LSP URI formation for `C:\` paths, MCP launch of a `.cmd` shim,
  config load with `%USERPROFILE%` data dir, and clipboard read and write (may need to
  be marked interactive only on CI).
- End to end smoke (needs a remote or mock endpoint, no model weights on Windows):
  start the agent with `llm.endpoint` pointed at a stub OpenAI compatible server or the
  real remote box, run one turn each of file read, file edit, grep, and Bash, confirm
  the TUI renders and `--classic` works under redirection, and connect the VS Code
  extension over ACP on port 7475.
- Suggested CI job (new file, e.g. `.github/workflows/windows-agent.yml`): checkout,
  setup .NET 10, `dotnet build`, `dotnet test`, publish `win-x64` self contained,
  run the smoke script (help, config set and get against a temp data dir, ACP boot and
  health check), and upload the `openmono.exe` artifact. Keep the model server and all
  Docker steps out of this job.

---

## 3. Milestone 2: inference and model server on Windows

Less detailed than Milestone 1 by design, but concrete. Question to answer: what would
it take to run the model server itself on Windows 10/11 with usable GPU acceleration.

### 3.1 Current state

- Inference is `llama-server` from `ghcr.io/ggml-org/llama.cpp` Linux images, configured
  by `scripts/install.sh` (model pick by VRAM tier, `docker-compose.override.yml`
  generation, `docker/.env` with `MODEL_NAME`, `MODEL_MMPROJ`, `CTX_SIZE`,
  `LLAMA_API_KEY`, `LLAMA_PORT`). GPU path is CUDA via `nvidia-container-toolkit`;
  AMD path is Vulkan on the 7940HS; CPU path is OpenBLAS. macOS uses native Metal.
- Nothing in this path runs on Windows today. `scripts/install_prereqs.sh` and
  `scripts/install.sh` are Ubuntu only (apt, `/sys` PCI probing, `powerprofilesctl`,
  `systemctl`, `sg`), the `openmono` launcher maps `Linux` and `Darwin` only and errors
  on anything else (see the `Unsupported OS` branch), and `Dockerfile.llama` is kept
  only as reference since the project uses the upstream prebuilt image.

### 3.2 Blockers and approach options

#### I1. Runtime: no Windows llama server distribution

- Blocker: the compose stack pins Linux images (`ghcr.io/ggml-org/llama.cpp:server`,
  `server-cuda`, `server-vulkan-b9070`). There is no Windows container variant and no
  native `llama-server.exe` distribution in this repo.
- Options: (a) native Windows build of llama.cpp via CMake plus MSVC, shipped as a zip
  alongside pinned model files, recommended for GPU users; (b) Docker Desktop on
  Windows running the existing Linux images through the WSL2 backend, recommended as
  the low effort CPU path and as the interim GPU path where the NVIDIA WSL driver
  works; (c) WSL2 Ubuntu with the current Linux installer unchanged, documented as the
  fallback only. The plan recommends (a) for the end state with (b) as the bridge.

#### I2. GPU backends on Windows

- Blocker: the Linux CUDA path (`nvidia-container-toolkit`, `deploy.resources.gpu`,
  `NVIDIA_VISIBLE_DEVICES`) does not translate to Windows. llama.cpp upstream supports
  CUDA on Windows natively (needs the NVIDIA driver plus a CUDA toolkit compatible host
  compiler), Vulkan on Windows (needs the Vulkan SDK or driver bundled runtime), and CPU
  with OpenBLAS. DirectML is not the path to bet on for llama.cpp.
- Approach: native build matrix of `llama-server.exe` flavors: CUDA (NVIDIA 12GB, 16GB,
  24GB tiers mirroring the current VRAM logic), Vulkan (AMD iGPU and discrete, Intel),
  CPU fallback. Reuse the existing tier logic (model file, quant, `CTX_SIZE`, KV cache
  types, mmproj context trim) but source VRAM from `nvidia-smi.exe` on Windows and RAM
  from WMI or .NET `GC` and performance counters, not `/sys` or `lspci`. Validate the
  24GB full accuracy tier first, then 16GB and 12GB lower accuracy tiers, then CPU.

#### I3. Model download and storage paths

- Blocker: `scripts/install.sh` downloads 5GB to 18GB GGUF files plus a 900MB mmproj
  with `curl`, `stat -c%s`, and `du`, into `$INSTALL_DIR/models` and `docker/.env`.
  Windows needs the same resume, checksum, and free space checks without those tools,
  plus paths that may contain spaces (`C:\Users\<name>\openmono.ai\models`) and
  Defender scanning delays on multi GB files.
- Approach: implement download in the .NET installer or a PowerShell installer using
  `HttpClient` with resume and SHA or size verification, default location
  `%USERPROFILE%\openmono.ai\models`, and a preflight free space check (about 22GB
  plus headroom). Keep the `MODEL_NAME`, `MODEL_MMPROJ`, `CTX_SIZE` semantics so the
  agent config stays identical across platforms.

#### I4. Service and daemon setup

- Blocker: lifecycle is `systemd` (`frpc` unit, docker restart policies) on Linux and
  Homebrew services on macOS. Neither exists on Windows.
- Approach: run `llama-server.exe` as a Windows Scheduled Task at logon (simplest,
  recommended first) or a Windows Service via WinSW or NSSM for machine scope.
  Health check by polling `http://localhost:7474/health` exactly as `cmd_status` does
  today. Keep `openmono start|stop|restart|status|logs` semantics but back them with
  service or task commands on Windows instead of `systemctl` or `docker compose`.

#### I5. Gateway and web services on Windows

- Blocker: Caddy, SearXNG, and Scrapling run as Linux containers in compose profiles.
  Scrapling pulls a full browser, which is the heaviest piece.
- Approach: Docker Desktop with the existing compose file covers all three unchanged
  (they are server side Linux images and do not care that the host is Windows). For a
  fully native option, Caddy has official Windows builds and SearXNG can run under
  Docker only in practice, so recommend: native `llama-server.exe` plus Docker Desktop
  only for the optional gateway services. The agent already degrades gracefully when
  the gateway is absent, so this can lag the core server port.

#### I6. Tunnel (`frpc`) on Windows

- Blocker: `scripts/setup-tunnel-inference.sh` plus systemd wiring is Linux only, with
  a Homebrew variant on macOS.
- Approach: ship `frpc.exe` (upstream publishes Windows builds) with a TOML config
  under `%APPDATA%` or `%PROGRAMDATA%`, installed as a Scheduled Task or service. No
  protocol work is needed. The relay contract is unchanged.

#### I7. Packaging and testing for Milestone 2

- Packaging: extend the Milestone 1 Windows distribution with a `llama-server.exe`
  flavor bundle (CUDA, Vulkan, CPU), the model download step, and the Scheduled Task or
  service registration. Winget or a versioned zip per flavor. Sign the binaries.
- Testing: `windows-latest` CI with an NVIDIA GPU runner is generally not available on
  hosted runners, so plan for CPU smoke on hosted CI (server boots, `/health` returns
  200, one tiny completion) plus self hosted GPU validation for the CUDA tier (model
  load, `/props` context size, tokens per second spot check, vision mmproj load). Add
  Windows paths to the existing `status` and `logs` flows and verify Defender, long
  paths, and pagefile behavior on the CPU tier.

### 3.3 Effort estimate for Milestone 2

Medium to large, dominated by the native build matrix and GPU validation, not by agent
code. Rough shape: native `llama-server.exe` build and packaging (medium), CUDA and
Vulkan tier validation across 12GB, 16GB, and 24GB cards (medium to large, hardware
bound), model download and service setup in the Windows installer (small to medium),
gateway services via Docker Desktop (small, mostly docs), CI CPU smoke plus self hosted
GPU checks (small to medium). The Docker Desktop plus WSL2 bridge (option (b) in I1)
could deliver a usable Windows inference story much sooner but leaves GPU performance
and packaging rough edges open, so it is best framed as an interim step, not the end
state.

### 3.4 Open questions for Milestone 2

These need maintainer decisions before implementation:

1. Is Docker Desktop on Windows acceptable as the interim inference path, or should
   Milestone 2 wait for native `llama-server.exe` builds.
2. Which GPU tiers to support at launch on Windows (24GB only first, or 12GB and 16GB
   lower accuracy tiers from day one), and whether CPU only Windows inference is worth
   shipping given the 24GB RAM guidance.
3. Whether to publish per flavor bundles (CUDA, Vulkan, CPU) or one installer that
   detects hardware and downloads the right server binary.
4. Signing and distribution channel for model weights and server binaries (winget,
   GitHub releases, or app installer), and whether Defender SmartScreen friction is
   acceptable for the first release.

---

## 4. Open questions for the maintainer (Milestone 1 decisions needed)

1. Shell choice on Windows: PowerShell first with cmd fallback (recommended), or git
   bash as a required dependency. This drives B1, B2, B7, and the test matrix.
2. Distribution shape: self contained single file `openmono.exe` (recommended) or
   framework dependent plus a .NET 10 runtime bootstrap. This decides installer size
   and update mechanics.
3. Installer channel: winget, zip with PATH setup, or both for the first release, and
   whether binaries will be signed (SmartScreen friction is real for unsigned exes).
4. Scope of the Windows launcher: full `openmono` subcommand parity in .NET, or agent
   role only (`config`, `agent`, `status`, `help`, `version`) with inference commands
   hidden on Windows. Recommended: agent role only for Milestone 1.
5. LSP and MCP support bar: which language servers and MCP servers must work on day
   one, and whether degraded modes (LSP off, managed search fallback) are acceptable
   with clear messages versus hard requirements on `rg`, Node, Python, and .NET.
6. Playbook scripts: label the 17 example `.sh` scripts as Unix only for now, or port
   the most used ones to PowerShell as part of Milestone 1.
7. Telemetry for the Windows test matrix: which shells and terminals are in scope
   (Windows Terminal plus PowerShell, cmd conhost, VS Code integrated terminal), and
   whether Windows 10 and 11 are both release targets or 11 only with best effort on 10.

---

## Appendix A. Verification log (what was and was not verified)

- Verified by reading code on Linux: architecture and runtime facts in section 1,
  all file and line references in B1 to B11 and I1 to I7, the `OperatingSystem.IsWindows`
  branches that already exist, and the counts from a static scan (5 `/bin/bash` spawn
  sites, 7 Unix path or clipboard binary references, 2 hardcoded `/workspace` defaults,
  3 `HOME` without `USERPROFILE` fallback sites, 22 repo shell scripts, 17 playbook
  example `.sh` files).
- Simulated Windows path handling statically only (string level scan of path, device,
  UNC, and clipboard logic). No Windows machine was available, so no Windows behavior
  was executed.
- Not verified: `dotnet build` and the `OpenMono.Tests` suite could not run because the
  `dotnet` SDK is not installed on this Linux VM. No Windows CI exists yet to run them
  on `windows-latest`. Recommend making the new Windows CI job the first deliverable so
  every later claim in this plan is tested.
- Playbook loader tilde expansion, `Terminal.Gui` behavior on Windows, and the VS Code
  extension Docker flow against a native Windows agent were reviewed but not executed.
  They are listed as verify items rather than stated as fact.

## Appendix B. File reference index

Agent code most relevant to the port, all under `src/OpenMono.Cli/`:

- `Tools/BashTool.cs` (lines 10 to 25, 94, 104 to 105, 186 to 241)
- `Hooks/HookRunner.cs` (lines 99 to 146)
- `Utils/ProcessRunner.cs` (lines 14 to 50)
- `Utils/GitHelper.cs` (whole file, via `ProcessRunner`)
- `Commands/InitCommand.cs` (lines 70 to 80)
- `Tools/GrepTool.cs` (lines 56 to 114)
- `Tools/GlobTool.cs` (managed, no change expected)
- `Tools/SanityCheck.cs` (lines 20 to 30, 58, 141 to 149, 184)
- `Permissions/PermissionEngine.cs` (lines 92, 164, 295, 407)
- `Permissions/PathGuard.cs` (lines 5 to 8, 154 to 168)
- `Rendering/AnsiInputReader.cs` (lines 1011 to 1064)
- `Utils/DesktopNotifier.cs` (lines 60 to 124)
- `Utils/SystemPrompt.cs` (lines 198 to 216)
- `Agents/AgentDefinition.cs` (line 121)
- `Lsp/LspServerManager.cs` (lines 17 to 25) and `Lsp/LspClient.cs` (lines 27 to 71)
- `Mcp/McpClient.cs` (around line 35) and `Mcp/McpServerManager.cs`
- `Config/AppConfig.cs` (`LlmConfig.Endpoint` line 64, `McpServerSettings` lines 48 to 55)
- `Config/ConfigLoader.cs` (data dir fallback, lines 66 to 118)
- `Session/SessionManager.cs` (lines 14, 63) and `History/FileHistory.cs` (line 17)
- `Acp/AcpLockFileWriter.cs` (lines 22 to 50) and `Acp/AcpServerSettings.cs`
- `Program.cs` (usage line 60, Docker detection line 269, ACP lines 315 to 352,
  hints lines 661 to 677 and 895, recovery lines 782 to 843)

Install, packaging, and infra:

- `openmono` (bash launcher, `cmd_agent` lines 793 to 911, OS dispatch with
  `Unsupported OS` error, `setup` gateway and tunnel flows)
- `get-openmono.sh`, `install.ps1`, `scripts/install.sh`, `scripts/install_prereqs.sh`,
  `scripts/lib/shell-integration.sh`, `docker/entrypoint.sh`
- `docker/docker-compose.yml` (agent and inference services), `docker/Dockerfile.agent`,
  `docker/Dockerfile.llama` (reference only), `src/OpenMono.Cli/OpenMono.Cli.csproj`,
  `global.json`, `Directory.Build.props`, `docs/SETUP.md`, `docs/CONFIG.md`,
  `docs/ARCHITECTURE.md`, `docs/MODELS.md`
