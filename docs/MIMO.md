# MiMo-V2.6-Flash on OpenMono (Mosaic MoE path)

This is the design and memory-budget note for running Xiaomi's **MiMo-V2.6-Flash
(RL variant)** as an OpenMono coding agent on a **24 GB+ VRAM / 64 GB+ RAM**
NVIDIA box, with **no model-weight reads from NVMe/SSD in the hot path**.
Weights may be loaded once at startup into a pinned RAM arena. Per-token SSD
reads and mmap paging are out of scope.

The runtime that owns this path is **Mosaic** (`scripts/mosaic.sh`,
`src/OpenMono.Moe`). Qwen3.8-Flash-Next via **Strata** (`scripts/strata.sh`) is
unchanged. See [MOE-ENGINE.md](MOE-ENGINE.md) for the adapter interface.

**Objective.** Highest SWE-bench (Verified, plus DeepSWE / Terminal-Bench where
published) that this structure can actually hold. Fit is necessary. Quality is
the ranking criterion.

## Official model (verified)

| | |
|---|---|
| **Repo** | [XiaomiMiMo/MiMo-V2.6-Flash-RL](https://huggingface.co/XiaomiMiMo/MiMo-V2.6-Flash-RL) |
| **Created** | 2026-09-21 (Hugging Face `createdAt`) |
| **License** | MIT |
| **Architecture class** | `MiMoV2ForCausalLM` (`model_type: mimo_v2`) |
| **Total / active** | 309B total / 15B activated (card). Safetensors parameter count: 310,756,322,688 |
| **Official on-disk size** | Hugging Face treesize `main`: **177,767,644,198 bytes (165.56 GiB / 177.77 GB)**. Native store is mxfp4 experts + FP8 compute + BF16 router/attention leftovers. |
| **DFlash drafter** | `dflash/` subtree: **2,936,151,148 bytes (2.73 GiB)** |

Card claims used for coding-agent ranking (provider-reported, full-precision /
native mxfp4-FP8 serve, not a 24+64 consumer quant):

| Benchmark | MiMo-V2.6-Flash | Source |
|---|---:|---|
| DeepSWE v1.1 | **67.9** | official card + arXiv:2610.11959 |
| Terminal-Bench 2.1 | **87.6** | official card |
| Terminal-Bench 4.0 | **28.8** | official card |
| MiMo Code Bench | 61.2 | official card |
| Toolathlon-Verified | 73.6 | official card |
| SWE-bench Verified | **not listed** on the official V2.6 table | card / tech report |

A third-party listing (AnyAPI) quotes **78.6 SWE-bench Verified in thinking
mode**. That figure is **not** on the official card or the V2.6 technical
report. Treat it as unverified. benchgap publishes an **estimated** 89.2
SWE-bench Verified with an uncertainty band; that is not a measured run.

The official SWE-bench Verified number that *does* exist in the V2.6 writeup
is for the **9B distill**, not Flash: Distill-Qwen-9B goes from 61.1 to 66.2
after RL. Do not use that as a Flash score.

## Architecture (from `config.json` + card)

Fetched from
`https://huggingface.co/XiaomiMiMo/MiMo-V2.6-Flash-RL/raw/main/config.json`
(sha `dcd3fd83` at fetch time).

| Knob | Value |
|---|---|
| Layers | 48 |
| Hybrid pattern | 9 global-attention (GA) + 39 sliding-window (SWA). `hybrid_layer_pattern` is 0=GA, 1=SWA. First block is GA. |
| MoE schedule | Layer 0 is dense FFN (`moe_layer_freq[0]=0`). Layers 1-47 are MoE. **No shared experts** (`n_shared_experts: null`). |
| Hidden size | 4096 |
| Vocab | 152,576 |
| Context | 1,048,576 (`max_position_embeddings`) |
| Routed experts | 256 per MoE layer |
| Activated experts | 8 per token (`num_experts_per_tok`) |
| Expert FFN width | `moe_intermediate_size = 2048` |
| Dense FFN width (layer 0) | `intermediate_size = 16384` |
| Router | `scoring_func = sigmoid`, `topk_method = noaux_tc`, `norm_topk_prob = true`, `n_group = 1` |
| SWA | 64 Q / 8 KV, window **128**, QK head dim 192, V head dim 128, SWA RoPE theta 10,000 |
| GA | 64 Q / 4 KV, QK head dim 192, V head dim 128, RoPE theta 10,000,000, partial rotary 0.334 |
| Attention layout | fused QKV, `attention_value_scale = 0.707` |
| MTP / drafter | Card: 5-layer SWA DFlash, window 1024, predicts 7 tokens per pass. Config also has `num_nextn_predict_layers = 3` (embedded MTP blocks). Separate `dflash/` weights ship in the official snapshot. |
| Vision / audio | 681M ViT + audio tokenizer. **Not used** on the recommended coding-agent pack (saves ~2.5-3 GiB and VRAM). |

Chat template (`chat_template.jinja`): ChatML-style `<|im_start|>role` /
`<|im_end|>`, tools in `<tools>…</tools>`, tool calls as
`<tool_call><function=name><parameter=…>`, reasoning wrapped in
`<think>…</think>`. Thinking is a **binary** `enable_thinking` flag (empty
`<think></think>` when false). Recommended sampling from
`generation_config.json` and the vLLM recipe: **temperature 1.0, top_p 0.95**,
thinking on for hard coding work.

## Why the hot path cannot touch SSD

24 GB VRAM + 64 GB RAM is **88 GB** of fast memory before OS, CUDA, KV, and
scratch. Official weights are 165.56 GiB. Even the smallest *unpruned*
community GGUF is ~92 GiB. If the runtime mmaps that file, decode pages experts
in from NVMe every time the router misses the working set. That is the failure
mode Spencer ruled out.

The Strata pattern we copy (and that PolyStrata generalized to other families,
MIT, [VecSzn/PolyStrata](https://github.com/VecSzn/PolyStrata)):

1. **Once at startup:** read the pack from disk into a **pinned RAM arena**
   (all routed experts that survived pruning).
2. **Hot path:** router writes chosen expert ids to a GPU doorbell. Hits run
   from a VRAM expert cache. Misses DMA from the RAM arena, or run on CPU in
   place. No file descriptor, no mmap.

Strata's kernels and pack format are architecture-specific (upstream issues
#310, #479). We do **not** fork those kernels for MiMo. Mosaic is a new
runtime with a family adapter. Qwen3.8-Flash-Next stays on stock Strata.

## Measured community artifacts (Hugging Face API)

Queried 2026-10-17 via `/treesize` and `/tree?expand=1`.

| Artifact | Bytes | GiB | Notes |
|---|---:|---:|---|
| Official Flash-RL snapshot | 177,767,644,198 | 165.56 | mxfp4/FP8. Needs multi-GPU serve (vLLM recipe: 4x H200). |
| Official `dflash/` | 2,936,151,148 | 2.73 | Separate speculative drafter. |
| TrevorJS IQ2_XS_M v2.1 (10 shards) | 98,692,967,808 | **91.90** | Unpruned 309B mixed IQ2. Smallest full-expert community GGUF found. Card quotes ~90.8-91.9 GiB depending on revision. GSM8K 95.6% (not SWE). |
| TrevorJS DFlash Q8_0 | 2,229,921,888 | 2.08 | Extra, optional. |
| Baekpica mixed IQ2 treesize | 93,591,634,196 | 87.17 | Role-aware IQ2_XXS/XS + Q8 trunk + BF16 mmproj + DFlash. Main LM ~82.7 GiB. |
| patrickbdevaney REAP50 Q2_K | 66,187,787,968 | **61.64** | 50% experts removed (REAP+HOPE). Card: ~3.36 bpw, trunk Q8_0. **No quality benches on the card.** |
| REAP50 MXFP4_MOE | 92,403,924,672 | 86.06 | Flagship pruned, still too big for 64+24 once OS/KV are paid. |
| REAP50 MTP Q8_0 | 2,383,728,544 | 2.22 | Keep this. Cheap relative to quality. |
| REAP50 mmproj Q8_0 | 1,564,438,240 | 1.46 | Skip for text-only OpenMono. |

llama.cpp CPU-MoE (`--n-cpu-moe` / `-ot`) plus `--no-mmap` is the baseline to
beat on speed. It can also pin weights in RAM, but it has no doorbell / hot
expert VRAM cache and no family-specific pack. Mosaic exists to close that gap
for MiMo, not to replace llama.cpp for Qwen.

## Memory model on a 24+64 box

### KV cache is not the limiter (hybrid SWA)

Per-token **GA** KV at FP16:

```
9 layers * 4 KV heads * (192 + 128) * 2 bytes = 23,040 bytes = 22.5 KiB/token
```

SWA KV is **window-capped** at 128 tokens per layer, regardless of context:

```
39 * 8 * (192 + 128) * 2 * 128 = 25.56 MiB FP16  (fixed)
```

| Context | GA KV FP16 | GA KV Q8 | GA KV Q4 | SWA FP16 | Notes |
|---|---:|---:|---:|---:|---|
| 32,768 | 0.70 GiB | 0.35 GiB | 0.18 GiB | 0.03 GiB | Comfortable |
| 65,536 | 1.41 GiB | 0.70 GiB | 0.35 GiB | 0.03 GiB | Comfortable |
| 131,072 | 2.81 GiB | 1.41 GiB | 0.70 GiB | 0.03 GiB | **Recommended** |
| 196,608 (OpenMono default) | 4.22 GiB | 2.11 GiB | 1.05 GiB | 0.03 GiB | Fits. Only ~0.7 GiB more than 131k at Q8. |
| 1,048,576 | 22.5 GiB | 11.3 GiB | 5.6 GiB | 0.03 GiB | Crowds out the expert cache. Do not. |

So OpenMono's 196,608 default is **KV-feasible** on 24 GB if weights are split
correctly. The thing that does not fit is the **weight** working set.

### Fast-memory envelope (estimates marked)

| Bucket | 24 GB VRAM | 64 GB RAM | Notes |
|---|---:|---:|---|
| OS + desktop + CUDA context | 1.5 | 4.0 | Estimate |
| Scratch / PCIe bounce / runtime | 1.0 | 3.0 | Estimate |
| KV @ 131k Q8 (GA+SWA) | 1.5 | 0 | Measured from the formula above |
| **Left for weights** | **~20** | **~57** | **~77 GiB combined** |

Anything whose pinned working set exceeds ~77 GiB will page. Anything whose
RAM slice exceeds ~57 GiB will page even if VRAM still has room.

### Weight-size estimates (not HF-measured)

`params * bits / 8`, using 309B total and ~90% expert / ~10% trunk (estimate,
aligned with typical sparse MoE splits and the REAP50 "157.4B remaining"
card):

| Scheme | Formula | Size | Fits 77 GiB envelope? |
|---|---|---:|---|
| Official mxfp4/FP8 | HF treesize | 165.56 GiB | No |
| Unpruned ~2.0 bpw all tensors | 309e9 * 2 / 8 | 72.0 GiB | Borderline as a raw number; real mixed IQ2 is **91.90 GiB** (measured) because attention/embed stay fatter. **No** |
| Unpruned TrevorJS IQ2_XS_M | HF tree | 91.90 GiB | No (OS+KV push it over) |
| Unpruned 1.89 bpw (Strata Coder density) | 309e9 * 1.89 / 8 | 67.9 GiB | Maybe as a raw number; no published MiMo pack at this density |
| REAP50 Q2_K + MTP Q8, no mmproj | 61.64 + 2.22 | **63.86 GiB** | **Yes**, with a VRAM/RAM split |
| REAP40 (estimate, 60% experts kept) | 61.64 * 1.20 + 2.22 | ~76.2 GiB | Tight. RAM slice after putting 16 GiB on the GPU is ~60 GiB, over the 57 GiB RAM budget. **No**, unless a custom pack lands under ~72 GiB |
| REAP25 (estimate) | 61.64 * 1.50 + 2.22 | ~94.7 GiB | No |

**Honest fit line.** Full-expert MiMo Flash does **not** fit 24+64 with
nothing on SSD in the hot path, at any community quant we can measure. The
closest published artifact that fits is **REAP50 Q2_K + Q8 MTP, text only**.

## Ranked configs (quality first)

Ranking is "expected SWE-bench Verified, then DeepSWE / Terminal-Bench, at
this budget." Published numbers beat estimates. Estimates are labeled.

| Rank | Config | Working set | SWE-Verified | DeepSWE | TB 2.1 | Fits 24+64 no-SSD? | Evidence |
|---|---|---:|---|---|---|---|---|
| **A (recommended for Mosaic)** | REAP50, 128/256 experts, role-aware mixed: Q8 trunk + router + dense-0 + MTP; Q3_K last-layer experts and every expert **down** proj; Q2_K remaining gate/up. Context **131,072** Q8 KV. Thinking on. No vision/audio. | **~64-68 GiB** (61.64 measured Q2_K + 2.22 MTP + ~2-4 GiB pack overhead estimate) | **Unknown.** Estimate **~62-72** if full Flash is ~78.6 and retention tracks GSQ-RCO Coder (91.3%). If full Flash has no SWE-Verified, this cell stays unknown. | Estimate **~58-64** (0.85-0.95 x 67.9) | Estimate **~75-84** (0.85-0.95 x 87.6) | **Yes** (split: ~12-16 GiB trunk+hot experts in VRAM, ~48-52 GiB experts in RAM) | Fit: HF-measured 61.64 GiB. Quality: **no MiMo REAP50 benches.** Retention analogue: ISTA-DASLab GSQ-RCO Coder on Qwen3.8-Flash-Next (50% experts dropped, ~3.5 bpw) keeps **91.3%** of BF16 SWE-Verified (75.60 / 82.80) and 98.7% of LiveCodeBench v6, all at `xhigh`. Agentic SWE drops more than single-shot code. REAP paper (arXiv:2609.18916, cited by the REAP50 card) is usage/saliency pruning, not random. |
| B | Same as A but context **196,608** Q8 KV | +0.7 GiB VRAM vs A | Same unknown as A. Long-repo tasks may gain a little. | Same unknown | Same unknown | **Yes**, KV is cheap. Slightly less VRAM for the hot-expert cache. | KV formula above. Prefer A until a GPU-box run shows the cache hit rate stays high at 196k. |
| C | REAP50 Q2_K as shipped (no extra last-layer bits), 65k ctx | 63.86 GiB | Same unknown, slightly worse than A (no last-layer upcast) | Same unknown | Same unknown | Yes | Measured file. Last-layer / down-proj upcast is a GSQ/Unsloth-style prior, not measured on MiMo. |
| D | Unpruned TrevorJS IQ2_XS_M + DFlash Q8, 32k ctx, `--no-mmap` | 93.98 GiB | Unknown (GSM8K 95.6% only) | Unknown | Unknown | **No.** 94 GiB weights + OS + KV exceeds 88 GiB. mmap would page. | HF-measured 91.90 + 2.08 |
| E | Official mxfp4/FP8, any ctx | 165.56 GiB | Official card does not publish SWE-Verified. DeepSWE **67.9**, TB 2.1 **87.6** at native serve. | **67.9** | **87.6** | **No** | Official card + treesize |
| *Reference, not Mosaic* | **Qwen3.8-Flash-Next GSQ-RCO Coder on Strata** (256 of 512 experts, 1.89-3.5 bpw depending on build, 58.4 GB file / ~29.6 GB resident) | ~58-70 GiB | **75.60 published** (91.3% of BF16 82.80) | Not published on that card | Not published on that card | **Yes**, and already shipping via `scripts/strata.sh` | ISTA-DASLab card, `xhigh` effort |

### Why A is the MiMo recommendation

1. It is the **highest-precision trunk** we can keep (Q8 attention / router /
   embeddings / MTP). Routing and attention errors compound across a SWE
   trajectory more than a single expert's 2-bit noise.
2. It spends extra bits where GSQ-style recipes spend them: **down-proj** and
   **last MoE layer**. The Coder card's gap between LiveCodeBench (98.7%
   retained) and SWE-Verified (91.3% retained) is the reason: multi-step
   agents accumulate error.
3. 50% usage-based prune is the only published MiMo artifact that fits. Random
   prune is worse; do not do it. A later usage-trace prune of the *same* 50%
   (built from OpenMono sessions) can replace the community REAP mask without
   changing the arena layout.
4. 131,072 context is enough for a large repo slice and leaves VRAM for the
   hot-expert cache. 196,608 is a flag, not the default, until hit-rate is
   measured.
5. MTP/DFlash stays on. Speculative decode does not change quality; it
   recovers tokens/s so thinking-on stays usable.

Do **not** drop `num_experts_per_tok` below 8. That is a quality cliff the
card never measured at reduced top-k.

### Closest workable config if A is still tight on a particular box

Drop MTP first (lose speculative speed, keep quality). Then drop last-layer
upcast (config C). Then cut context to 65,536. Do not go below REAP50 and do
not turn mmap back on.

## Can compressed MiMo beat Qwen3.8-Flash-Next on Strata at this budget?

**Plain answer: not on published evidence, and probably not on SWE-bench
Verified after the prune+quant this box requires.**

What is solid:

- Strata Coder (Qwen3.8-Flash-Next GSQ-RCO, 50% experts dropped) is
  **75.60 SWE-bench Verified**, measured, and **fits** 24+64. BF16 Qwen is
  82.80. That is the quality floor of a *published* coding MoE at this
  budget.
- Full MiMo Flash is a stronger *native* agent on Xiaomi's own long-horizon
  benches (**DeepSWE 67.9**, **TB 2.1 87.6**). Those benches are not
  SWE-Verified. Qwen's Coder card does not report DeepSWE or TB 2.1, so a
  cross-family win on those two is **unproven either way**.
- Full-expert MiMo **cannot** run on 24+64 without SSD in the hot path
  (smallest measured unpruned GGUF: 91.90 GiB).
- The only MiMo pack that fits is a **50% expert prune + ~2-3.4 bpw** build
  with **zero published SWE / DeepSWE / TB numbers**.

What we can reasonably infer (estimates):

- The one published analogue of "drop half the experts + ~3 bpw, then run
  SWE-Verified" is Qwen's own Coder build: **-8.7 points** (82.80 -> 75.60).
- If the unverified 78.6 SWE-Verified for full Flash were real, the same
  relative hit lands around **72**. That is **below** Strata Coder's 75.60.
- If full Flash's true SWE-Verified is closer to DeepSWE's difficulty
  (67.9), the compressed score is lower still.
- DeepSWE and TB 2.1 might still favor even a compressed MiMo, because the
  native gaps (67.9 / 87.6) are large and those benches match OpenMono's
  agent loop better than single-shot code. That is a hypothesis, not a
  measurement.

**Ship Mosaic as experimental.** Keep **`--model=flash-next` (Strata Coder on
a 32-64 GB box, or the full GSQ-RCO family on 64 GB)** as the quality-max
*published* choice at this budget. Use `--model=mimo-flash` when you want the
Flash-RL lineage (DeepSWE / TB) and accept an unbenchmarked REAP50+Q2 pack.

Default `openmono setup` stays Qwen on llama.cpp. Nothing here changes that.

## Installer / agent wiring

| | |
|---|---|
| Catalog id | `mimo-flash` (aliases: `mimo`, `mimo-v2.6-flash`) |
| Backend | `mosaic` (host-native, `scripts/mosaic.sh`) |
| Gate | Linux + NVIDIA + **24 GB+ VRAM** + **64 GB-class RAM** (`CAN_MOSAIC`). 64 GB boxes that report ~62 GiB in `/proc/meminfo` still pass (threshold 60 GiB). |
| Port | `8090` (Strata keeps `8080`) |
| Provider | `mosaic` / `MiMo-V2.6-Flash` |
| Reasoning | Binary `enable_thinking` (on by default). Sampling 1.0 / 0.95 when thinking. |
| Context default | 131,072 (196,608 is opt-in) |

Strata / llama / Bonsai paths are untouched unless you pass
`--model=mimo-flash` or `OPENMONO_INFERENCE_BACKEND=mosaic`.

## What this milestone tests vs what it does not

**Tested on this VM (no GPU, no 64 GB, no weights):**

- Packer + block quantizer on tiny synthetic tensors (round-trip, size math,
  role-aware bit assignment).
- Router (sigmoid, top-8, `norm_topk_prob`) and expert-cache / doorbell
  logic, including "miss stays in the RAM arena, never opens a file."
- CPU reference forward for a toy MiMo-shaped stack (hybrid SWA/GA, dense
  layer 0, MoE after, residual + RMSNorm).
- Catalog / provider / reasoning-profile wiring.
- Full `dotnet test` suite, plus `mosaic.sh check` in a dry environment.

**Not tested (needs a real GPU box):** see the checklist below.

## Needs a real GPU box

- [ ] Download REAP50 Q2_K + MTP Q8 (or rebuild an equivalent pack from the
      official snapshot). Confirm SHA and that the process **mlock**s the
      arena (`MOSAIC_ALLOW_MMAP=0`).
- [ ] Startup load into RAM+VRAM with `vmstat` / `nvidia-smi` showing **zero**
      major faults on the pack fd after the first token.
- [ ] Doorbell path: cached experts on GPU, misses via DMA from the arena,
      CPU fallback under burst.
- [ ] Decode tok/s vs llama.cpp `--n-cpu-moe --no-mmap` on the same file.
- [ ] Thinking on/off, tool calls, SSE streaming against `openmono agent`.
- [ ] SWE-bench Verified (and DeepSWE / TB 2.1 if the harness is available)
      on config A. **Until that lands, do not claim a quality win over
      Strata Coder.**
- [ ] 131,072 vs 196,608 cache hit-rate and VRAM headroom.
- [ ] Dual-box tunnel: Mosaic on `:8090`, gateway optional.

## Credits / licenses

- MiMo-V2.6-Flash-RL: Xiaomi MiMo Team, MIT.
- Strata: Niko1221 and contributors, MIT. Used as-is for Qwen3.8-Flash-Next.
- PolyStrata: VecSzn and contributors, MIT. We reuse the *idea* of a
  multi-family adapter over a shared RAM-arena / VRAM-cache / doorbell
  runtime. We do not vendor PolyStrata kernels.
- Community GGUFs cited above: their own cards (MIT unless noted). REAP50 is
  a third-party prune, not an official Xiaomi release.
- llama.cpp / ggml: ggml-org.

Nothing in Mosaic changes those licenses.
