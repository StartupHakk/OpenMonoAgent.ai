# Third party binaries (Windows)

Fetched at build time by windows/build/fetch-*.ps1, never committed.

- llama.cpp Windows builds (CUDA 12.4, Vulkan, AVX2 CPU) from official
  GitHub releases, pinned in src/OpenMono.Models/models.json with SHA256.
  CUDA runtime DLLs (cublas, cudart) ship beside the CUDA flavor binary.
- ripgrep Windows build (rg.exe), pinned with SHA256, added to the child
  process PATH and the agent environment.
- Portable Node (optional, for MCP and language servers), version pinned.
- VC++ redist bootstrapper check at install time (not bundled drivers).

Record exact versions and checksums per release in SBOM.md.
