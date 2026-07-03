# Setup & Commands

## Requirements

| | |
|--|--|
| **OS** | Ubuntu 26.04 LTS (recommended) · 25.10 · **macOS 14+** (Sonoma/Sequoia) |
| **GPU mode** (Linux) | NVIDIA GPU · 12 GB VRAM minimum · 24 GB recommended |
| **CPU mode** (Linux) | 24 GB RAM |
| **Apple Silicon** (macOS) | M1+ · **64 GB+ unified memory recommended** (tested) · less than 64 GB not encouraged |
| **Disk** | ~22 GB free (model + ~900 MB vision projector) |

> [!NOTE]
> Both Linux and macOS use the same install command — the installer detects your OS and architecture automatically. Intel Macs are supported in **agent-only** mode; native inference requires Apple Silicon. See [macOS notes](#macos-notes) below.

---

## Step 1 — Install

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/StartupHakk/OpenMonoAgent.ai/refs/heads/main/get-openmono.sh)
```

The installer will ask you one question before it does anything:

> [!IMPORTANT]
> ### What do you want to install on this machine?
>
> 1. **Both** — agent + inference server on one box (single-box mode)
> 2. **Inference server only** — inference box that runs the model (dual-box mode)
> 3. **Agent only** — laptop/workstation that talks to a remote inference server
>
> *Picking **2** or **3**? See the [Dual-box setup](#dual-box-setup) section below for the final connection steps.*


| Option | Pick this if… |
|--------|--------------|
| **1 — Both** | You have one machine and want everything on it |
| **2 — Inference** | Dedicated inference box — do this first, then run option 3 on your agent box |
| **3 — Agent** | Your agent box — run this after option 2 is set up on the inference box |

> [!TIP]
> Not sure? Pick **1**. If you have a GPU, this is the best starting point. If you're on CPU, make sure you have at least 24 GB RAM — the machine needs to run both the model and the agent. If you'd rather keep model load on a separate machine, go for **2** and **3**.

---

## Step 2 — During setup

The install runs in two phases, each with its own `[1/8]` → `[8/8]` progress.

**Phase 1** installs prerequisites: Docker, .NET 10, build tools, and the NVIDIA stack if a GPU is found.

**Phase 2** downloads the model, builds Docker images, and starts the inference server.

### GPU prompt

If an NVIDIA GPU is detected, Phase 1 asks:

```
  NVIDIA GPU Detected
  Would you like to install on GPU? (Y/n):
```

Say **Y**.

### Reboot (GPU driver installs only)

If the NVIDIA drivers are being installed fresh, a reboot is required:

```
  NVIDIA drivers installed — reboot required
  Would you like to reboot now? (Y/n):
  ℹ  After reboot, run: ~/openmono.ai/openmono setup
```

> [!IMPORTANT]
> After rebooting, use the **full path** — `openmono` won't be on your PATH until setup fully completes:
> ```bash
> ~/openmono.ai/openmono setup
> ```
> The installer picks up at Phase 2 automatically.

### Model download

The only slow step. The installer picks the right model based on your VRAM:

| VRAM | Model | Size | Accuracy | Context (vision on) |
|------|-------|------|----------|---------------------|
| 24 GB+ | Qwen3.6-27B-Q4_K_M | ~15.5 GB | Full | 168k |
| 16 GB | Qwen3.6-35B-A3B-UD-IQ3_S | ~12 GB | Lower | 96k |
| 12 GB | Qwen3.5-9B-Q4_K_M | ~5 GB | Lower | 168k |
| CPU | Qwen3.6-35B-A3B-UD-Q4_K_XL | ~17.6 GB | Full | 168k |

The installer also downloads the **multimodal projector** (mmproj, ~900 MB) for each model, which enables vision. A reduced context size is set automatically to keep the projector within VRAM/RAM budget. To disable vision and recover the extra context, see [disabling vision](#vision) below.

> [!NOTE]
> These are the default models for each tier. If you have more VRAM or RAM available, you can swap to a higher quant for better accuracy — or a lower quant to free up memory. Context size is also configurable: a larger window gives the agent more working memory but requires more RAM. Both can be changed in `settings.json` via `llm.model` and `llm.contextSize`, or by editing `docker/docker-compose.override.yml` directly.

To override auto-detection:

```bash
openmono setup --gpu     # force GPU (NVIDIA only)
openmono setup --cpu     # force CPU
```

> [!NOTE]
> You may see a `.NET SDK not installed` warning at the start of Phase 2 — safe to ignore. The SDK was just installed but the current shell session hasn't loaded it yet.

> [!TIP]
> Full install log is saved to `~/.openmono/logs/setup-<timestamp>.log`

---

## Step 3 — After install

When setup finishes you'll see:

```text
────────────────────────────────────────────────────────────
  Setup Complete
────────────────────────────────────────────────────────────

  ✓ OpenMono.ai is ready to use!

  Your machine is configured for single-box mode (agent + inference).

  Next steps:
    1. cd your-project/
    2. openmono agent                 # Start the agent

  Other commands:
    openmono status              # Show llama-server status
    openmono config             # Configure settings

  Troubleshooting:
    If openmono or docker are not found, reload your shell:
      newgrp docker     # Activate docker group (Linux only)
      source ~/.bashrc  # Reload shell config (bash)
      exec $SHELL       # Reload shell

  Full help: openmono --help
────────────────────────────────────────────────────────────
```

Reload your shell so the `openmono` command is on your PATH:

```bash
source ~/.bashrc    # macOS / zsh: source ~/.zshrc
```

> [!NOTE]
> On macOS there's no `docker` group — skip the `newgrp docker` step. If `openmono` isn't found, run `source ~/.zshrc` or open a new terminal.

If `openmono` or `docker` are still not found after that:

```bash
newgrp docker      # activate docker group without logging out
exec $SHELL        # reload shell
```

Confirm the inference server is running:

```bash
openmono status
```

---

## Step 4 — Run the agent

Navigate to any project and start the agent:

```bash
cd your-project/

openmono agent            # TUI — interactive panel layout (default)
openmono agent --classic  # CLI — plain scrolling terminal
```

Once it's running, just type what you need in plain English:

```
Explain what this codebase does
Find all usages of AuthService
Fix the failing tests in UserController
Refactor this function to be async
Add error handling to the payment flow
```

OpenMono navigates your codebase, proposes solutions, and executes changes with full transparency. You stay in control throughout — the agent shows its work at every step and asks before making any major actions, including file reads, edits, and running commands.

> [!TIP]
> Type `/think` or press `Ctrl+T` to enable step-by-step reasoning mode — best for complex bugs, large refactors, and architecture decisions. Turn it off for simple lookups and quick edits.

---

## Step 5 — Daily use

```bash
openmono start      # start the inference server
openmono stop       # stop everything
openmono restart    # restart the inference server
openmono status     # container · model status
openmono logs       # tail live inference logs
openmono help       # list all commands
```

---

## macOS notes

macOS is supported alongside Linux — the same install command works on both, and the installer routes to the macOS path automatically when it detects Darwin.

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/StartupHakk/OpenMonoAgent.ai/refs/heads/main/get-openmono.sh)
```

### How macOS differs from Linux

| | Linux | macOS (Apple Silicon) |
|--|-------|------------------------|
| **Inference** | llama.cpp in Docker (CUDA/CPU) | llama.cpp native on the host (Metal GPU) |
| **Agent sandbox** | Docker | Docker (Docker Desktop or Colima) |
| **Acceleration** | NVIDIA CUDA | Apple Metal — automatic |
| **Connection** | localhost | container reaches the host via `host.docker.internal` |

On Apple Silicon the model runs **natively** so it can use the Metal GPU and unified memory directly — Docker can't pass the GPU through on macOS. The agent still runs in a Docker container and talks to the native llama-server. Inference config is written to `~/.openmono/settings.json` and the generated API key is stored in `docker/.env`.

### Prerequisites (installed automatically)

Phase 1 installs, via Homebrew where needed:

- **Homebrew** + **Xcode Command Line Tools**
- Core tools: `git`, `curl`, `jq`, `cmake`, `ripgrep`, `openblas`, `pkg-config`, `python3` (3.10+)
- **llama.cpp** (Metal backend) — Apple Silicon only
- **Docker** — Docker Desktop if present, otherwise Colima is installed and started
- **.NET 10 SDK** (to `~/.dotnet`)

### Model tiers (Apple Silicon unified memory)

The installer reads `hw.memsize` and picks the model for your memory tier:

| Unified memory | Model | Accuracy | Context (vision on) | Status |
|----------------|-------|----------|---------------------|--------|
| 64 GB+ | Qwen3.6-35B-A3B-UD-Q4_K_XL | Full | 192k (168k) | ✅ Recommended / tested |
| 32 GB | Qwen3.5-9B-Q4_K_M | Lower | 64k (48k) | ⚠️ Not encouraged |
| 16 GB | Qwen3.5-9B-Q4_K_M | Lower | 16k (12k) | ⚠️ Not encouraged |

> [!IMPORTANT]
> **64 GB+ unified memory is the recommended, tested configuration** — full-accuracy 35B model at the full 192k context. The installer will still configure the 16 GB and 32 GB tiers (smaller 9B model, much tighter context), but **less than 64 GB is not encouraged** — they fall back to a smaller model with a much tighter context window. 16 GB is the hard floor: below it the full and inference roles refuse to install — use the **agent** role and point it at a separate inference server. As on Linux, vision (mmproj) downloads automatically and trims the context to keep the encoder within the memory budget.

### Intel Macs

Intel Macs have no Metal GPU and no unified memory, so native inference is unsupported. The installer allows only the **agent** role on Intel — it runs the agent locally and connects to a separate Apple Silicon or Linux inference box (see [Dual-box setup](#dual-box-setup)).

---

## Dual-box setup

Run the model on a dedicated inference box and connect from your laptop over the internet. No port forwarding required — the tunnel is established outbound from the inference box.

![Dual-box setup diagram](assets/dual-box-server.png)

### Step 1 — Install on the inference box (option 2)

On the inference box, run the installer and pick **2 — Inference server only**:

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/StartupHakk/OpenMonoAgent.ai/refs/heads/main/get-openmono.sh)
```

Select **2** when prompted. The installer downloads the model and starts llama-server. No agent is installed on this machine.

Confirm it's running:

```bash
openmono status
```

### Step 2 — Register the inference box with the relay

Still on the inference box, run tunnel setup:

```bash
openmono tunnel setup
```

You'll receive a one-time verification code. Enter it at [app.openmonoagent.ai](https://app.openmonoagent.ai) — you'll get an email with a step-by-step guide including your relay endpoint and API key.

> [!NOTE]
> The code expires in 15 minutes.

Then start the tunnel:

```bash
openmono tunnel start
```

Confirm the tunnel is up:

```bash
openmono tunnel status
```

### Step 3 — Install on the laptop (option 3)

Once the inference box is running and the tunnel is up, switch to your laptop and run the installer there:

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/StartupHakk/OpenMonoAgent.ai/refs/heads/main/get-openmono.sh)
```

Select **3** when prompted. This installs the agent but skips Docker, model download, and llama-server — the laptop needs no GPU.

### Step 4 — Point the agent at the relay

Using the endpoint and API key from the email in Step 2:

```bash
openmono config set llm.endpoint http://relay.openmonoagent.ai:<port>
openmono config set llm.api_key <token>
```

### Step 5 — Run the agent

```bash
cd your-project/
openmono agent
```

The agent on your laptop sends requests through the relay to the inference box.

> [!NOTE]
> Don't have a relay account? Sign up free at [app.openmonoagent.ai](https://app.openmonoagent.ai).

---

### Tunnel commands (inference box)

```bash
openmono tunnel start    # start the frpc tunnel
openmono tunnel stop     # stop the tunnel
openmono tunnel restart  # restart
openmono tunnel status   # show tunnel state + configured target
openmono tunnel logs     # tail frpc logs
```

---

### Troubleshooting

> [!CAUTION]
> **401 Unauthorized** — the API key on your laptop doesn't match the one on the inference box.
>
> Check both values:
> ```bash
> # On the inference box
> grep LLAMA_API_KEY docker/.env
>
> # On the laptop
> openmono config get llm.api_key
> ```
> If they differ, copy the inference box value to the laptop:
> ```bash
> openmono config set llm.api_key <value-from-inference-box>
> ```

---

## Vision

Vision is enabled by default when the mmproj is present. Set `OPENMONO_VISION_ENABLED=1` (or add `"vision_enabled": true` to `settings.json`) so the agent accepts and processes images.

### Attaching images in chat

Use the `@filename` syntax to attach an image alongside your message:

```
@screenshot.png what's wrong with this UI?
@diagram.jpg explain this architecture
```

Supported formats: PNG, JPG, JPEG, GIF, WebP. Images are automatically compressed before being sent — see [Image compression](#image-compression) below.

You can also ask the agent to read an image file directly:

```
Read src/assets/logo.png and describe it
```

### Tuning

**Image token budget** — controls how many tokens each image is allocated in the context window. Set in `docker/docker-compose.override.yml`:

```
--image-min-tokens 1024   # minimum tokens per image (lower = faster, less detail)
--image-max-tokens 1280   # cap per image — raise to 2048 for more detail, costs more context
```

A single image at the default budget uses ~1024–1280 tokens. If you're sending multiple images per message, reduce `--image-max-tokens` to keep context usage predictable.

**Image compression** — handled client-side by [SixLabors.ImageSharp](https://sixlabors.com/products/imagesharp/) before the image reaches the model:

- Images above ~1.3 MP (≈ 1280×1024) are resized down, keeping aspect ratio
- Re-encoded as JPEG at 90% quality
- This happens transparently in the CLI — the model always receives a compact, correctly-sized image regardless of the original file size

### VRAM usage during a vision session

VRAM at startup and VRAM mid-session are different things. When the server first loads, the model and mmproj are resident and the remaining headroom looks comfortable. During inference, two things grow on top of that: the **KV cache** (every processed token occupies space here) and a **prompt cache** (llama.cpp caches KV states from previous requests to speed up repeated context — defaults to 8192 MiB). Both accumulate as the conversation continues.

Each image adds roughly 1,000–1,280 tokens to the KV cache on top of the text. After a few exchanges the headroom that looked available at startup may be significantly reduced, and the vision encoder needs a short burst of extra VRAM each time it processes a new image. If that burst can't be satisfied, the server will crash with an out-of-memory error.

If you run into this, two knobs in `docker/docker-compose.override.yml`:

```
--cache-ram 2048    # cap prompt cache at 2048 MiB instead of the default 8192 MiB
--cache-ram 0       # disable prompt cache entirely — maximum headroom, no prefix caching
--cache-reuse 256   # raise the reuse threshold — only reuse cached KV if ≥256 tokens match;
                    # doesn't reduce cache size but avoids cache thrash on short prompts
```

### Disabling vision

The mmproj uses ~1–2 GB of VRAM/RAM and reduces the context window to compensate (e.g. 192k → 168k on the 24 GB tier). To disable it and recover that context:

1. Open `docker/.env` and clear `MODEL_MMPROJ=` (set it to empty)
2. Restore the full context size: e.g. `CTX_SIZE=196608` (the value printed during setup)
3. Restart llama-server: `docker compose up -d llama-server`

Or set `OPENMONO_VISION_ENABLED=0` to prevent the CLI from sending images without unloading the projector from the server.

---

## VS Code & Cursor extension

The same agent that runs in the terminal is available as a sidebar chat panel in VS Code (1.85+) and Cursor.

### Install the extension

```bash
code --install-extension StartupHakk.openmono-agent
```

Or install a `.vsix` release directly:

```bash
code --install-extension openmono-agent-0.6.1.vsix
```

The extension is also on the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=StartupHakk.openmono-agent) (search `StartupHakk.openmono-agent`).

> [!IMPORTANT]
> The extension is the UI and workspace tool executor — it does **not** run the model. You still need the local OpenMono agent running on your machine (or a dual-box inference server). The extension connects to it over HTTP/SSE using ACP on port `7475`.

### Start the agent in ACP mode

Instead of `openmono agent` (which opens the TUI), use `--acp-only` to expose an ACP server that the extension connects to:

```bash
cd your-project/
openmono agent --acp-only --acp-port 7475
```

The extension auto-connects at `localhost:7475`. Open the **OpenMono Agent** panel in the VS Code sidebar, then use the commands:

| Command | What it does |
|---------|-------------|
| Start Agent for Workspace | Connects to the running agent |
| Stop Agent | Disconnects |
| Clear Session | Clears the current chat session |
| Resume Session | Resumes a previous session |
| Select Workspace Folder | Changes the workspace root |

> [!TIP]
> The extension can also auto-spawn the agent in a Docker container — this is optional. If you prefer to control the agent process yourself, always start with `--acp-only`.

---

## Web search & scraping

Web search and scraping are opt-in services that run alongside the inference server. Install them with:

```bash
openmono setup search    # SearXNG + Caddy gateway
openmono setup scraper   # Scrapling + Camoufox + gateway
openmono setup gateway   # Caddy gateway only (if you already have searxng/scrapling)
```

Once installed, the `WebSearch` and `WebFetch` tools route through the local gateway automatically — no config needed. Enable flags are written to `docker/.env`:

| Variable | Effect |
|----------|--------|
| `WEB_SEARCH_ENABLED=true` | Routes `WebSearch` through SearXNG |
| `WEB_SCRAPE_ENABLED=true` | Routes `WebFetch` through Scrapling |

Both fall back gracefully (DuckDuckGo / direct fetch) when the gateway is unavailable.

→ [Architecture: web services](ARCHITECTURE.md#inference-side-web-services-caddy-gateway)

---

## Slash commands

| Command | What it does |
|---------|-------------|
| `/help` | List all commands and keyboard shortcuts |
| `/think` | Toggle step-by-step reasoning mode |
| `/plan` | Restrict agent to read-only tools for safe exploration |
| `/model <name>` | Switch model mid-session |
| `/compact [focus]` | Summarize history to free up context |
| `/checkpoint` | Save a named checkpoint in the conversation |
| `/undo [n]` | Revert the last n file changes |
| `/resume [id]` | Resume a previous session |
| `/export [format] [path]` | Export as `markdown`, `json`, or `html` |
| `/status` | Turn count, token usage, model, working directory |
| `/stats` | Token and tool call statistics |
| `/init` | Generate an `OPENMONO.md` for the current project |
| `/clear` | Clear context and start fresh |
| `/debug` | Toggle verbose debug output |
| `/retry` | Resend the last message |
| `/quit` | Exit OpenMono |

---

## Keyboard shortcuts

| Shortcut | Action |
|----------|--------|
| <kbd>Ctrl</kbd>+<kbd>C</kbd> | Cancel active turn · double-tap to exit |
| <kbd>Ctrl</kbd>+<kbd>U</kbd> | Clear input line |
| <kbd>Ctrl</kbd>+<kbd>W</kbd> | Delete last word |
| <kbd>Ctrl</kbd>+<kbd>P</kbd> | Open command picker |
| <kbd>Tab</kbd> | Autocomplete command or file path |
| <kbd>Esc</kbd> | Cancel active request · dismiss suggestions |
| <kbd>F1</kbd> | Help overlay |
| <kbd>↑</kbd> / <kbd>↓</kbd> | Navigate input history |
| <kbd>PageUp</kbd> / <kbd>PageDown</kbd> | Scroll conversation |

Shortcuts can be customised in `~/.openmono/tui.json` (user-wide) or `.openmono/tui.json` (per project).

---

## Configuration

Settings live in `~/.openmono/settings.json` (user-wide) or `.openmono/settings.json` (per project):

```bash
openmono config set llm.endpoint http://localhost:7474
openmono config set llm.model qwen3.6-27b
openmono config get llm.endpoint
```

→ [Full configuration reference](CONFIG.md)
