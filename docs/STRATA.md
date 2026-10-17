# Strata backend (Linux) - Qwen3.8-Flash-Next

On Linux, OpenMono can run inference through **[Strata](https://github.com/Niko1221/Strata)**
instead of the bundled llama.cpp Docker path. Strata serves the
**Qwen3.8-Flash-Next** model through an OpenAI-compatible API at
`http://127.0.0.1:8080/v1` (plus `/v1/messages` for Anthropic-style clients and
`/v1/responses`). The .NET agent talks to it exactly like any other
OpenAI-compatible provider. Any API key and any model name work.

OpenMono does **not** vendor Strata: `scripts/strata.sh` clones upstream Strata
(default `~/strata`) and drives its documented Linux flow (`./setup.sh`). Model
weights (~70 GB, resumable download) live in Strata's own data dir.

The generic engine that should outgrow Flash-Next is **Mosaic**. See
[ENGINE-DESIGN.md](ENGINE-DESIGN.md). Mosaic is not the installer default yet.

> **Credits / license.** Strata is MIT-licensed by Niko1221 and contributors;
> the model is [Qwen3.8-Flash-Next](https://huggingface.co/Qwen/Qwen3.8-Flash-Next)
> by the Qwen team (compressed by ISTA-DASLab, UkisAI, Unsloth), and Strata
> builds on [llama.cpp / ggml](https://github.com/ggml-org/llama.cpp). See
> Strata's `LICENSE` and `docs/HOW_IT_WORKS.md` after cloning. Nothing below
> changes Strata's own licensing.

## Requirements (what OpenMono offers)

These are the gates in `scripts/install.sh` (`CAN_STRATA`), not the full
upstream Strata matrix.

| | OpenMono installer | Upstream Strata (for reference) |
|---|---|---|
| **GPU** | **NVIDIA** RTX 20/30/40/50, **12 GB+ VRAM**. CUDA 13 is auto-installed. | Also lists AMD RX 7900/7800/7700/9060/9070 (XT), AI PRO R9700, RX 6800/6900. HIP is a 10-20 min source build. OpenMono does not offer that path yet. |
| **RAM to offer Flash-Next** | **48 GB-class**: `/proc/meminfo` `MemTotal` **>= 44 GiB**. 48 GB machines report ~46-47 GiB after firmware reservations; 32 GB boxes report ~31. | 32 GB+ (64 GB runs every size; 32 GB takes the `coder` family, often in low-RAM mode). |
| **RAM-resident PLE** | Default when experts + the ~27 GiB n-gram table + 8 GiB OS fit. On 64 GB that is **Coder + `--ple-io ram`**. | `--ple-io direct` (SSD) by default; `--ple-io ram` is opt-in / rotational disks. |
| **Disk** | ~80 GB free, SSD preferred | same |
| **OS** | Linux (Ubuntu 22.04/24.04 fully automatic) with a current NVIDIA driver | Windows or Linux |

OpenMono does **not** offer Strata on 32 GB boxes: upstream's low-RAM mode maps
experts from disk while decoding, which this project does not allow (no model
weights from NVMe in the hot path). It does **not** offer AMD in the installer
menu because the CUDA 13 auto-install path is NVIDIA-only.

`./setup.sh --check` (via `scripts/strata.sh check`) still runs Strata's own
probe after a clone.

### RAM-resident plan (`scripts/strata.sh`)

Spencer's constraint: load weights once at startup; do not read them from SSD
per token. Flash-Next's shard 2 is a ~26.82 GiB n-gram table. `--ple-io ram`
loads it once. That only fits next to the expert arena on some boxes:

| MemTotal | Plan |
|---|---|
| 93+ GiB (96 GB class) | `--family qwen --model IQ3_S --ple-io ram` |
| 73-92 GiB | `--family qwen --model IQ2_XS --ple-io ram` |
| 60-72 GiB (64 GB class) | `--family coder --model IQ1_M --ple-io ram` |
| below 60 GiB (48 GB class, still offered) | do not force `--ple-io ram`; Strata picks a size; PLE stays on SSD |

Overrides: `STRATA_FAMILY`, `STRATA_MODEL`, `STRATA_PLE_IO`. Setting
`STRATA_FAMILY=qwen STRATA_MODEL=IQ2_XS` on 64 GB keeps that quant and does
**not** add `--ple-io ram` (36+29+8 GiB does not fit).

## Single-box install (agent + Strata on one machine)

```bash
openmono setup --model=flash-next    # or OPENMONO_INFERENCE_BACKEND=strata
```

Non-interactive `openmono setup` without that flag still defaults to Qwen on
llama.cpp. Interactive setup shows Flash-Next when `CAN_STRATA=1`.

What that does (roles `full` / `inference`):

1. `scripts/strata.sh install` - clones Strata, applies the RAM-resident plan,
   runs `./setup.sh --no-start` (interactive unless stdin isn't a TTY, in which
   case `--yes` plus the plan). Re-run to resume an interrupted download.
2. Skips the legacy GGUF download, `llama-server` image build, and override.
   There is no `llama-server` container on this backend.
3. `scripts/strata.sh start` - launches Strata's `run-<model>.sh` detached and
   waits for `/health`. First boot loads 35-55 GB into RAM; the PC may feel
   slow for 1-3 minutes. That is normal. Don't kill it.
4. Writes `INFERENCE_BACKEND=strata` + `STRATA_PORT=8080` to `docker/.env` and
   `~/.openmono/settings.json` gets `llm.endpoint = http://localhost:8080/v1`.

Daily commands are unchanged. They detect the backend from `docker/.env`:

| Command | Strata behavior |
|---|---|
| `openmono start` / `stop` / `restart` | start/stop/restart the host Strata server |
| `openmono logs` | tail Strata's server log (`scripts/strata.sh logs --tail N`) |
| `openmono status` | Strata `/health` + `/v1/models`, gateway containers, GPU |
| `openmono agent` | auto-starts Strata when the local endpoint is down |

Manual equivalents: `bash scripts/strata.sh {check,install,start,stop,status,logs,update}`.
`scripts/strata.sh update` pulls upstream Strata + runs `./update.sh` (no start).

### Switching backends

```bash
OPENMONO_INFERENCE_BACKEND=llama openmono setup   # llama.cpp Docker path
OPENMONO_INFERENCE_BACKEND=strata openmono setup  # Strata (opt-in on Linux)
```

Extra flags after `strata.sh install` are forwarded to Strata's `setup.sh`
verbatim, e.g. `bash scripts/strata.sh install --yes --family coder`. Model,
context, vision, port, and data dir can also be preset:

```bash
STRATA_FAMILY=qwen STRATA_MODEL=IQ2_XS STRATA_VISION=yes STRATA_PORT=8080 \
  bash scripts/strata.sh install
```

> **Vision (image input).** Unless `STRATA_VISION` is preset (or `--vision` is
> passed through), the installer **prompts** for it: `yes` (GPU projector),
> `cpu` (CPU-encoded, saves VRAM), or `no`. Non-interactive runs default to
> `no`. Whatever you choose is recorded in `~/.openmono/strata.env` and mirrored
> to `inference.vision_enabled` in `~/.openmono/settings.json`, so the agent
> knows to accept image parts. Change it later with
> `STRATA_VISION=yes bash scripts/strata.sh install` (the ~70 GB base weights are
> already cached; only the ~900 MB projector is fetched), or flip just the
> agent-side gate with `openmono config set inference.vision_enabled true`.

## Agent configuration

Single-box (`config-examples/strata-single-box.settings.json`):

```bash
openmono config set llm.endpoint http://localhost:8080/v1
openmono config set llm.model Qwen3.8-Flash-Next
# api_key: anything (or the --api-key from Strata setup, if one was set)
```

Thinking levels: the agent's `off / low / medium / high` knob maps to Strata's
`reasoning_effort` directly (the client's `xhigh` is clamped to `high`, which
is Strata's documented max). `off` is fastest; `high` is best for hard
questions. The legacy `chat_template_kwargs` payload is still sent alongside
for llama.cpp compatibility. Each backend reads what it understands.

## Dual-box (agent laptop + Strata GPU box via relay)

The **frpc relay path is unchanged**. frpc still tunnels one local port to
`relay.openmonoagent.ai`, and the Caddy gateway still fans that port out to
inference + search + scrape. Only the tunnel *target* is backend-aware:

**Inference box (Strata):**

```bash
openmono setup --inference --model=flash-next   # installs Strata, no agent
openmono start               # start Strata
# Optional but recommended: front Strata with the Caddy gateway so the single
# relay port also serves search/scrape:
openmono setup gateway
openmono tunnel setup        # tunnels the gateway, else Strata's port directly
```

`scripts/setup-tunnel-inference.sh` picks the local port automatically:
gateway port when the gateway is installed, otherwise `STRATA_PORT` (8080).
When tunneling Strata *directly* (no gateway), the agent-box `llm.api_key` must
be the Strata server's own `--api-key` (set at Strata setup time). The printed
`LLAMA_API_KEY` guards the Caddy gateway path. `openmono tunnel rotate-key`
rotates the gateway key and restarts Caddy on this backend.

**Agent box:**

```bash
openmono setup --agent
openmono config set llm.endpoint http://<relay-host>:<remotePort>
openmono config set llm.api_key  <key-from-inference-box>
openmono agent
```

The relayed endpoint needs no `/v1` suffix conventions beyond what the
inference box exposes: gateway target -> same relay URL serves `/v1/*`,
`/search`, `/scrape`; direct-Strata target -> relay URL is Strata itself, so set
`llm.endpoint` to `http://<relay-host>:<remotePort>/v1`.

## Agent-host (bare-metal sub-agent)

Unaffected. The host bridge (`OpenMono.HostBridge`) spawns the same `agent`
container; its endpoint mapping now understands Strata-style endpoints:
a loopback endpoint carrying the `/v1` base path (e.g.
`http://localhost:8080/v1`) is rewritten to `host.docker.internal` with port
and path preserved, while plain `:7474`-style endpoints keep the legacy
`llama-server:7474` mapping. No bridge config changes needed.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `openmono start` says Strata isn't installed | `bash scripts/strata.sh install` (see log + `docs/STRATA.md` requirements) |
| Port 8080 already in use | Strata is already running. Check `curl 127.0.0.1:8080/health`, or `STRATA_PORT=8081` at install |
| Very slow / "engine stopped unexpectedly" | Not enough free RAM: close programs or install a smaller size (`strata.sh install --yes --family qwen --model Q2_0`) |
| Disk light blinks on every token | PLE is still `--ple-io direct`. On 64 GB reinstall so the plan uses Coder + `ram`, or set `STRATA_PLE_IO=ram` only if experts+PLE fit |
| Download/install interrupted | Re-run the same command. Strata resumes |
| `prompt exceeds the context` | Re-run setup with a bigger context (`--setup --context N` in Strata terms) |
| Agent connects but auth fails through the relay | Direct-Strata tunnel needs Strata's own `--api-key`; gateway tunnel needs `LLAMA_API_KEY` |
| `MapEndpoint` sends the container to the wrong host | Only loopback `/v1*` endpoints map to `host.docker.internal`; relay URLs pass through untouched |

Strata engine detail lives in `~/strata/strata-*.log`; the wrapper log is
`~/.openmono/strata-server.log`.

## What still needs a real GPU box to verify

- [ ] End-to-end `openmono setup --model=flash-next` on a 12 GB+ VRAM Linux box, including the ~70 GB model download.
- [ ] 64 GB NVIDIA: installer picks Coder + `--ple-io ram`; `openmono agent` chat + tool calls (SSE, `reasoning_effort`, `/v1/models`).
- [ ] 96 GB NVIDIA: IQ3_S + `--ple-io ram`.
- [ ] 48 GB NVIDIA: Strata still installs; PLE is not forced into RAM; no installer break.
- [ ] Dual-box: frpc tunnel -> Strata (direct + via Caddy gateway with `docker-compose.strata.yml`), agent box over the relay.
- [ ] `openmono setup gateway/search/scraper` layering on top of a live Strata backend.
- [ ] AMD GPU path: still not offered by OpenMono; upstream Strata HIP compile (~10-20 min) if we wire it later.
