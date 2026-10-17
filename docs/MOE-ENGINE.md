# Mosaic MoE engine

Mosaic is OpenMono's host-native MoE runtime for families that do not fit in
24 GB VRAM as a single GGUF and must not page weights from SSD while decoding.

Qwen3.8-Flash-Next stays on upstream **Strata** (`scripts/strata.sh`). Mosaic
does not wrap, fork, or recompile those kernels. MiMo-V2.6-Flash is the first
Mosaic family. Other MoE families plug in as adapters.

Background and the 24+64 budget: [MIMO.md](MIMO.md).

## Why a new engine

Strata's pack format and GPU kernels are Qwen3.8-Flash-Next specific (upstream
issues #310, #479). PolyStrata (MIT, [VecSzn/PolyStrata](https://github.com/VecSzn/PolyStrata))
already split "one engine, many families" from that constraint: Qwen stays on
the Strata engine; GLM / DeepSeek / others run on a second engine with a
shared RAM-resident expert arena. Mosaic follows that split, inside OpenMono,
without vendoring either tree.

llama.cpp `--n-cpu-moe --no-mmap` is the speed baseline. It can pin weights,
but it has no doorbell and no adaptive VRAM expert cache. Mosaic's job is
that missing path for MiMo-shaped models.

## Shared runtime

```
  pack file  --1x-->  RAM arena (mlock, all surviving routed experts)
  (startup)                    |
                               | DMA / in-place CPU
  GPU doorbell  ------------>  VRAM expert cache (adaptive, spare VRAM)
  (expert ids)                 |
                               +--> family adapter: loader, packer,
                                    router, attention, drafter
```

Rules the runtime enforces:

- After `RamArena.PinFromDisk`, `Get` never opens a file and never maps one.
  `MOSAIC_ALLOW_MMAP=0` is the only supported mode.
- The doorbell is a host-visible `int32` ring of expert ids for the current
  token (or prefill chunk). The GPU writes; the host dispatches.
- Cache hit: expert GEMM on GPU. Miss: copy from the arena (PCIe DMA) or run
  the CPU reference in place if the DMA queue is full.
- Prefill is chunked. Decode can call the family drafter (MTP / DFlash) when
  the adapter says one exists.

This milestone implements the contracts and a CPU reference on toy tensors.
CUDA kernels, real pack conversion, and the HTTP server are the GPU-box
follow-up.

## Adapter interface

`IModelFamilyAdapter` (C#, `OpenMono.Moe`):

| Port | Responsibility |
|---|---|
| **Loader** | Map a on-disk snapshot (safetensors, GGUF, or already-packed OMS1) to named tensors. Startup only. |
| **Packer** | Quantize + repack experts into OMS1 (OpenMono Mosaic pack v1). Role-aware bit assignment. |
| **Router** | From hidden states, emit top-k expert ids + weights. Writes the doorbell. |
| **Attention** | Family-specific hybrid / GQA / SWA vs GA. Owns KV layout. |
| **Drafter** | Optional MTP / speculative head. MiMo: DFlash-style 7-token draft. Qwen adapter: `null` (Strata owns it). |

`AdapterRegistry` looks up a family by id (`mimo-v2.6-flash`,
`qwen3.8-flash-next`). Adding GLM or DeepSeek later is a new adapter class
plus a catalog row. The arena, cache, and doorbell stay.

### Families in this milestone

| Id | Adapter | Runtime |
|---|---|---|
| `mimo-v2.6-flash` | `MimoV26FlashAdapter` | Mosaic (this engine) |
| `qwen3.8-flash-next` | `Qwen38FlashNextAdapter` | **Passthrough.** Describes the model so the registry is complete. Execution stays on Strata. `OwnsRuntime` is false; `AdapterRegistry.RequireOwned` throws `NotSupportedException`. |

## OMS1 pack (v1)

Little-endian, one expert blob:

```
magic[4] = "OMS1"
version  u16 = 1
family   u16          // 1 = mimo-v2.6-flash
layer    u16
expert   u16
role     u8           // 0=gate, 1=up, 2=down, 3=other
bpw_x10  u8           // 20 = 2.0 bpw, 33 = 3.3, 80 = 8.0
block    u16          // quant group size
rows     u32
cols     u32
bytes    u32
payload  [bytes]      // packed codes + per-block scales (fp16)
```

The arena concatenates blobs and keeps an index `(family, layer, expert,
role) -> offset`. A later GPU kernel can consume this without a second
conversion step.

## Recommended MiMo pack (quality-max that fits)

See [MIMO.md](MIMO.md) config A. Encoded as `MosaicRecommendedConfig.MimoFlashRl`:

- Keep 128 of 256 experts (REAP / usage mask, never random).
- Q8: attention, embeddings, lm_head, router, dense layer 0, MTP/DFlash.
- Q3_K (or 3.0-3.3 bpw block quant): last MoE layer, every expert down-proj.
- Q2_K (or 2.0 bpw): remaining expert gate/up.
- Context 131,072, KV Q8.
- No mmproj.

`MemoryBudget.FitsConsumerBox` is the unit-tested gate: weights + KV +
reserves must stay inside 20 GiB VRAM / 57 GiB RAM / 77 GiB combined.

## Agent-facing surface

Mosaic speaks OpenAI-compatible `/v1` on `127.0.0.1:8090` (planned; the
scaffold does not bind a port on this VM). The .NET agent already talks to
that shape via `MosaicProvider`. Chat template, sampling, and reasoning
mapping live in `ModelReasoningProfile` (`mimo`) and the `mimo` model preset
(temperature 1.0, top_p 0.95).

## Needs a real GPU box

Listed in [MIMO.md](MIMO.md). The short version: convert a real REAP50 (or
better) pack, mlock it, run the doorbell, and actually score SWE-Verified
before claiming a win over Strata Coder.
