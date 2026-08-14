# Models

---

## Model selection

The installer picks the right model automatically based on your hardware. Two models ship:

| | GPU | CPU |
|--|-----|-----|
| **Model** | Qwen3.8-27B-Q4_K_M | Qwen3.6-35B-A3B-UD-Q4_K_XL |
| **Type** | Dense | MoE (Mixture of Experts) |
| **Disk** | ~15.5 GB | ~17.6 GB |
| **VRAM / RAM** | ~23 GB (model + KV cache) | ~20 GB |
| **Context** | 192K tokens | 192K tokens |
| **Trigger** | NVIDIA GPU + nvidia-container-toolkit | No GPU or no nvidia runtime |

### Why different models for GPU vs CPU?

Token generation speed is memory-bandwidth bound — the bottleneck is how fast you can read model weights per token, not raw compute.

**GPU (dense 27B):** VRAM bandwidth is ~900 GB/s. Reading 15.5 GB takes ~17 ms → ~60 tok/s. Token generation speed is bandwidth-bound on model size, not on benchmark quality, so this holds for Qwen3.8-27B at the same disk/VRAM footprint as Qwen3.6-27B. Dense models fully utilise GPU parallel execution. Putting the MoE model on a GPU wastes VRAM and doesn't help — sparse routing doesn't parallelise as well as dense matmuls on CUDA.

**CPU (MoE 35B-A3B):** DDR5 RAM bandwidth is ~89 GB/s — 10× slower than VRAM. A dense 27B would read 15.5 GB per token → ~174 ms → ~6 tok/s (unusable). The MoE model activates only ~3B parameters per token, reading ~1.7 GB instead → ~19 ms → **~20 tok/s**. You get near-GPU quality from a 35B-equivalent model at CPU speeds that are actually usable.

Short version: **dense models are fast on GPU, MoE models are fast on CPU.**

### Apple Silicon (Metal)

On macOS the model runs natively against the Metal GPU and unified memory — there's no separate "VRAM vs RAM" split, so the MoE model is the right choice on high-memory Macs. The installer picks the tier from unified memory size:

| Unified memory | Model | Type | Accuracy |
|----------------|-------|------|----------|
| 48 GB+ | Qwen3.6-35B-A3B-UD-Q4_K_XL | MoE | Full |
| 32 GB | Qwen3.5-9B-Q4_K_M | Dense | Lower |
| 16 GB | Qwen3.5-9B-Q4_K_M | Dense | Lower |

The 35B-A3B MoE activates only ~3B parameters per token, so on a high-bandwidth Apple Silicon chip it hits the same usable-to-fast range as a Linux GPU while keeping full accuracy.

> [!IMPORTANT]
> Only the 64 GB+ tier (full-accuracy 35B) has been validated as stable. The installer will still configure the lower tiers, but **less than 64 GB is not encouraged** — they fall back to the 9B model with a much tighter context window. 16 GB is the hard floor for native inference; below that, use the agent role with a remote inference box.

---

## Why Qwen3.8 (GPU) / Qwen3.6 (CPU)?

Qwen was chosen because it's the best open-weight model family for agentic coding tasks at consumer hardware sizes — not just in raw benchmark numbers, but in how it handles tool use, multi-step reasoning, and real-world code iteration.

**Qwen3.8-27B (dense)** — the GPU model:
- Benchmarks (SWE-bench, etc.) not yet measured on this build — check the [Qwen3.8-27B model card](https://huggingface.co/Qwen/Qwen3.8-27B) for upstream numbers
- Fits in 24 GB VRAM quantised, runs at ~60 tok/s — same footprint and bandwidth profile as Qwen3.6-27B
- Native reasoning mode (`/think`) carries over from the Qwen3.x line

**Qwen3.6-35B-A3B (MoE)** — the CPU model:
- **EvalPlus: 71.45** aggregate (HumanEval, MBPP, HumanEval+, MBPP+)
- Only ~3B parameters active per token — makes 35B-class quality achievable at CPU speeds
- Same 192K context as the GPU model

Both models are designed for agentic workflows, not just benchmark numbers — the training emphasises tool calling, code iteration, and multi-turn task completion.

> [!NOTE]
> You're not locked to Qwen. Any GGUF model that fits your hardware can be configured via `settings.json` — point `llm.model_path` to a local GGUF file or use `/model <name>` to switch providers mid-session.

---

## Hardware benchmarks

### GPU — Qwen3.8-27B-Q4_K_M (192K context)

| Hardware | VRAM | tok/s | Notes |
|----------|------|-------|-------|
| RTX 3090 | 24 GB | ~45–70 | Best price/performance used (~$700–800). Same as Qwen3.6-27B — bandwidth-bound on model size, not benchmark quality |
| RTX 4090 | 24 GB | ~70–100 | ~40% faster, ~2× the cost. Same as Qwen3.6-27B — bandwidth-bound on model size, not benchmark quality |

### CPU — Qwen3.6-35B-A3B-UD-Q4_K_XL (192K context)

Token generation is memory-bandwidth bound — **RAM channel count matters as much as CPU speed**.

| Hardware | RAM config | Bandwidth | tok/s |
|----------|------------|-----------|-------|
| NUC 13th-gen i5 | DDR5 5600, dual-channel | ~89 GB/s | ~17 |
| NUC 13th-gen i5 | DDR5 5600, single-channel | ~45 GB/s | ~8.5 |
| Ryzen 9 7940HS | DDR5 5600, dual-channel | ~89 GB/s | ~20 |

> Halving RAM channels halves throughput. Always fill both DIMM slots.

### Apple Silicon (Metal)

Apple Silicon's unified memory has far higher bandwidth than a desktop CPU's DDR5, so the 35B-A3B MoE that runs at ~20 tok/s on a NUC reaches GPU-class speeds on a high-memory Mac.

| Hardware | Unified memory | Model | Context (vision on) | tok/s | Status |
|----------|----------------|-------|---------------------|-------|--------|
| M5 Pro | 64 GB | Qwen3.6-35B-A3B-UD-Q4_K_XL (full) | 192k (168k) | ~45–48 | ✅ Recommended / tested |
| M1 Max | 32 GB | Qwen3.5-9B-Q4_K_M (lower) | 64k (48k) | ~22–27 | ⚠️ Not encouraged |
| M4 | 16 GB | Qwen3.5-9B-Q4_K_M (lower) | 16k (12k) | ~12–16 | ⚠️ Not encouraged |

> [!IMPORTANT]
> 64 GB+ unified memory is the recommended, tested configuration — full-accuracy 35B model at the full 192k context. The 32 GB and 16 GB tiers install and run, but **less than 64 GB is not encouraged** — they fall back to the 9B model with much tighter context windows. Below 16 GB, native inference doesn't install at all — use the agent role with a remote inference box.

### What the speeds feel like

| Speed | Experience |
|-------|------------|
| ~6 tok/s | Unusable for agentic work |
| ~17–20 tok/s | Usable — you notice the wait on long tool chains |
| ~50–70 tok/s | Feels like a fast cloud API |

For agentic coding tasks the agent typically makes many short tool calls with brief LLM turns between them, so sustained tok/s matters less than it does for long prose generation. 20 tok/s is workable.

---

## Reasoning mode (`/think`)

Qwen has a native reasoning mode that makes the model think step-by-step before responding. Toggle it with `/think` or `Ctrl+T`.

### How it works

When enabled, the model emits a hidden `reasoning_content` stream alongside its response. You see it in the **thinking panel** as a collapsible block:

```
◈ Thinking [312 tok]
```

The block expands on `Ctrl+T`. It collapses automatically once the model starts its visible response.

### What the agent changes when thinking is on

| Parameter | Normal | Thinking |
|-----------|--------|----------|
| Temperature | 0.7 (Qwen preset) | **0.6** |
| Presence penalty | 1.5 | **0.0** |
| `enable_thinking` | — | **true** |

Lower temperature keeps reasoning chains consistent. Zero presence penalty lets the model repeat terms as needed without being penalised — important for logical chains that revisit the same concepts.

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

Override any of these in `settings.json` under `llm.*` or per-model under `modelPresets.qwen`.

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

## Using cloud models instead

> [!CAUTION]
> Cloud providers (OpenAI, Anthropic, Ollama) are WIP and untested. Local llama.cpp is the only fully supported provider.

If you prefer a cloud model for a session, switch without restarting:

```bash
/model claude-sonnet-4-20250514   # Anthropic (requires ANTHROPIC_API_KEY)
/model gpt-4o                     # OpenAI (requires OPENAI_API_KEY)
```

Or set permanently in `settings.json`:

```jsonc
{
  "providers": {
    "anthropic": { "api_key": "sk-ant-...", "model": "claude-sonnet-4-20250514", "active": true }
  }
}
```

The local llama-server keeps running in the background — switch back to it any time with `/model qwen3.8-27b` (GPU) or `/model qwen3.6-35b-a3b` (CPU).

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
