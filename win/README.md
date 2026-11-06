# OpenMono on Windows (Foundry Local)

Windows install and host control lives here under `win/`. The agent itself
stays in the unchanged Linux Docker container; what Windows changes is the
host side: Microsoft Foundry Local inference on `http://127.0.0.1:7474`
(reached from containers as `http://host.docker.internal:7474`) and
PowerShell lifecycle scripts for Docker Desktop checks and stack start/stop.

## Principles (same product, one platform difference)

This is the same OpenMonoAgent.ai on Windows, not a separate app. The one
intentional platform difference is inference: Foundry Local instead of
llama.cpp. Everything else matches the existing project: the agent loop and
its 21 tools run unchanged in the Linux container, the guarded tool pipeline
and PermissionEngine, ACP on 7475, graph tools, the VS Code extension API,
`settings.json` keys (`OPENMONO_ENDPOINT`, `OPENMONO_MODEL`,
`OPENMONO_VISION_ENABLED`, `inference.vision_enabled`), and local-first
privacy (loopback only on the host; no prompt, image, or file content leaves
the machine). oma-bot stays Linux. Windows code manages the host (install,
Docker checks, Foundry lifecycle, stack start/stop); it does not invent a
different UX or workflow. See `docs/BUILD-PLAN.md` (the approved plan),
`docs/VISION.md`, and `docs/WINDOWS-ACP-GRAPH-VSCODE.md`.

This is the same OpenMonoAgent.ai on Windows, not a separate app. The one
intentional platform difference is inference: Foundry Local instead of
llama.cpp. Everything else matches the existing project: the agent loop and
its 21 tools, the guarded tool pipeline and PermissionEngine, ACP on 7475,
graph tools, the VS Code extension API, `settings.json` keys
(`OPENMONO_ENDPOINT`, `OPENMONO_MODEL`, `OPENMONO_VISION_ENABLED`,
`inference.vision_enabled`), and local-first privacy (loopback only: the host
never binds `0.0.0.0`, and no prompt, image, or file content leaves the
machine). oma-bot stays Linux. Windows code manages the host (install,
Docker checks, Foundry lifecycle, stack start/stop); it does not invent a
different UX or workflow. See `docs/BUILD-PLAN.md` (the approved plan),
`docs/VISION.md`, and `docs/WINDOWS-ACP-GRAPH-VSCODE.md`.

- `docs/BUILD-PLAN.md` is the approved plan for this branch, plus the
  container-decision addendum.
- `src/OpenMono.Win.Foundry` wraps Foundry Local: CLI detection, GPU/VRAM and
  CUDA checks, catalog alias resolution, download/load, OpenAI-compatible server
  adopt-or-start on `127.0.0.1:7474`, health checks, smaller-model fallback.
- `src/OpenMono.Win.AgentHost` holds host controls: PowerShell shell selection,
  container stack config (endpoint mapping to host Foundry, ACP wiring, env),
  host guardrail patterns, vision flag mapping, and graph artifact checks.
  The agent itself stays in the Linux container.
- `tests/OpenMono.Win.Tests` runs without a GPU (fakes for CLI, GPU, and HTTP).
- `scripts/` has host PowerShell helpers: `bootstrap.ps1` (Foundry install),
  `check-cuda.ps1` (GPU/CUDA visibility), `start-stack.ps1` (Docker check,
  Foundry adopt, agent container start), `stop-stack.ps1`.

Branch rules: this code lives only on the `win` branch. Never touch `main`,
never merge to `main`, never open a pull request. Do not use the old `Windows`
branch or the legacy `windows/` folder as a starting point.
