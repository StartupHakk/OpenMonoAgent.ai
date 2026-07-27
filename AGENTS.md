# AGENTS.md

## Cursor Cloud specific instructions

OpenMono is a **.NET 10 CLI coding agent** (project `src/OpenMono.Cli`, tests
`src/OpenMono.Tests`). The full product also ships a llama.cpp inference server,
optional web services (SearXNG/Scrapling/Caddy), and an frpc tunnel — but those
require Docker + a ~15–22 GB GPU/CPU model download and are **not** part of the
day-to-day dev loop. For development you only need the .NET SDK.

### Environment

- The .NET 10 SDK (`10.0.100`, pinned by `global.json`) is installed at
  `~/.dotnet` and added to `PATH`/`DOTNET_ROOT` in `~/.bashrc`. New interactive
  shells get `dotnet` automatically; non-interactive contexts can invoke it as
  `~/.dotnet/dotnet`.
- The startup update script runs `dotnet restore OpenMono.sln`. No database or
  other services are needed to build/test — state is local JSON under `~/.openmono`.

### Build / test / lint (run from repo root)

- Build: `dotnet build OpenMono.sln` (Debug; `Directory.Build.props` sets
  `TreatWarningsAsErrors=true`, so warnings fail the build).
- Test: `dotnet test OpenMono.sln` — ~379 unit tests pass. Three
  `Integration.SmokeTests` are `[SkippableFact]` and **skip unless an
  OpenAI-compatible LLM endpoint is reachable** (probes `OPENMONO_TEST_ENDPOINT`,
  then `http://localhost:7474`, then `:11434`).
- Lint/format: `dotnet format OpenMono.sln --verify-no-changes`. Note: the
  checked-in tree currently has **pre-existing whitespace formatting violations**
  (exit code 2) that are unrelated to any new change — don't treat a non-zero
  exit here as caused by your edit. Run `dotnet format` (no flags) to auto-fix
  only the files you touched before committing.

### Running the agent without a real model (dev/test only)

The agent speaks the OpenAI streaming chat API (`/health`, `/props`,
`/v1/chat/completions`). To exercise the full agentic loop end-to-end without the
heavy model, point it at any OpenAI-compatible endpoint and run classic mode:

```bash
dotnet run --project src/OpenMono.Cli -- \
  --classic --no-acp --endpoint http://localhost:7474 --workdir <some-project>
```

- Classic mode (`--classic`) is far more robust for headless/non-TTY automation
  than the default full-screen TUI. Use `--no-acp` to avoid binding the ACP
  server on port 7475.
- The same trick makes the 3 skipped smoke tests run: start a mock OpenAI server
  on `:7474`, then `OPENMONO_TEST_ENDPOINT=http://localhost:7474 dotnet test`.
- Real inference (`openmono` launcher, `scripts/install.sh`, Docker, model
  download) is heavyweight/interactive and not needed for code work.
