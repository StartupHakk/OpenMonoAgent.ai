# OpenMono on Windows (Foundry Local)

Windows-only host code lives here under `win/`. It runs the existing OMA agent
(`src/OpenMono.Cli`, referenced as a project, never moved or rewritten) against
Microsoft Foundry Local inference on `http://127.0.0.1:7474`.

- `docs/BUILD-PLAN.md` is the approved plan for this branch.
- `src/OpenMono.Win.Foundry` wraps Foundry Local: CLI detection, GPU/VRAM and
  CUDA checks, catalog alias resolution, download/load, OpenAI-compatible server
  adopt-or-start on `127.0.0.1:7474`, health checks, smaller-model fallback.
- `src/OpenMono.Win.AgentHost` hosts `OpenMono.Cli` in process with Windows tool
  overrides (PowerShell shell, guardrails, clipboard, LSP paths, MCP `.cmd`
  helper, prompt overlay).
- `tests/OpenMono.Win.Tests` runs without a GPU (fakes for CLI, GPU, and HTTP).
- `scripts/` has first-run PowerShell helpers (`bootstrap.ps1`, `check-cuda.ps1`).

Branch rules: this code lives only on the `win` branch. Never touch `main`,
never merge to `main`, never open a pull request. Do not use the old `Windows`
branch or the legacy `windows/` folder as a starting point.
