# OpenMono — Setup & Commands

## System Requirements

- **Ubuntu**: 26.04 LTS (recommended) or 25.10 (minimum)
- **Docker**: Included in setup
- **.NET**: 10 (bundled)

## First-time setup

``` 
bash <(curl -fsSL https://raw.githubusercontent.com/StartupHakk/OpenMonoAgent.ai/refs/heads/main/get-openmono.sh)
```

Or clone and run manually:

```bash
git clone https://github.com/StartupHakk/OpenMonoAgent.ai.git ~/openmono.ai
cd ~/openmono.ai

chmod +x openmono scripts/*.sh
echo 'export PATH="$HOME/openmono.ai:$PATH"' >> ~/.bashrc
source ~/.bashrc

openmono setup              # auto-detects GPU/CPU
openmono setup --cpu        # force CPU mode
openmono setup --gpu        # force GPU mode (requires NVIDIA)
openmono setup --verbose    # stream every command's output
```

Setup installs prerequisites (Docker, .NET, CUDA if GPU), downloads the model (~15–18 GB), builds Docker images, and starts llama-server. Full log written to `~/.openmono/logs/setup-<timestamp>.log`.

## Daily commands

```bash
openmono start      # start llama-server (if not already running)
openmono stop       # stop all containers
openmono restart    # restart llama-server
openmono status     # container + GPU + model status
openmono logs       # tail llama-server logs
openmono agent      # run the coding agent in the current directory
openmono help       # list all commands
```

## Slash commands

| Command | Description |
|---------|-------------|
| `/help` | Show all commands and keyboard shortcuts |
| `/model <name>` | Switch model mid-session |
| `/think` | Toggle step-by-step reasoning mode |
| `/compact` | Summarize history to free context space |
| `/checkpoint` | Checkpoint conversation (named save point) |
| `/undo [n]` | Revert last n file changes |
| `/resume [id]` | Resume a previous session (lists recent if no ID) |
| `/export [format] [path]` | Export conversation — `markdown`, `json`, or `html` |
| `/status` | Session status: turn count, token usage, model, working directory |
| `/stats` | Token usage and tool call statistics |
| `/init` | Auto-generate `OPENMONO.md` from the current project |
| `/debug` | Toggle verbose debug output |
| `/clear` | Clear context and start fresh (preserves system prompt) |
| `/retry` | Resend the last message |

## Keyboard shortcuts

| Shortcut | |
|----------|---|
| <kbd>Ctrl</kbd>+<kbd>C</kbd> | Cancel active turn · double-tap to exit |
| <kbd>Ctrl</kbd>+<kbd>T</kbd> | Toggle thinking panel |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> | Toggle tool sidebar |
| <kbd>Ctrl</kbd>+<kbd>A</kbd> | Toggle approval mode |
| <kbd>Ctrl</kbd>+<kbd>D</kbd> | Toggle debug mode |
| <kbd>Ctrl</kbd>+<kbd>U</kbd> | Clear input line |
| <kbd>Ctrl</kbd>+<kbd>W</kbd> | Delete last word |
| <kbd>Ctrl</kbd>+<kbd>P</kbd> | Pause / resume streaming |
| <kbd>Tab</kbd> | Autocomplete command or file |
| <kbd>Esc</kbd> | Cancel active request / dismiss suggestions |
| <kbd>F1</kbd> | Help overlay |
| <kbd>↑</kbd> / <kbd>↓</kbd> | Navigate input history |
| <kbd>PageUp</kbd> / <kbd>PageDown</kbd> | Scroll conversation |

Shortcuts can be customised in `~/.openmono/tui.json` (user) or `.openmono/tui.json` (project).

---

