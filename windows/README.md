# OpenMono for Windows (LM Studio style desktop product)

Purely additive Windows product. Everything new lives in this `windows/` folder.
No file outside `windows/` is moved, renamed, restructured, or edited, except for
the single allowed addition of `.github/workflows/windows-desktop.yml` (CI only,
builds and tests `windows/` on `windows-latest`).

Existing OMA code in `src/`, `scripts/`, `docker/`, and `docs/` is reused via
project references to `src/OpenMono.Cli`, wrappers and adapters inside
`windows/`, and by referencing the existing `docker/` compose definitions
without editing them.

## Layout

```text
windows/
  OpenMono.Windows.sln
  README.md
  VERSION.windows
  global.json
  Directory.Build.props            (windows subtree only)
  src/
    OpenMono.Hardware/             (net10.0, GPU/RAM/disk detection, tier selection)
    OpenMono.Models/               (net10.0, models.json tier mirror, downloader, checksums)
    OpenMono.Supervisor/           (net10.0, llama-server + Docker stack supervision, ports, health)
    OpenMono.AgentHost/            (net10.0, in-process agent host, Windows tool overrides)
    OpenMono.Desktop/              (net10.0-windows, WinUI 3 app: chat, models, server, settings, wizard)
    OpenMono.SmokeTest/            (net10.0 console, headless CI smoke test)
  tests/
    OpenMono.Windows.Tests/        (net10.0 xUnit)
  installer/
    inno/openmono.iss              (per-user Inno Setup installer)
  docker/
    docker-compose.windows.yml     (override by reference: Caddy upstream points at native llama-server)
  build/
    build.ps1 fetch-llama.ps1 fetch-rg.ps1 fetch-node.ps1 smoke-test.ps1 new-release.ps1
  docs/
    windows-architecture.md first-run-spec.md manual-test-plan.md
  thirdparty/
    README.md SBOM.md
```

## Decisions (Spencer overrides, current)

1. No embedded terminal. The agent is hosted in process with a native WinUI 3
   chat window (streaming text, collapsible thinking, tool call cards, native
   permission dialogs, slash command palette, session history).
2. Docker Desktop (WSL2 backend) is kept for Caddy, SearXNG, and Scrapling.
   Inference (`llama-server`) runs native on Windows on 127.0.0.1:7474 for
   direct GPU access. The app detects Docker Desktop, offers install/start,
   supervises the services stack, and the agent uses the gateway exactly as on
   Linux, with DuckDuckGo and direct fetch fallback when Docker is absent.
3. Inference tiers mirror `scripts/install.sh` exactly.
4. Installer is per-user Inno Setup `setup.exe`, self contained .NET publish,
   VC++ redist check, lean installer with GPU flavor and model downloaded on
   first run, Velopack-ready auto update, keep or delete data prompt on
   uninstall. Code signing is an optional pipeline step skipped when no cert
   is provided.
5. Scope is M1 through M3. Open questions use the plan recommendations
   (WinUI 3, no telemetry by default, loopback ACP toggle, per-user install).

## Build on Windows

```powershell
cd windows
.\build\build.ps1 -Configuration Release
dotnet test ..\windows\tests\OpenMono.Windows.Tests\OpenMono.Windows.Tests.csproj
```

Publish the desktop app self contained, then compile the installer with
Inno Setup 6 (see `installer/inno/openmono.iss`). Signing runs only when
`WINDOWS_CERT_PATH` (PFX) and `WINDOWS_CERT_PASSWORD` are set.

## CI

`.github/workflows/windows-desktop.yml` builds and tests `windows/` on
`windows-latest`, publishes the app, compiles the installer, and runs a
headless smoke test of the supervisor against a stub OpenAI-compatible server.
