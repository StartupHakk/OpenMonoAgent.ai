# Models

---

## Model selection

The installer picks the right model automatically based on your hardware. Two models ship:

| | GPU | CPU |
|--|-----|-----|
| **Model** | Qwen3.6-27B-Q4_K_M | Qwen3.6-35B-A3B-UD-Q4_K_XL |
| **Type** | Dense | MoE (Mixture of Experts) |
| **Disk** | ~15.5 GB | ~17.6 GB |
| **VRAM / RAM** | ~23 GB (model + KV cache) | ~20 GB |
| **Context** | 192K tokens | 192K tokens |
| **Trigger** | NVIDIA GPU + nvidia-container-toolkit | No GPU or no nvidia runtime |

### Why different models for GPU vs CPU?

Token generation speed is memory-bandwidth bound — the bottleneck is how fast you can read model weights per token, not raw compute.

**GPU (dense 27B):** VRAM bandwidth is ~900 GB/s. Reading 15.5 GB takes ~17 ms → ~60 tok/s. Dense models fully utilise GPU parallel execution. Putting the MoE model on a GPU wastes VRAM and doesn't help — sparse routing doesn't parallelise as well as dense matmuls on CUDA.

**CPU (MoE 35B-A3B):** DDR5 RAM bandwidth is ~89 GB/s — 10× slower than VRAM. A dense 27B would read 15.5 GB per token → ~174 ms → ~6 tok/s (unusable). The MoE model activates only ~3B parameters per token, reading ~1.7 GB instead → ~19 ms → **~20 tok/s**. You get near-GPU quality from a 35B-equivalent model at CPU speeds that are actually usable.

Short version: **dense models are fast on GPU, MoE models are fast on CPU.**

---

## Why Qwen3.6?

Qwen3.6 was chosen because it's the best open-weight model family for agentic coding tasks at consumer hardware sizes — not just in raw benchmark numbers, but in how it handles tool use, multi-step reasoning, and real-world code iteration.

**Qwen3.6-27B (dense)** — the GPU model:
- **77.2% on SWE-bench Verified** — competitive with frontier proprietary models. For reference, Claude Opus 4.7 scores 80.8%.
- Outperforms its predecessor (Qwen2.5-72B) on most coding benchmarks at less than half the parameter count
- Fits in 24 GB VRAM quantised, runs at ~60 tok/s — the same speed tier as fast cloud APIs
- Native reasoning mode (`/think`) for complex multi-step problems

**Qwen3.6-35B-A3B (MoE)** — the CPU model:
- **EvalPlus: 71.45** aggregate (HumanEval, MBPP, HumanEval+, MBPP+)
- Only ~3B parameters active per token — makes 35B-class quality achievable at CPU speeds
- Same 192K context as the GPU model

Both models are designed for agentic workflows, not just benchmark numbers — the training emphasises tool calling, code iteration, and multi-turn task completion.

> [!NOTE]
> You're not locked to Qwen3.6. Any GGUF model that fits your hardware can be configured via `settings.json` — point `llm.model_path` to a local GGUF file or use `/model <name>` to switch providers mid-session.

---

## Hardware benchmarks

### GPU — Qwen3.6-27B-Q4_K_M (192K context)

| Hardware | VRAM | tok/s | Notes |
|----------|------|-------|-------|
| RTX 3090 | 24 GB | ~45–70 | Best price/performance used (~$700–800) |
| RTX 4090 | 24 GB | ~70–100 | ~40% faster, ~2× the cost |

### CPU — Qwen3.6-35B-A3B-UD-Q4_K_XL (192K context)

Token generation is memory-bandwidth bound — **RAM channel count matters as much as CPU speed**.

| Hardware | RAM config | Bandwidth | tok/s |
|----------|------------|-----------|-------|
| NUC 13th-gen i5 | DDR5 5600, dual-channel | ~89 GB/s | ~17 |
| NUC 13th-gen i5 | DDR5 5600, single-channel | ~45 GB/s | ~8.5 |
| Ryzen 9 7940HS | DDR5 5600, dual-channel | ~89 GB/s | ~20 |

> Halving RAM channels halves throughput. Always fill both DIMM slots.

### What the speeds feel like

| Speed | Experience |
|-------|------------|
| ~6 tok/s | Unusable for agentic work |
| ~17–20 tok/s | Usable — you notice the wait on long tool chains |
| ~50–70 tok/s | Feels like a fast cloud API |

For agentic coding tasks the agent typically makes many short tool calls with brief LLM turns between them, so sustained tok/s matters less than it does for long prose generation. 20 tok/s is workable.

---

## Reasoning mode (`/think`)

Qwen3.6 has a native reasoning mode that makes the model think step-by-step before responding. Toggle it with `/think` or `Ctrl+T`.

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

The local llama-server keeps running in the background — switch back to it any time with `/model qwen3.6-27b`.

---

## Credits & resources

**Qwen3.6** is built by [Alibaba](https://qwenlm.github.io/). OpenMono bundles quantized GGUF versions from [Unsloth](https://github.com/unslothai/unsloth) for optimal consumer hardware performance.

### Model repositories

| Model | Type | Repo |
|-------|------|------|
| **Qwen3.6-27B-Q4_K_M** | Dense (GPU) | [unsloth/Qwen3.6-27B-GGUF](https://huggingface.co/unsloth/Qwen3.6-27B-GGUF) |
| **Qwen3.6-35B-A3B-UD-Q4_K_XL** | MoE (CPU) | [unsloth/Qwen3.6-35B-A3B-GGUF](https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF) |

### Technical references

- [Qwen3.6 Technical Report](https://arxiv.org/pdf/2505.09388) — benchmarks, training methodology, reasoning mode details
- [Qwen Blog Post](https://qwen.ai/blog?id=qwen3.6-27b) — release announcement and key improvements over Qwen2.5
- [Qwen on HuggingFace](https://huggingface.co/Qwen) — all Qwen model releases and documentation
