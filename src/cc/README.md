# OpenMonoAgent.ai Control Panel

This folder is the home of the Control Panel, the browser UI that ships with the agent install. Implementation will live here. Nothing in this folder is part of the .NET build yet: there is no project file, and `OpenMono.sln` does not reference this directory.

| File | What it is |
|---|---|
| [control-panel-plan.md](control-panel-plan.md) | Product design: who it is for, why ACP stays the chat API, what to take from Agent Canvas, security posture |
| [PLAN.md](PLAN.md) | Build plan: folder layout, agent changes, command wrapper, pairing, packaging, tests, tasks |
| [api.md](api.md) | HTTP contract the panel uses, including the ACP routes that already exist |
| [screens.md](screens.md) | Every MVP screen and the states it can be in |

Paths such as `docs/DEPLOYMENT-MODES.md` in these files are from the repository root.
