# Models

---

## Model selection

Setup reads your hardware and picks the **best** model the box can run — it does not ask.
Pass `--options` to choose the model, vision and parallel slots yourself; `--model=<name>`
always wins over the hardware gate. The gate is ordered, **first match wins**, and the
VRAM check runs before the system-RAM check — so the same card lands on different rows
depending on how much RAM sits behind it.

### Switching models at runtime

- `/model` lists the models advertised by the active backend and marks the current one.
- `/model refresh` re-queries the backend's `/v1/models` endpoint.
- `/model <name|index>` changes the model name sent to the active provider; it takes
  effect on the next turn.

This does not change the provider and does not start a second local model. A llama.cpp
server normally serves one model per process, so in practice the list contains that
model; backends such as Ollama may advertise several.

For multiple sampling configurations, define `model_presets` in `settings.json` and
activate one with `"active": true` or `OPENMONO_MODEL_PRESET` at startup. There is no
runtime preset-switch command yet.

### Linux

| # | VRAM | System RAM | Model | Engine | Context / KV | Vision |
|---|------|------------|-------|--------|--------------|--------|
| 1 | NVIDIA **24 GB+** | **44 GB+** (48 GB-class) | Qwen3.8-Flash-Next | Strata (CUDA 13) | sized by Strata | on |
| 2 | NVIDIA **24 GB+** | **under 44 GB** | Qwen3.8-27B-UD-Q4_K_M | llama.cpp (CUDA) | 196608 / q8_0 | on |
| 3 | NVIDIA **16 GB** | any | Qwen3.8-27B-UD-IQ3_XXS | llama.cpp (CUDA) | 180224 / q4_0 | on |
| 4 | NVIDIA **12 GB** | any | Ternary-Bonsai-2-27B **PQ2_0** | Prism (CUDA) | 98304 / q4_0 | on |
| 5 | AMD **iGPU** (Ryzen 9 7940HS / Radeon 780M) | **32 GB+** | Ternary-Bonsai-2-27B **PQ2_0** | Prism (Vulkan) | 196608 / q8_0 | on |
| 6 | **none** (CPU) | 24 GB+ | Qwen3.6-35B-A3B-UD-Q4_K_XL | llama.cpp (CPU) | 196608 / q8_0 | on |

The **Context / KV** column lists the tier preset, which is what setup writes when
vision is turned off. As shipped — with vision on — the encoder's ~1–2 GB of VRAM is
paid for out of the KV budget, so the README tables show the as-installed numbers:
196608 → 172032 (168k) on the 24 GB+ and iGPU rows, and 180224 → 98304 (96k) on the
16 GB row. The 12 GB Bonsai row is already sized for the encoder, so vision costs it
nothing.

Row 1 shows **auto** rather than a number because Strata owns that value: OpenMono
leaves `STRATA_CONTEXT` unset unless you set it, and Strata's installer sizes the
context to the box (its guided setup asks, with a recommended answer per card).
Whatever it picked is the running truth — `openmono status` prints it as `ctx=` from
the server's `max_context`. We do not print a number we did not choose.

Rows 1 and 2 are the **same card** split by system RAM: the Flash-Next weights are
~70 GB and stream from disk, so the card alone is not enough — the box needs to be
48 GB-class. The gate tests 44 GB rather than 48 because a 48 GB box reports 46–47 GB
once firmware and kernel reservations are subtracted.

Row 3 stays on Qwen3.8-27B: IQ3_XXS (~12 GB) fits a 16 GB card with room for the KV
cache, so there is no reason to fall back to a ternary quant. Row 4 is where Qwen has
no 27B option left — a 12 GB card only fits the 9B model — which is exactly where
PQ2_0 (7.21 GB) buys a real 27B instead. The bucket spans 12–15 GB in the detector but
is labelled 12 GB because NVIDIA ships no 13–15 GB card. Row 5 uses PQ2_0 rather than
the smaller PTQ1_0 because the iGPU is UMA: it addresses system RAM, so the extra
~1.3 GB of weights comes out of the same pool the model already uses — which is why the
row is documented at 32 GB+ of RAM. Below 32 GB the pick is still Bonsai, because the
CPU fallback needs ~20 GB and would not load; setup warns instead of downgrading.
NVIDIA cards below 12 GB are not a supported inference target; those boxes stay on the
Qwen row that matches their tier.

Bonsai is still *selectable* on the Qwen rows — `--model=bonsai-pq2` works on any
NVIDIA card from 8 GB up and takes that tier's context and KV settings. Auto-select
just prefers a 27B Qwen wherever one fits, which is why 16 GB and 24 GB+ cards no
longer land on a ternary quant.

### macOS (Apple Silicon, unified memory)

| # | VRAM | System RAM | Model | Engine | Context |
|---|------|------------|-------|--------|---------|
| 1 | unified | **48 GB+** | Qwen3.6-35B-A3B-UD-Q4_K_XL | llama.cpp (Metal) | 196608 |
| 2 | unified | **24–47 GB** | Ternary-Bonsai-2-27B **PQ2_0** | Prism (Metal) | 65536 |
| 3 | unified | **16–23 GB** | Ternary-Bonsai-2-27B **PTQ1_0** | Prism (Metal) | 16384 |

Apple Silicon has no discrete VRAM — weights and KV cache share unified memory — so
the Mac table has one memory column where the Linux table has two. The context column
is discrete per memory size, not a range: 48 GB+ → 196608 preset (172032 as shipped
with vision), 24–47 GB → 65536, 16–23 GB → 16384. Strata is Linux + NVIDIA only, so
high-memory Macs keep the 35B-A3B MoE. In the 24–47 GB range Bonsai wins: at ~7.2 GB
of weights it leaves the headroom unified memory needs, and it beats the 9B dense
model on every agentic-coding benchmark we track. 16–23 GB takes PTQ1_0 (~5.9 GB)
because PQ2_0 would swap-thrash. Every Mac row ships with vision on.

> [!NOTE]
> The 16–23 GB row ships 16384, not the 32768 an uncapped box would allow. Metal will
> not let a process use more than `recommendedMaxWorkingSetSize` (~75% of physical, so
> ~12 GB on a 16 GB box), and the native server always runs q8_0 KV (`--cache-type-k
> q8_0 --cache-type-v q8_0`): PTQ1_0 5.95 GB + mmproj 0.63 GB + 3.45 GB of KV at 32768
> = 10.03 GB, which leaves macOS only ~2 GB of the ~12 GB window. At 16384 the same
> arithmetic gives 8.30 GB and leaves room to breathe — the same 16k the Qwen Metal
> tier already uses at 16 GB. The Linux tiers get more context at this size because
> they can drop KV to q4_0; the macOS tiers carry no `kv` field at all.
>
> The ~8–10 tok/s above was measured on a 16 GB M4 Mac mini **at 32768**, so 32k does
> run there — the margin is tighter than comfortable, not fatal, and 16384 is the safer
> preset rather than a fix for a failure seen in practice. But it is also probably not
> the speed a 16k install gets: the M4's ~122 GB/s of memory bandwidth puts the decode
> floor for 5.95 GB of weights near 20 tok/s, so 8–10 is roughly half the hardware
> ceiling — the signature of memory pressure at 32k, not a bandwidth limit. 16384
> should recover some of that; re-measure before quoting a 16k figure.

> [!NOTE]
> If the Prism image can't be pulled (Linux) or built (macOS), setup falls back to stock
> Qwen rather than failing — so an auto-selected Bonsai box still ends up running. Pick
> Qwen up front with `--model=qwen`.

The two llama.cpp models, for reference:

| | GPU | CPU |
|--|-----|-----|
| **Model** | Qwen3.8-27B-UD-Q4_K_M | Qwen3.6-35B-A3B-UD-Q4_K_XL |
| **Type** | Dense | MoE (Mixture of Experts) |
| **Disk** | ~15.5 GB | ~17.6 GB |
| **VRAM / RAM** | ~23 GB (model + KV cache) | ~20 GB |
| **Context** | 192K tokens | 192K tokens |
| **When** | 24 GB+ VRAM on a sub-48 GB box, or `--model=qwen` | No GPU, or no nvidia runtime |

### Why the llama.cpp models differ between GPU and CPU


Token generation speed is memory-bandwidth bound — the bottleneck is how fast you can read model weights per token, not raw compute.

**GPU (dense 27B):** VRAM bandwidth is ~900 GB/s. Reading 15.5 GB takes ~17 ms → ~60 tok/s. Token generation speed is bandwidth-bound on model size, not on benchmark quality, so this holds for Qwen3.8-27B at the same disk/VRAM footprint as Qwen3.6-27B. Dense models fully utilise GPU parallel execution. Putting the MoE model on a GPU wastes VRAM and doesn't help — sparse routing doesn't parallelise as well as dense matmuls on CUDA.

**CPU (MoE 35B-A3B):** DDR5 RAM bandwidth is ~89 GB/s — 10× slower than VRAM. A dense 27B would read 15.5 GB per token → ~174 ms → ~6 tok/s (unusable). The MoE model activates only ~3B parameters per token, reading ~1.7 GB instead → ~19 ms → **~20 tok/s**. You get near-GPU quality from a 35B-equivalent model at CPU speeds that are actually usable.

Short version: **dense models are fast on GPU, MoE models are fast on CPU.**

### Apple Silicon (Metal)

On macOS the model runs natively against the Metal GPU and unified memory — there's no separate "VRAM vs RAM" split. Bonsai is the default from 16 GB up (table above); the 35B-A3B MoE stays on 48 GB+ Macs.

The 35B-A3B MoE activates only ~3B parameters per token, so on a high-bandwidth Apple Silicon chip it hits the same usable-to-fast range as a Linux GPU while keeping full accuracy.

> [!IMPORTANT]
> The 48 GB+ tier (full-accuracy 35B MoE) is the validated reference configuration. Bonsai on 24–47 GB and 16–23 GB installs and runs by default, but it is the newer Metal path — if a Mac of that size misbehaves, `--model=qwen` puts it back on the older Qwen tiers. 16 GB is the hard floor for native inference; below that, use the agent role with a remote inference box.

---

## Why Qwen3.8 (GPU) / Qwen3.6 (CPU)?

Qwen was chosen because it's the best open-weight model family for agentic coding tasks at consumer hardware sizes — not just in raw benchmark numbers, but in how it handles tool use, multi-step reasoning, and real-world code iteration.

**Qwen3.8-27B (dense)** — the GPU model, our strongest yet. Large jump over Qwen3.6-27B on agentic coding benchmarks (upstream numbers, [full model card](https://huggingface.co/Qwen/Qwen3.8-27B)):
- **Terminal Bench 2.1: 73.0** (vs 63.4 on Qwen3.6-27B) — agentic terminal use
- **SWE-bench Pro: 61.7** (vs 53.5) — agentic coding
- **QwenSWEBench: 79.0** (vs 49.3) — software engineering
- **OSWorld-Verified: 84.3** (vs 63.9) — computer use, relevant if vision/mmproj is enabled
- Fits in 24 GB VRAM quantised, runs at ~60 tok/s — same footprint and bandwidth profile as Qwen3.6-27B
- Native reasoning mode (`/think`) carries over from the Qwen3.x line

**Qwen3.6-35B-A3B (MoE)** — the CPU model:
- **EvalPlus: 71.45** aggregate (HumanEval, MBPP, HumanEval+, MBPP+)
- Only ~3B parameters active per token — makes 35B-class quality achievable at CPU speeds
- Same 192K context as the GPU model

Both models are designed for agentic workflows, not just benchmark numbers — the training emphasises tool calling, code iteration, and multi-turn task completion.

> [!NOTE]
> You're not locked to Qwen. Any GGUF model that fits your hardware can be served by llama.cpp and selected with `llm.model` in `settings.json`. Use `/model <name>` to change the model name sent to the active provider; providers themselves are selected at startup.

---

## Hardware benchmarks

### GPU — Qwen3.8-Flash-Next (Strata)

| Hardware | VRAM / RAM | tok/s | Notes |
|----------|-----------|-------|-------|
| 24 GB+ NVIDIA | on a 48 GB-class box | ~80–160 | Strata streams the ~70 GB weights from disk; the spread is drive speed and how much KV is resident |

### GPU — Qwen3.8-27B-Q4_K_M (192K context)

| Hardware | VRAM | tok/s | Notes |
|----------|------|-------|-------|
| RTX 3090 | 24 GB | ~45–70 | Best price/performance used (~$700–800). Same as Qwen3.6-27B — bandwidth-bound on model size, not benchmark quality |
| RTX 4090 | 24 GB | ~70–100 | ~40% faster, ~2× the cost. Same as Qwen3.6-27B — bandwidth-bound on model size, not benchmark quality |

### GPU — Ternary-Bonsai-2-27B PQ2_0 (Prism)

| Hardware | VRAM | tok/s | Notes |
|----------|------|-------|-------|
| NVIDIA 12 GB tier | 12 GB | ~28–32 | PQ2_0 is ~7.2 GB of weights, so it is bandwidth-comfortable on a 12 GB card — the same card on Qwen would have to run the 9B model |

> [!NOTE]
> The **macOS Bonsai** rows in the policy table above have no measured number yet — that
> Metal path has never been benched on a box we logged. It is configured and supported,
> but we do not publish throughput we have not measured. If you run one, Prism's own
> server log gives a number we can add.

### CPU — Qwen3.6-35B-A3B-UD-Q4_K_XL (192K context)

Token generation is memory-bandwidth bound — **RAM channel count matters as much as CPU speed**.

| Hardware | RAM config | Bandwidth | tok/s |
|----------|------------|-----------|-------|
| NUC 13th-gen i5 | DDR5 5600, dual-channel | ~89 GB/s | ~17 |
| NUC 13th-gen i5 | DDR5 5600, single-channel | ~45 GB/s | ~8.5 |
| Ryzen 9 7940HS | DDR5 5600, dual-channel | ~89 GB/s | ~20 |

> Halving RAM channels halves throughput. Always fill both DIMM slots.

### AMD iGPU (Vulkan) — Ternary-Bonsai-2-27B PQ2_0

| Hardware | Memory | tok/s | Notes |
|----------|--------|-------|-------|
| Radeon 780M (Ryzen 9 7940HS) | 32 GB+ DDR5 5600, dual-channel, UMA | ~17–20 | The iGPU addresses system RAM over UMA, so throughput tracks the same DDR5 bandwidth as the CPU row above |

### Apple Silicon (Metal)

Apple Silicon's unified memory has far higher bandwidth than a desktop CPU's DDR5, so the 35B-A3B MoE that runs at ~20 tok/s on a NUC reaches GPU-class speeds on a high-memory Mac.

| Hardware | Unified memory | Model | Context (vision on) | tok/s | Status |
|----------|----------------|-------|---------------------|-------|--------|
| M5 Pro | 64 GB | Qwen3.6-35B-A3B-UD-Q4_K_XL (full) | 192k (168k) | ~45–48 | ✅ Recommended / tested |
| M-series | 24–47 GB | Ternary-Bonsai-2-27B PQ2_0 | 64k | not benched | Default; Metal path newer than 48 GB+ |
| M-series | 16–23 GB | Ternary-Bonsai-2-27B PTQ1_0 | 16k | ~8–10 | ✅ Tested (16 GB Mac mini, measured at 32k); 16k is the shipped preset — sized for Metal's ~75% working-set cap, see below |

> [!IMPORTANT]
> 48 GB+ unified memory is the recommended, tested configuration — full-accuracy 35B model at the full 192k context. Below that, setup defaults to Bonsai (PQ2_0 from 24 GB, PTQ1_0 from 16 GB) instead of the old 9B fallback; it is the newer Metal path, so `--model=qwen` is the escape hatch if a smaller Mac misbehaves. Below 16 GB, native inference doesn't install at all — use the agent role with a remote inference box.

### What the speeds feel like

| Speed | Experience |
|-------|------------|
| ~6 tok/s | Unusable for agentic work |
| ~8–10 tok/s | Slow but usable — every tool round-trip is a visible pause |
| ~17–20 tok/s | Usable — you notice the wait on long tool chains |
| ~50–70 tok/s | Feels like a fast cloud API |

For agentic coding tasks the agent typically makes many short tool calls with brief LLM turns between them, so sustained tok/s matters less than it does for long prose generation. 20 tok/s is workable, and so is ~10 — a 16 GB Mac mini running Bonsai PTQ1_0 sits right there and still completes agentic runs.

---

## Reasoning mode (`/think`)

Qwen has a native reasoning mode that makes the model think step-by-step before responding. Toggle it with `/think`.

### How it works

When enabled, the model emits a hidden `reasoning_content` stream alongside its response. You see it in the **thinking panel** as a collapsible block:

```
◈ Thinking [312 tok]
```

The block collapses automatically once the model starts its visible response. There is currently no keybinding to expand or collapse it manually.

### What the agent changes when thinking is on

| Parameter | Normal | Thinking |
|-----------|--------|----------|
| Temperature | 0.7 (Qwen preset) | **0.6** (Qwen3.x) / **1.0** (Qwen3.8) |
| Top-P | 0.8 | **0.95** |
| Presence penalty | 1.5 | **0.0** |
| `enable_thinking` | — | **true** |

Qwen3.x uses a lower thinking temperature (0.6) for consistency; Qwen3.8 uses 1.0. Zero presence penalty lets the model repeat terms as needed without being penalised — important for logical chains that revisit the same concepts.

### When to use it

| Task | Thinking? |
|------|-----------|
| Simple file edits, grep, quick lookups | Off — faster, no benefit |
| Debugging a subtle logic error | **On** |
| Refactoring with many interdependencies | **On** |
| Architecture planning, blast-radius analysis | **On** |
| Mechanical code generation (boilerplate) | Off |

### Cost of thinking

Thinking tokens consume context window. On the GPU model (192K) this is rarely a constraint. On the CPU model (192K) a deep reasoning chain can consume 4–8K tokens of context before the first visible word — factor this in for long sessions.

There is no configurable thinking budget cap in the current build. If context pressure becomes an issue during a thinking-heavy session, run `/compact` to free space.

---

## Sampling parameters (Qwen preset)

The defaults are tuned for Qwen3's training distribution:

| Parameter | Value | Effect |
|-----------|-------|--------|
| Temperature | 0.7 | Balanced creativity vs determinism |
| Top-P | 0.8 | Nucleus sampling — cuts low-probability tail |
| Top-K | 20 | Hard cap on token candidates |
| Presence penalty | 1.5 | Discourages repetition |
| Min-P | 0.0 | Disabled |
| Repetition penalty | 1.0 | Neutral |

Override any of these in `settings.json` under `llm.*` or per-model under `model_presets.qwen`.

---

## Context window and parallel users

The KV cache for the full context is allocated at startup. `--parallel N` splits it equally across slots:

| Mode | Context | Per-user context |
|------|---------|-----------------|
| GPU, `--parallel 1` (default) | 192K | **192K** |
| GPU, `--parallel 2` | 192K | **96K each** |
| CPU, `--parallel 1` (default) | 192K | **192K** |

For a single-user setup (typical), keep `--parallel 1` to maximise context. To add users, edit `docker/docker-compose.override.yml`.

---

## Backends (multiple inference endpoints)

A **backend** is a named inference endpoint (e.g. your local llama.cpp server, a remote relay,
Ollama, OpenAI). You can configure several and switch between them **at runtime, per session,
without restarting the agent.**

Backends live in `~/.openmono/backends.json` (0600). The `default` entry is what a new session
starts on; every other entry is reachable via `/models`.

### Config file format

```jsonc
{
  "default": {
    "name": "default",
    "endpoint": "http://relay.example:10422",   // OpenAI-compatible endpoint
    "default_model": "Qwen3.8-27B-UD-Q4_K_M",  // model used when switching to this backend
    "provider_type": "openai-compat",           // only "openai-compat" is supported
    "is_local": false,
    "api_key": "..."                            // optional; 0600 file, never logged
  },
  "local": {
    "name": "local",
    "endpoint": "http://localhost:7474",
    "default_model": "Ternary-Bonsai-2-27B-PQ2_0",
    "is_local": true
  },
  "echo15": {
    "name": "echo15",
    "nickname": "echo15",                        // optional display name shown in menus
    "endpoint": "http://relay.example:10070",
    "default_model": "qwen3.8-flash-next-iq3_s",
    "api_key": "..."
  }
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `name` | yes | Stable registry key. Switching uses this name (or the nickname). |
| `endpoint` | yes | OpenAI-compatible base URL. |
| `nickname` | no | Human-friendly label; menus show `[nickname] <model>`. Falls back to `name` when absent. |
| `default_model` | no | Model adopted when you switch to this backend. Leave unset and the backend's own `/v1/models` response is the truth. |
| `is_local` | no | `true` for on-box servers (no API key required, endpoint reachable via loopback on the host). |
| `api_key` | no | Persisted in the 0600 file so the backend survives restarts. Can also be supplied at runtime (kept in memory only). |

### Runtime commands (TUI / terminal)

| Command | What it does |
|---------|--------------|
| `/models` | Interactive backend menu. Probes every backend's `/v1/models` (5 s timeout, in parallel); unreachable ones render as **`offline`** instead of a model name. Select with 1–9 or ↑/↓ + Enter. |
| `/models list` | Non-interactive listing, same offline annotation. |
| `/models set NAME` | Switch the current session to `NAME` (name or nickname). Takes effect on the next message. |
| `/models NAME` | Shorthand for `set`. |
| `/models add NAME endpoint [--model=X] [--nickname=N]` | Add a backend (prompts for the API key, stored in memory). |
| `/models remove NAME` | Remove a backend from the registry. |

Switching is **session-scoped**: other sessions keep their own backend. The active backend's
default model is adopted automatically (you can still override the model name with `/model`).

### Non-interactive CLI (scripts, setup)

```
openmono config backends list
openmono config backends add <name> --endpoint <url> [--model <m>] [--nickname <n>] [--key <k>] [--local true]
openmono config backends remove <name>
openmono config backends set-key <name> <key>     # omit <key> to clear
```

Writes directly to `~/.openmono/backends.json` (0600) — useful for provisioning relays from
setup scripts. A running agent picks up file changes on its next restart.

### Two-level model selection

A backend is a *server*; a model is what the server serves. The two commands map to that:

- `/models` — pick the **server** (endpoint + its key)
- `/model` — pick the **model** the active server serves (`/model refresh` re-queries it)

Most relays serve exactly one model, so `/models` is usually all you need.

### Container networking

When the agent runs in its sandbox container, `localhost` is the container — not the host.
Loopback endpoints on backends marked `is_local` are therefore rewritten in-memory to
`host.docker.internal` (e.g. `http://localhost:7474` → `http://host.docker.internal:7474`).
`backends.json` itself is never modified, so the same file works on-box and in-container.

### ACP HTTP API (for the VS Code / Cursor extension)

The ACP server (the same process the extension talks to) exposes the full backend surface, so
the extension can list, create, remove, and switch backends — including by nickname:

| Method & path | Purpose | Body / response |
|---------------|---------|-----------------|
| `GET /api/v1/backends` | List backends | `{ backends: [{ name, endpoint, nickname, default_model, provider_type, is_local, has_key }] }` — keys are never exposed. |
| `POST /api/v1/backends` | Create/update a backend | `{ name, endpoint, default_model?, nickname?, provider_type?, is_local?, api_key? }` → `{ ok, name, endpoint, has_key }`. `api_key` is in-memory only. |
| `DELETE /api/v1/backends/{name}` | Remove a backend | 204 / 404. |
| `POST /api/v1/sessions/{id}/backend` | Switch a session's backend | `{ backend, api_key? }` — `backend` is a name **or nickname** → `{ ok, backend, model, has_key }`. 404 unknown session, 400 unknown backend, `ok: false, error: "no_api_key"` when a remote backend has no key. |

Notes for the extension:

- `POST /api/v1/sessions` accepts a `backend` field to start a session on a specific backend.
- `GET /api/v1/sessions/{id}` returns `active_backend`.
- The menu's **offline** annotation is computed client-side by probing `/v1/models` (5 s
timeout per backend, in parallel); the extension can do the same to badge dead relays.
- `has_key` is advisory: a persisted key in `backends.json` loads at startup; a key supplied
via `POST` lives in memory until process exit.

### Key handling

- Keys are kept **in memory** while the process runs. A persisted `api_key` in
  `backends.json` is the only on-disk form, and the file is written 0600.
- `/models add` and the TUI prompt store the key in memory for the session lifetime;
  re-entering it after a restart is only needed if it was never persisted.
- Keys are never logged and never returned by `GET /api/v1/backends` (only `has_key`).

---

## Using cloud models instead

> [!CAUTION]
> Cloud providers (OpenAI, Anthropic, Ollama) are WIP and untested. Local llama.cpp and Strata are the fully supported backends.

Providers are selected at agent startup. To use a cloud provider, configure it in `settings.json` and restart the agent:

```jsonc
{
  "providers": {
    "anthropic": { "api_key": "sk-ant-...", "model": "claude-sonnet-4-20250514", "active": true }
  }
}
```

The local llama-server keeps running in the background. To switch back, mark the local provider active in `settings.json` and restart the agent; `/model` then selects the model name served by that backend.

---

## Credits & resources

**Qwen** is built by [Alibaba](https://qwenlm.github.io/). OpenMono bundles quantized GGUF versions from [Unsloth](https://github.com/unslothai/unsloth) for optimal consumer hardware performance.

### Model repositories

| Model | Type | Repo |
|-------|------|------|
| **Qwen3.8-27B-Q4_K_M** | Dense (GPU) | [unsloth/Qwen3.8-27B-GGUF](https://huggingface.co/unsloth/Qwen3.8-27B-GGUF) |
| **Qwen3.6-35B-A3B-UD-Q4_K_XL** | MoE (CPU) | [unsloth/Qwen3.6-35B-A3B-GGUF](https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF) |

### Technical references

- [Qwen3.8-27B Model Card](https://huggingface.co/Qwen/Qwen3.8-27B) — architecture, benchmarks, release notes
- [Qwen on HuggingFace](https://huggingface.co/Qwen) — all Qwen model releases and documentation
