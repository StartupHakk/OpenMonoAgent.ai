# OpenMono on Windows (Foundry Local)

Windows-only host code lives here under `win/`. It runs the existing OMA agent
(`src/OpenMono.Cli`, referenced as a project, never moved or rewritten) against
Microsoft Foundry Local inference on `http://127.0.0.1:7474`.

## Principles (same product, one platform difference)

This is the same OpenMonoAgent.ai on Windows, not a separate app. The one
intentional platform difference is inference: Foundry Local instead of
llama.cpp. Everything else matches the existing project: the agent loop and
its 21 tools, the guarded tool pipeline and PermissionEngine, ACP on 7475,
graph tools, the VS Code extension API, `settings.json` keys
(`OPENMONO_ENDPOINT`, `OPENMONO_MODEL`, `OPENMONO_VISION_ENABLED`,
`inference.vision_enabled`), and local-first privacy (loopback only: the host
never binds `0.0.0.0`, and no prompt, image, or file content leaves the
machine). Windows code adapts paths, shells, and executables; it does not
invent a different UX or workflow. See `docs/BUILD-PLAN.md` (the approved
plan), `docs/VISION.md`, and `docs/WINDOWS-ACP-GRAPH-VSCODE.md`.

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
