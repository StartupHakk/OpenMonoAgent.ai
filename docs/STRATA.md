# Strata backend (Linux) — Qwen3.8-Flash-Next

On Linux, OpenMono runs inference through **[Strata](https://github.com/Niko1221/Strata)**
instead of the legacy bundled llama.cpp Docker path. Strata serves the
**Qwen3.8-Flash-Next** model through an OpenAI-compatible API at
`http://127.0.0.1:7474/v1` (plus `/v1/messages` for Anthropic-style clients and
`/v1/responses`). The .NET agent talks to it exactly like any other
OpenAI-compatible provider — any API key and any model name work.

OpenMono does **not** vendor Strata: `scripts/strata.sh` clones upstream Strata
(default `~/strata`) and drives its documented Linux flow (`./setup.sh`). Model
weights (~70 GB, resumable download) live in Strata's own data dir.

> **Credits / license.** Strata is MIT-licensed by Niko1221 and contributors;
> the model is [Qwen3.8-Flash-Next](https://huggingface.co/Qwen/Qwen3.8-Flash-Next)
> by the Qwen team (compressed by ISTA-DASLab, UkisAI, Unsloth), and Strata
> builds on [llama.cpp / ggml](https://github.com/ggml-org/llama.cpp). See
> Strata's `LICENSE` and `docs/HOW_IT_WORKS.md` after cloning. Nothing below
> changes Strata's own licensing.

## Requirements

Strata runs on the host, not in Docker. OpenMono installs it on this box:

| | |
|---|---|
| **GPU** | NVIDIA RTX 20/30/40/50 or AMD RX 7900/7800/7700/9060/9070 (XT), AI PRO R9700, RX 6800/6900. **12 GB+ VRAM** |
| **RAM** | 48 GB-class box (about 44 GiB or more of system RAM). Weights are about 70 GB and stream from disk. |
| **Disk** | About 80 GB free. An SSD is preferred. |
| **OS** | Linux. Ubuntu and Ubuntu derivatives (tested on Ubuntu 24.04 LTS), with a current NVIDIA driver. CUDA 13 is installed during setup. |

OpenMono offers Strata on this box. Setup auto-picks it when the NVIDIA card has 24 GB or more of VRAM. On a smaller NVIDIA card with the same RAM, it is in the `openmono setup --options` menu.

## Single-box install (agent + Strata on one machine)

```bash
openmono setup            # picks Strata on a qualifying NVIDIA box
```

What that does (roles `full` / `inference`):

1. `scripts/strata.sh install` — clones Strata, runs `./setup.sh --no-start`
   (`--yes` is passed unless you used `--options` on a TTY, so the default route
   takes Strata's recommended size for the box instead of asking). Re-run to
   resume an interrupted download.
2. Skips the legacy GGUF download, `llama-server` image build, and override —
   there is no `llama-server` container on this backend.
3. `scripts/strata.sh start` — launches Strata's `run-<model>.sh` detached and
   waits for `/health`. First boot loads 35–55 GB into RAM; the PC may feel
   slow for 1–3 minutes. That is normal — don't kill it.
4. Writes `INFERENCE_BACKEND=strata` + `INFERENCE_PORT=7474` to `docker/.env` and
   `~/.openmono/settings.json` gets `llm.endpoint = http://localhost:7474`.

Daily commands are unchanged — they detect the backend from `docker/.env`:

| Command | Strata behavior |
|---|---|
| `openmono start` / `stop` / `restart` | start/stop/restart the host Strata server |
| `openmono logs` | tail Strata's server log (`scripts/strata.sh logs --tail N`) |
| `openmono status` | Strata `/health` + `/v1/models`, gateway containers, GPU |
| `openmono agent` | auto-starts Strata when the local endpoint is down |

Manual equivalents: `bash scripts/strata.sh {check,install,start,stop,status,logs,update}`.
`scripts/strata.sh update` pulls upstream Strata + runs `./update.sh` (no start).

### Single source of truth: `~/.openmono/settings.json`

Port, API key, parallel slots, context, and vision are runtime settings. Edit
them once in `~/.openmono/settings.json` (or with `openmono config set`), then
restart; `openmono start` / `restart` run `scripts/apply_settings.sh --apply`,
which materializes only those shared knobs into the files each backend reads:

- `docker/.env` — `INFERENCE_PORT`, `INFERENCE_PARALLEL`, `LLAMA_API_KEY`,
  Caddy's `LLAMA_UPSTREAM`
- `~/.openmono/strata.env` — `INFERENCE_PORT`, `INFERENCE_PARALLEL`, context
- the generated `~/strata/strata-*.json` + `run-*.sh` — port, parallel, API key

Those files are **applied state**; don't hand-edit them. Install details
(`STRATA_DIR`, family, model, data dir) stay in `strata.env` and are never
copied into settings.json.

```bash
openmono stop                                  # stop with the old applied state
openmono config set inference.port 7474
openmono config set inference.parallel_slots 2
openmono config set llm.api_key "$(openssl rand -hex 24)"
openmono start                                 # apply + start
```

### Bind address and API key

Strata is bound to `0.0.0.0`, not `127.0.0.1`: on Linux the agent runs in a
container, and a loopback bind gives it `Connection refused`. Every OpenMono
inference server runs with a bearer key — llama.cpp takes `--api-key`, Strata
requires one and refuses to start without it on a non-loopback bind — so the
key is not optional on any backend. `apply_settings.sh` creates one into
`llm.api_key` when it is missing, and the install and every `openmono start`
run that apply — so an install made before this existed repairs itself on the
next `openmono restart`. An existing key is never rotated.

Only `/health` and `/v1/health` are public. Everything else needs the bearer:
`/v1/chat/completions`, `/props`, `/v1/models`, and on Strata also `/slots`
and `/status`.

`openmono status` shows desired vs applied values. To check without changing
anything: `bash scripts/apply_settings.sh --check` (exit 2 = drift).
Parallel slots are asked during install only **with `--options`**; otherwise the
default is 1 (`openmono config set inference.parallel N` changes it later).

Strata's slots are **not** a share of the context. `"parallel": N` is the
engine's `--batch`/`--slots`: the configured context stays whole and **every
slot keeps its own full-context K/V session**. What a slot costs is the VRAM it
takes away from the model's expert cache — about 0.56 GiB at a 32K context with
8-bit KV, more at longer contexts unless KV streaming (`--kv-resident`) keeps
only the attention window resident and puts the rest in pinned RAM. So raising
slots does not shrink anyone's context; it trades per-request speed for
concurrency. Strata serves **2..8 slots** (8 per group with a layer split) and
never refuses a larger number — it logs the count it can actually run
(`batch_slots=N`, also visible as `concurrency.serving` in `GET /v1/status`).
On a lone 12 or 16 GB card upstream recommends staying at 1: a batch costs about
10-25% speed per request there, because different conversations route to
different experts and the expert cache shrinks.

### Switching backends

```bash
OPENMONO_INFERENCE_BACKEND=llama openmono setup   # legacy llama.cpp Docker path
OPENMONO_INFERENCE_BACKEND=strata openmono setup  # Strata (default on Linux)
```

Extra flags after `strata.sh install` are forwarded to Strata's `setup.sh`
verbatim, e.g. `bash scripts/strata.sh install --yes --family coder`. Model,
context, vision, port, and data dir can also be preset:

```bash
STRATA_FAMILY=qwen STRATA_MODEL=IQ2_XS STRATA_VISION=yes INFERENCE_PORT=7474 \
  bash scripts/strata.sh install
```

> **Vision (image input).** Vision is **on by default**. The installer only
> **prompts** — `yes` (GPU projector), `cpu` (CPU-encoded, saves VRAM), or `no` —
> when you ran setup with `--options` on a TTY. Otherwise, and always in
> non-interactive runs, it takes `yes` and logs the choice; preset
> `STRATA_VISION=no` to opt out without `--options`. Whatever you choose is recorded
> in `~/.openmono/strata.env` and mirrored
> to `inference.vision_enabled` in `~/.openmono/settings.json`, so the agent
> knows to accept image parts. Change it later with
> `STRATA_VISION=yes bash scripts/strata.sh install` (the ~70 GB base weights are
> already cached — only the ~900 MB projector is fetched), or flip just the
> agent-side gate with `openmono config set inference.vision_enabled true`.

## Agent configuration

Single-box (`config-examples/strata-single-box.settings.json`):

```bash
openmono config set llm.endpoint http://localhost:7474
openmono config set llm.model Qwen3.8-Flash-Next
# api_key: the shared key. OpenMono creates it in settings.json (llm.api_key)
# because Strata is bound to 0.0.0.0 and requires one.
```

Thinking levels: the agent's `off / low / medium / high` knob maps to Strata's
`reasoning_effort` directly (the client's `xhigh` is clamped to `high`, which
is Strata's documented max for Qwen3.8-Flash-Next). `off` is fastest; `high` is best for hard
questions. The legacy `chat_template_kwargs` payload is still sent alongside
for llama.cpp compatibility — each backend reads what it understands.

The RTX 3060 12GB profile is a different Strata: the **0.2.22 terminal**
(`strata launch`) serving Inkling 2 Mini SQ-10, not this Qwen clone. Inkling's
template rejects `high`. That profile sets `llm.reasoning_effort` to `low`, and
an Inkling model name is not sent `high`. See [SETUP.md](SETUP.md#rtx-3060-12gb).
Other GPUs still use the clone described above.

## Dual-box (agent laptop + Strata GPU box via relay)

The **frpc relay path is unchanged** — frpc still tunnels one local port to
`relay.openmonoagent.ai`, and the Caddy gateway still fans that port out to
inference + search + scrape. Only the tunnel *target* is backend-aware:

**Inference box (Strata):**

```bash
openmono setup --inference   # installs Strata, no agent
openmono start               # start Strata
# Optional but recommended: front Strata with the Caddy gateway so the single
# relay port also serves search/scrape:
openmono setup gateway
openmono tunnel setup        # tunnels the gateway, else Strata's port directly
```

`scripts/setup-tunnel-inference.sh` picks the local port automatically:
gateway port when the gateway is installed, otherwise `INFERENCE_PORT` (7474).
When tunneling Strata *directly* (no gateway), the agent-box `llm.api_key` must
be the Strata server's own `--api-key` (set at Strata setup time) — the printed
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
inference box exposes: gateway target → same relay URL serves `/v1/*`,
`/search`, `/scrape`; direct-Strata target → relay URL is Strata itself, so set
`llm.endpoint` to `http://<relay-host>:<remotePort>/v1`.

## Agent-host (bare-metal sub-agent)

Unaffected. The host bridge (`OpenMono.HostBridge`) spawns the same `agent`
container; its endpoint mapping now understands Strata-style endpoints:
a loopback endpoint carrying the `/v1` base path (e.g.
`http://localhost:7474/v1`) is rewritten to `host.docker.internal` with port
and path preserved, while plain `:7474`-style endpoints keep the legacy
`llama-server:7474` mapping. No bridge config changes needed.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `openmono start` says Strata isn't installed | `bash scripts/strata.sh install` (see log + `docs/STRATA.md` requirements) |
| Port 7474 already in use | Strata is already running — check `curl 127.0.0.1:7474/health`, or choose another port with `openmono config set inference.port 7475` then `openmono restart` |
| Very slow / "engine stopped unexpectedly" | Not enough free RAM: close programs or install a smaller size (`strata.sh install --yes --family qwen --model Q2_0`) |
| Download/install interrupted | Re-run the same command — Strata resumes |
| `prompt exceeds the context` | Re-run setup with a bigger context (`bash scripts/strata.sh install --yes --context N`) |
| Agent connects but auth fails through the relay | Direct-Strata tunnel needs Strata's own `--api-key`; gateway tunnel needs `LLAMA_API_KEY` |
| Server starts then dies; `openmono logs` ends with "a non-loopback bind requires an inference API key" | Strata refused the `0.0.0.0` bind because no key was set. `openmono restart` repairs it; by hand: `openmono config set llm.api_key "$(openssl rand -hex 24)" && openmono restart` |
| `MapEndpoint` sends the container to the wrong host | Only loopback `/v1*` endpoints map to `host.docker.internal`; relay URLs pass through untouched |

Strata engine detail lives in `~/strata/strata-*.log`; the wrapper log is
`~/.openmono/strata-server.log`.

## What still needs a real GPU box to verify

- [ ] End-to-end `openmono setup` (Strata path) on a qualifying NVIDIA box (48 GB-class RAM), including the ~70 GB model download.
- [ ] `openmono agent` chat + tool calls against Strata (SSE streaming, `reasoning_effort` mapping, `/v1/models` catalog).
- [ ] Dual-box: frpc tunnel → Strata (direct + via Caddy gateway with `docker-compose.strata.yml`), agent box over the relay.
- [ ] `openmono setup gateway/search/scraper` layering on top of a live Strata backend.
- [ ] AMD GPU path (Strata compiles its HIP engine on first install, about 10 to 20 min).
