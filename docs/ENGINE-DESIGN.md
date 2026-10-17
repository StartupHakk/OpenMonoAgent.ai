# Mosaic MoE engine: design

OpenMono today talks to **Strata** for Qwen3.8-Flash-Next and to **llama.cpp**
for everything else. Strata is fast on a 24 GB card plus 64 GB RAM, and it is
tied to one architecture. This document is the plan for **Mosaic**: a
Strata-style runtime that keeps expert weights in RAM/VRAM after startup
(never NVMe in the hot path) and plugs a new model family in through an
adapter rather than a fork of the kernels.

Milestone 1 (this branch): the design, a RAM-resident default for the
existing Strata integration on boxes where it fits, and a CPU-testable
engine scaffold with the first non-Qwen3.8 adapter (GLM-4.5-Air).

## 1. How Strata works

Upstream: [Niko1221/Strata](https://github.com/Niko1221/Strata) (MIT). OpenMono
does not vendor it; `scripts/strata.sh` clones it to `~/strata` and drives
`./setup.sh`.

Qwen3.8-Flash-Next is a 125B-total / 6B-active MoE: 48 layers, 512 routed
experts per MoE layer, 10 routed + 1 shared expert per token, plus a ~51B
n-gram per-layer token embedding (PLE) and an MTP draft head. ISTA-DASLab
GSQ-RCO quants at about 2 to 3.3 bpw are repacked by `tools/strata_pack.py`
into Strata's native pack.

Kitchen-counter split (from Strata `docs/HOW_IT_WORKS.md` and `DETAILS.md`):

| Where | What |
|---|---|
| **VRAM** | Attention and DeltaNet mixers, gated-residual weights, routers, shared experts, output head, MTP draft layer, KV cache (from 64k only the hot part), and an **adaptive expert cache** in leftover VRAM |
| **RAM** | All routed experts, page-locked (the **arena**). CPU computes misses **in place** (AVX-512 / AVX2 / ggml i-quants) while the GPU runs hits |
| **SSD** | The ~26.8-28.8 GB n-gram PLE table. Default `--ple-io direct` reads rows unbuffered every token. `--ple-io ram` loads the table once. `--ple-io mmap` uses the page cache without locking |

A **GPU doorbell** writes the chosen expert ids into a host-visible buffer.
Cached ids run on the GPU; misses are DMA'd or computed on the CPU from the
arena. Prefill is chunked (up to 8,192 tokens) with the next layer's experts
streaming over PCIe during attention. MTP speculative decoding is about 1.6
to 1.8x (Strata: 2.4-3.2 tokens per pass). Reported about 95 tok/s on an RTX
3090 24 GB + 64 GB DDR4 at IQ3_S (community / README ballpark 100-140 tok/s
on a 3090 at lighter quants).

`--family qwen36` is a second path in current Strata for Qwen3.6-35B-A3B.
The maintainer has said the Flash-Next kernels and pack will not grow to
DeepSeek / GLM and similar (issues #310, #479). That is why OpenMono needs
its own engine rather than waiting on Strata.

### 1.1 Model-specific vs generic

**Generic (Mosaic runtime should own these):**

- RAM expert arena, loaded once, no hot-path file reads
- Adaptive VRAM expert cache (copy of hot experts; does not shrink the arena)
- Doorbell: router produces expert ids, host splits hit vs miss
- Prefill chunking and PCIe overlap (later)
- Speculative decode loop that *calls* a family drafter
- OpenAI-compatible HTTP in front, same as Strata / llama-server
- Hardware gate: refuse a plan that would read weights from SSD per token

**Family-specific (adapters):**

- Checkpoint loader (GGUF / safetensors / native pack)
- Packer (how one expert blob is laid out and quantized)
- Router (softmax top-k vs sigmoid / grouped / shared-expert rules)
- Attention / mixer (standard GQA, MLA, Gated DeltaNet, linear attention)
- Drafter (MTP head, draft model file, or none)
- Extra tables that are not routed experts (Flash-Next PLE, vision projector)

PolyStrata ([VecSzn/PolyStrata](https://github.com/VecSzn/PolyStrata), MIT)
already split this way in practice: Strata's engine unchanged for
Qwen3.8-Flash-Next, plus a second `strata-poly` engine for GLM 5.3 Flash,
DeepSeek V4 Flash, LFM2.5, Qwen3.6, Ornith, Gemma 4, Tiel Coder, MiniCPM-V.
Mosaic should learn from that split and from llama.cpp `--n-cpu-moe` / `-ot`,
and must not copy non-MIT code. We are not vendoring PolyStrata.

## 2. Mosaic architecture

```
                    OpenMono agent (OpenAiCompatClient)
                                    |
                                    | HTTP /v1/chat/completions
                                    v
                    MosaicProvider  (llm.endpoint, default :8090)
                                    |
                    scripts/mosaic.sh   (not the installer default)
                                    v
                    Mosaic runtime (shared)
        +-----------+---------------+----------------+
        | RAM arena | VRAM cache    | doorbell       |
        | experts   | hot copies    | expert id list |
        +-----------+---------------+----------------+
                                    |
                    FamilyAdapter (pluggable)
        loader | packer | router | attention | drafter
```

Language for this milestone: **Python 3, stdlib only**, under `engine/`.
That matches Strata's packer tools and runs on the Cloud Agent VM. Production
kernels should move to C++/CUDA (or reuse llama.cpp / ggml pieces, MIT) once
the contracts are stable. The OMOS pack (`b'OMOS'`) is a stand-in for a
native expert arena, not a competitor to GGUF on disk.

### 2.1 Adapter interface

`engine/openmono_engine/adapters/base.py` (`FamilyAdapter`):

- `spec`: layer count, expert count, top-k, shared experts, mixer kind, PLE/MTP flags
- `route(hidden, logits) -> RoutingDecision`
- `pack_layer_experts(...)` / attention stub / `draft(...)` / SwiGLU mix

Registry (`AdapterRegistry`) ships:

| family_id | Role in this milestone |
|---|---|
| `glm45_air` | **First non-Qwen3.8 adapter.** GLM-4.5-Air, standard attention + MoE |
| `qwen38_flash_next` | Spec only. Production serving stays on upstream Strata |

Adding Qwen3.6, Gemma 4, LFM2.5, GLM 5.3, DeepSeek V4 later means a new
adapter module plus tests with tiny synthetic weights. It must not require
editing the arena, cache, or doorbell.

### 2.2 Hard constraint: no SSD in the hot path

Allowed: read the pack (or GGUF) **once at `load_pack`**.

Forbidden: `mmap`/`pread` of expert payloads or embedding tables on each
token. Mosaic counts pack blob reads and raises `HotPathDiskRead` if the
count exceeds 1 after load. Tests unlink the pack file and keep generating.

Strata's `--ple-io direct` violates this for Flash-Next. OpenMono's Strata
wrapper now applies `--ple-io ram` when MemTotal can hold experts + PLE +
8 GiB OS. See section 5.

### 2.3 Plug-in to OpenMono

**Installer (`scripts/install.sh`):** unchanged default. Non-interactive
Linux setup still picks Qwen on llama.cpp. Strata is still opt-in
(`--model=flash-next` / `OPENMONO_INFERENCE_BACKEND=strata`) and still
gated on Linux + NVIDIA + 48 GB-class RAM (~44 GiB `MemTotal`). Mosaic is
**not** a `prompt_model_choice` option yet.

**Strata wrapper (`scripts/strata.sh`):** on install/start, runs
`python3 -m openmono_engine.strata_plan` and, when the plan says so, sets
`--family` / `--model` / `--ple-io ram` on the Strata run config. Existing
`STRATA_FAMILY` / `STRATA_MODEL` / `STRATA_PLE_IO` env vars win.

**Provider registry (`ProviderRegistry`):** `MosaicProvider` (`name=mosaic`,
default `http://localhost:8090`, model `GLM-4.5-Air`). Used only when
`settings.json` has `"providers": { "mosaic": { "active": true, ... } }`.
The default client is still `OpenAiCompatClient` on `llm.*`.

**Ops script:** `scripts/mosaic.sh` (`check`, `test`, `plan`). It does not
download 40 GB weights and is not started by `openmono start`.

## 3. Feasibility: 24 GB VRAM + 64 GB RAM, no SSD hot path

Target box: **24 GB+ VRAM, 64 GB+ RAM**. Expert quants around **2 to 3.5 bpw**.
File sizes are Hugging Face tree/sibling bytes (1024^3 GiB) fetched
**2026-10-17**. RAM-resident column is file (or Strata expert+PLE) plus 8 GiB
OS/runtime. No Nemotron models.

| Model | Quant (HF repo) | File GiB | Active | Fits 24/64 no SSD? | Why |
|---|---|---|---|---|---|
| **GLM-4.5-Air** | UD-IQ2_XXS (`unsloth/GLM-4.5-Air-GGUF`) | **39.75** | 12B of 106B | **Yes, best large-MoE fit** | Too big for 24 GB VRAM, fits 64 GB RAM. No PLE table. Mosaic's first adapter. IQ3_XXS is 47.91 GiB (still RAM-ok, tighter). |
| Qwen3.6-35B-A3B | UD-Q3_K_M (`unsloth/Qwen3.6-35B-A3B-GGUF`) | 15.46 | 3B of 35B | Yes, but whole file also fits VRAM | OpenMono CPU default is UD-Q4_K_XL **20.82 GiB** (catalog still says ~17.6 GB). llama.cpp on 24 GB does not need CPU-MoE. Hybrid DeltaNet is family-specific. |
| Gemma 4 26B-A4B | UD-Q3_K_M (`unsloth/gemma-4-26B-A4B-it-GGUF`) | 11.85 | ~4B of 26B | Yes, fits VRAM whole | PolyStrata `gemma4`. Extra 0.43-0.80 GB MTP file. |
| LFM2.5-24B-A2B | UD-Q4_K_XL (`unsloth/LFM2.5-24B-A2B-GGUF`) | 13.86 | 2B of 24B | Yes, fits VRAM whole | Tiny KV. License: commercial use only under $10M revenue. |
| GLM-4.7-Flash | Q4_K_M (`unsloth/GLM-4.7-Flash-GGUF`) | 17.05 | ~3B class | Yes at Q3/Q4 | Not GLM 5.3 Flash. Likely fits 24 GB VRAM whole. |
| Qwen3-30B-A3B | Q3_K_M (`unsloth/Qwen3-30B-A3B-GGUF`) | 13.70 | 3B of 30B | Yes, fits VRAM whole | Older than Qwen3.6. |
| Qwen3.8-Flash-Next | IQ2_XS (ISTA-DASLab GSQ-RCO) | 36.53+26.82=63.35 | 6B of 125B | Partial | Experts-in-RAM yes. PLE-in-RAM no (36+27+8=71 GiB). Stays on Strata. |
| Qwen3.8-Flash-Next Coder | IQ1_M | 27.58+26.82=54.40 | 6B, 256 experts | **Yes with `--ple-io ram`** | OpenMono 64 GB Strata default. Weaker outside code/CJK. |
| GLM 5.3 Flash | 3.0bit / IQ2 / REAP50 (PolyStrata) | 79-114 | large | **No** | PolyStrata: 32 GB VRAM + 96 GB RAM. |
| DeepSeek V4 Flash | UD-IQ2_XXS (PolyStrata) | 91 | large | **No** | PolyStrata: 32 GB VRAM + 72 GB RAM. |
| Qwen3-235B-A22B | UD-Q2_K_XL | 81.97 | 22B of 235B | **No** | Needs ~90 GiB RAM-resident. |
| DeepSeek-V3.1 | UD-IQ1_S (4 shards) | ~180 | 37B of 671B | **No** | Server-class even at 1-2 bpw. |

**Best fit for Mosaic's stated job** (large MoE on 24/64, no SSD): **GLM-4.5-Air
UD-IQ2_XXS**. That is the first adapter.

**Best fit that OpenMono already downloads:** Qwen3.6-35B-A3B. On 24 GB, plain
llama.cpp can hold it; Mosaic is optional there.

## 4. Honest comparison with llama.cpp CPU-MoE offload

llama.cpp `--n-cpu-moe N` and `-ot` tensor overrides already offload expert
tensors to system RAM and keep attention on the GPU. OpenMono's Linux CPU
path runs Qwen3.6-35B-A3B entirely on the CPU (~17-20 tok/s in `docs/MODELS.md`).

| | llama.cpp `--n-cpu-moe` / `-ot` | Strata (Flash-Next) | Mosaic (goal) |
|---|---|---|---|
| Works today in OpenMono | Yes (CPU-full or GPU GGUF) | Yes, Qwen3.8 only | CPU scaffold only |
| Extra families | Many GGUF MoEs | Flash-Next + qwen36 | Adapter per family |
| Adaptive VRAM expert cache | No (layer/tensor static split) | Yes | Yes (CPU stand-in now) |
| Doorbell + CPU in-place misses | No | Yes | Contract + CPU dispatch now |
| Prefill overlapping PCIe | Weaker | Chunked 8k + ring | Later |
| MTP / spec decode | Separate draft model if any | Built-in MTP ~1.7x | Adapter `draft()` |
| SSD hot path | mmap GGUF unless fully loaded | PLE `direct` by default | Forbidden |
| Pack format | GGUF | Strata native pack | OMOS (tests) then GGUF ingest |
| 24 GB, Qwen3.6 15-21 GB | **Whole model in VRAM; this wins** | Optional | Optional |
| 24 GB, GLM-4.5-Air 40 GB | CPU experts via `-ot`; no hot cache | Not supported | **This is the point** |
| 24 GB, Flash-Next 125B | Painful / incomplete vs Strata | **This is the point** | Not replacing Strata yet |

PolyStrata vs llama.cpp b11540 (their README, 8 GB laptop, LFM2.5): llama.cpp
`--n-cpu-moe 25` wrote at 53 tok/s, PolyStrata at 71-73 tok/s and much faster
prefill. On a 32 GB card with the whole model resident, they are close.
Expect Mosaic vs llama.cpp to look the same: **big win only when experts do
not fit in VRAM**.

For OpenMono on a 24 GB + 64 GB box:

1. Keep llama.cpp for dense 27B and for MoEs that fit in 24 GB (Qwen3.6, Gemma 4).
2. Keep Strata for Flash-Next, with PLE in RAM when the box can hold it.
3. Build Mosaic for GLM-4.5-Air-class models that are larger than VRAM and
   smaller than RAM.

## 5. Strata RAM-resident default (existing integration)

`engine/openmono_engine/strata_plan.py` (also used by `scripts/strata.sh`):

| MemTotal (GiB) | Typical DIMM | Plan |
|---|---|---|
| 93+ (96 GB class) | 96 GB | `--family qwen --model IQ3_S --ple-io ram` |
| 73-92 | 80 GB | `--family qwen --model IQ2_XS --ple-io ram` |
| **60-72** | **64 GB** | **`--family coder --model IQ1_M --ple-io ram`** |
| below 60 | 48 GB | do not force `--ple-io ram`; Strata picks a size; PLE stays on SSD |

Coder + PLE + 8 GiB OS = 23+29+8 = 60 GiB, so a 64 GB box that reports 62 GiB
`MemTotal` fits. IQ2_XS + PLE + OS = 73 GiB, which does **not** fit 64 GB.
Forcing IQ2_XS with `--ple-io ram` on 64 GB would swap or OOM. That is why
the 64 GB default is Coder, not IQ2_XS.

Overrides: `STRATA_FAMILY`, `STRATA_MODEL`, `STRATA_PLE_IO`. If you set
`STRATA_FAMILY=qwen STRATA_MODEL=IQ2_XS` on 64 GB, OpenMono keeps that quant
and does **not** add `--ple-io ram` (it would not fit).

## 6. Docs vs installer (Flash-Next offer gate)

Upstream Strata supports NVIDIA **and** AMD, and 32 GB RAM with the Coder
(often in low-RAM mode that maps experts from disk). OpenMono's installer
is stricter, on purpose:

| | Upstream Strata | OpenMono installer (`CAN_STRATA`) |
|---|---|---|
| GPU | NVIDIA 12 GB+ or listed AMD | **NVIDIA only** (CUDA 13 auto-install). AMD HIP is a 10-20 min source build, not wired |
| RAM to *offer* Flash-Next | 32 GB+ (Coder) | **48 GB-class**: `MemTotal >= 44` GiB. 32 GB boxes report ~31 GiB and would stream experts from SSD |
| RAM-resident PLE | opt-in / rotational disks | default when the plan in section 5 fits |

32 GB is not offered because low-RAM mode reads experts from SSD while
decoding, which Spencer forbade. AMD is not offered because OpenMono does
not yet drive the HIP compile path. `docs/STRATA.md` matches the installer
and points at upstream for AMD / 32 GB Coder.

## 7. What was tested vs not

See the PR body and `docs/STRATA.md` checklist. Short version:

**Tested on the Cloud Agent VM (no GPU, no 40 GB weights):**

- `PYTHONPATH=engine python3 -m unittest discover -s engine/tests`
- Packer round-trip, router top-k, arena miss, cache eviction, doorbell hit/miss
- GLM-4.5-Air tiny forward + generate after unlinking the pack file
- Strata RAM-resident planner (64 GB -> Coder+ram, 96 GB -> IQ3_S+ram)
- `dotnet test` (includes a wrapper that runs the Python suite)

**Needs a real GPU box:**

- [ ] Strata install with the new planner on 64 GB NVIDIA: Coder + `--ple-io ram`, `/health`, agent chat
- [ ] Same on 96 GB: IQ3_S + `--ple-io ram`
- [ ] Confirm `nvidia-smi` shows no per-token disk while decoding (PLE ram)
- [ ] 48 GB box: Strata still installs, PLE not forced to ram, no installer break
- [ ] AMD: still not offered by OpenMono; upstream Strata HIP unchanged
- [ ] Mosaic GLM-4.5-Air real UD-IQ2_XXS load (GGUF ingest not in this milestone)
- [ ] llama.cpp `-ot` / `--n-cpu-moe` baseline numbers on the same 24/64 box
- [ ] Dual-box relay / Caddy path: unchanged, re-smoke if touching compose

## 8. Next milestones (not this PR)

1. GGUF ingest into the RAM arena for GLM-4.5-Air IQ2_XXS
2. CUDA doorbell + ggml/CUDA expert GEMM (reuse llama.cpp where it is MIT)
3. Qwen3.6 adapter if we want Mosaic on the model OpenMono already ships
4. Optional installer entry behind `OPENMONO_INFERENCE_BACKEND=mosaic`
5. Only then: GLM 5.3 / DeepSeek V4 on machines with 32 GB VRAM + 96 GB RAM
