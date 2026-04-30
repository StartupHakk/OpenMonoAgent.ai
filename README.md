<div align="center">
  <img src="docs/assets/logo.png" alt="OpenMonoAgent" width="480" />
</div>

<div align="center">
  <strong>Open-source coding agent. Local-first. Zero cost. Zero cloud.</strong><br/>
  <sub>Built to democratize AI. Powered by .NET.</sub>
</div>

<br>

<div align="center">
  <a href="#quickstart">Quickstart</a> · <a href="#how-it-compares">How it compares</a> · <a href="#whats-inside">What's inside</a> · <a href="#supported-hardware">Hardware</a> · <a href="#docs">Docs</a> · <a href="ROADMAP.md">Roadmap</a> · <a href="#contributing">Contributing</a>
</div>

<br>

<div align="center">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/license-AGPL--3.0-green" alt="GNU AGPL-3.0 License" />
  <img src="https://img.shields.io/badge/docker-ready-2496ED?logo=docker&logoColor=white" alt="Docker" />
  <img src="https://img.shields.io/badge/llama.cpp-local%20inference-black?logo=llama&logoColor=white" alt="llama.cpp" />
  <img src="https://img.shields.io/badge/self--hosted-yes-brightgreen" alt="Self-hosted" />
  <img src="https://img.shields.io/badge/platform-Linux-FCC624?logo=linux&logoColor=black" alt="Linux" />
</div>

---

OpenMono is a coding agent that runs entirely on your hardware — no subscriptions, no data leaving your network, no per-token billing. It pairs a .NET 10 CLI with its own llama.cpp inference server, giving you a full agentic loop with 20 built-in tools, Docker sandboxing, and deep code intelligence. GPU or CPU, it auto-configures itself. You own the model, the compute, and the data.

---

## Quickstart

```
bash <(curl -fsSL https://raw.githubusercontent.com/StartupHakk/OpenMonoAgent.ai/refs/heads/main/get-openmono.sh)
```

Then from any project:

```bash
cd your-project/

openmono agent          # TUI mode (default)
openmono agent --classic    # classic scrolling terminal
```

<div align="center">
  <img src="docs/assets/tui-snapshot-openmono.png" alt="OpenMono TUI" width="780" />
</div>

> [!NOTE]
> TUI mode is the default for interactive terminals. Use `openmono  agent --classic` for CLI.

→ [Full command reference](docs/SETUP.md) — daily commands, setup flags, GPU/CPU options

---

## How it compares

Most coding agents are cloud products wearing an open-source label. Your prompts, your code, and your context hit someone else's servers on every keystroke. You pay per token, forever, with no ceiling.

OpenMono runs the model on your hardware via llama.cpp — an RTX 3090 or a workstation NUC is all you need. After the one-time setup, inference costs nothing. Your code never leaves the machine. No account, no usage dashboard, no API key.

It's a full [agentic loop](docs/ARCHITECTURE.md): 20 tools, sub-agents, Docker sandboxing, LSP code intelligence, native Roslyn C# analysis, MCP integration, and [playbooks](docs/PLAYBOOKS.md). Runs at ~45 tok/s on GPU, ~20 tok/s on CPU.


|  | **OpenMono** | Claude Code | OpenCode |
|--|:-------------|-------------|----------|
| **Inference cost** | Zero per token (local) | Per-token billing | Per-token billing |
| **Data privacy** | Fully offline capable | Cloud only | Depends on provider |
| **Default inference** | llama.cpp bundled, zero config | Anthropic API required | BYO provider, no bundled inference |
| **Sandboxing** | Docker-native | Host process | Host process |
| **Code intelligence** | LSP + Roslyn + MCP graph tools | File reads | LSP (30+ servers) |
| **Extensibility** | [Playbooks](docs/PLAYBOOKS.md) (typed, composable) | Skills (markdown) | Plugins (TS SDK) |
| **MCP** | Client (stdio) | Full client | Full client |
| **UI** | TUI + CLI | Web, Desktop, VS Code, CLI | TUI, Desktop, Web |
---

## What's inside

<table>
<tr>
<td width="50%" valign="top">

**01 · Bundled inference — zero config, zero cost**  
llama.cpp ships inside Docker. Installer detects your hardware and picks the right model. After setup, every token is free.

`GPU` Qwen3.6-27B dense · ~60 tok/s  
`CPU` Qwen3.6-35B-A3B MoE · ~20 tok/s

→ [Models & reasoning mode](docs/MODELS.md)

</td>
<td width="50%" valign="top">

**02 · Agentic loop that earns its name**  
25 iterations per turn. Doom-loop detection aborts if the same tool sequence repeats 3×. Checkpoints at 65% context fill, compacts at 80%. Runs until done — then stops.

</td>
</tr>
<tr>
<td valign="top">

**03 · [20 tools](docs/ARCHITECTURE.md), 12-step pipeline**  
Every call: parse → schema validate → path sanity → plan-mode guard → capability check → cache → pre-hook → execute → post-hook → artifact store. Read-only tools run in parallel. Nothing bypasses the pipeline.

</td>
<td valign="top">

**04 · 5 specialist sub-agents**  
Isolated sessions with locked tool sets and turn budgets:

`Explore` · read-only discovery · 15 turns  
`Plan` · architecture, no writes · 10 turns  
`Coder` · full file access · 30 turns  
`Verify` · adversarial + Roslyn · 20 turns  
`general-purpose` · everything · 25 turns

</td>
</tr>
<tr>
<td valign="top">

**05 · Docker sandbox**  
Project mounts as `/workspace`. The agent reads and writes your real files — that's the blast radius. Nothing outside that mount is visible or reachable.

</td>
<td valign="top">

**06 · Deep code intelligence**  
Roslyn: type hierarchy, blast-radius, cross-file symbol search, callers, diagnostics — 5-min compilation cache. LSP for TypeScript, Python, Go, Rust, lazy-started on first use.

Auto-detects [graphify](docs/graphify.md) (semantic concept graph, 25+ languages) and [code-review-graph](docs/code-review-graph.md) (structural call graph via MCP, ~22 tools) if installed — no config needed.

</td>
</tr>
<tr>
<td valign="top">

**07 · [Playbooks](docs/PLAYBOOKS.md)**  
YAML workflows with typed parameters, conditional gates, and checkpoint/resume. Composable — one playbook can call another.

</td>
<td valign="top">

**08 · [4 providers](docs/MODELS.md), hot-swappable**  
Local llama.cpp is the default and fully supported. OpenAI, Anthropic, and Ollama are available but WIP — see [Models](docs/MODELS.md) for details.

</td>
</tr>
</table>

> **09 · Distributed inference** — Agent on your laptop, inference on a separate GPU machine. Free relay at [app.openmonoagent.ai](https://app.openmonoagent.ai).

---

## Supported Hardware

| | GPU | CPU |
|--|-----|-----|
| **Model** | Qwen3.6-27B-Q4_K_M | Qwen3.6-35B-A3B-UD-Q4_K_XL |
| **Minimum** | 24 GB VRAM | 24 GB RAM |
| **Speed** | ~45–50 tok/s (RTX 3090) | ~17–20 tok/s |
| **Auto-configured** | ✓ | ✓ |

> [!NOTE]
> The installer detects your hardware and selects the right model automatically — no config needed. Requires Ubuntu 26.04 LTS (recommended) or 25.10.

## Architecture

A .NET 10 CLI driving a local llama.cpp inference server over HTTP, everything sandboxed in Docker. The agent streams tokens, dispatches tool calls through a 12-step pipeline, and loops until done.

→ [Full architecture + diagram](docs/ARCHITECTURE.md)

## Configuration

Settings load from `~/.openmono/settings.json` (user-level) or `.openmono/settings.json` (project-level) — reference, providers, permissions, MCP servers

→ [Full configuration reference](docs/CONFIG.md)

## Commands & shortcuts

14 slash commands including `/think`, `/undo`, `/resume`, and `/export`. Full keyboard shortcut reference for TUI mode.

→ [Commands, slash commands & keyboard shortcuts](docs/SETUP.md)

## Docs

- [Roadmap](ROADMAP.md)
- [Setup & commands](docs/SETUP.md) — daily commands, TUI vs classic, flags
- [Architecture](docs/ARCHITECTURE.md) — .NET CLI + llama.cpp + Docker, full diagram
- [Models & reasoning mode](docs/MODELS.md)
- [Configuration](docs/CONFIG.md) — settings.json, providers, permissions, MCP servers
- [Tools](docs/ARCHITECTURE.md)
- [Playbooks](docs/PLAYBOOKS.md)
- [graphify](docs/graphify.md) — semantic code graph, 25+ languages
- [code-review-graph](docs/code-review-graph.md) — structural call graph via MCP
- [Contributing](CONTRIBUTING.md)

---

## Contributing

OpenMono is early and moving fast. Contributions are welcome — new tools, providers, LSP servers, playbooks, bug fixes, or docs.

Read the [contributing guide](CONTRIBUTING.md) before opening a PR.

---

<div align="center">
  <br>
  <em>"AI shouldn't be a subscription you rent. It should be infrastructure you own —<br>sitting on your desk, serving your code, answering only to you."</em><br><br>
  <sub>— Startup Hakk</sub>
</div>

<br>

<div align="center">
  <a href="https://startuphakk.com"><img src="docs/assets/STARTUP-HAKK-logo.jpg" alt="StartupHakk" width="140" /></a><br>
  <sub>GNU AFFERO GENERAL PUBLIC LICENSE v3.0 · © 2026 StartupHakk</sub>
</div>
