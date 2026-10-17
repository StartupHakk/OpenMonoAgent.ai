# OpenMono Mosaic engine

CPU-testable core of a Strata-style Mixture-of-Experts runtime that is not
tied to Qwen3.8-Flash-Next. Design: [docs/ENGINE-DESIGN.md](../docs/ENGINE-DESIGN.md).

## What this milestone is

- Family adapters (loader / packer / router / attention / drafter hooks)
- Shared RAM expert arena, VRAM expert-cache stand-in, GPU doorbell contract
- OMOS pack format that is loaded once at startup
- First non-Qwen3.8 adapter: **GLM-4.5-Air** (`glm45_air`)
- Tests that run on this VM with tiny synthetic weights (no GPU, no multi-GB files)

## What it is not

- Not a replacement for `scripts/strata.sh` yet
- Not CUDA / HIP kernels
- Not GGUF ingest of the real 39.75 GB GLM-4.5-Air file
- Not wired into `openmono setup` as a default (that would break existing paths)

## Tests

From the repo root:

```bash
PYTHONPATH=engine python3 -m unittest discover -s engine/tests -v
```

`dotnet test` also runs these via `OpenMono.Tests.Engine.EnginePythonTests`.
