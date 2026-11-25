# AMD Ryzen 9 7940HS / Radeon 780M native profile

An opt-in inference profile for 7940HS mini PCs. It replaces the
`llama-server` container on the box with a native `llama-server` (Unsloth
llama.cpp, Vulkan on Mesa RADV) run by systemd on the usual inference port
(7474), plus the kernel and power settings that made the difference in
testing. Nothing here runs unless you call `oma-7940hs.sh`, and the default
OpenMono install paths (including `scripts/opt_7940hs.sh` and the Prism
Bonsai image) are unchanged.

Target: one box serving 1 to 2 users with the best accuracy and speed this
iGPU can give. Every number below was measured on one reference box over
three days of testing (November 2026).

- [Hardware](#hardware)
- [Recipe](#recipe)
- [Results](#results)
- [Install](#install)
- [Verify](#verify)
- [Rollback](#rollback)
- [Caveats](#caveats)
- [Still being tuned](#still-being-tuned)
- [Adding a tweak](#adding-a-tweak)
- [Files](#files)

## Hardware

| | Reference box |
|---|---|
| CPU / iGPU | AMD Ryzen 9 7940HS (8 cores, 16 threads), Radeon 780M (RDNA3, gfx1103) |
| RAM | 2x 16 GB DDR5-5600 SO-DIMM (the 7940HS maximum), dual channel |
| BIOS UMA carve-out | 6 GiB, which leaves 24.7 GiB visible to Linux |
| OS | Ubuntu 26.04.1, kernel 7.0, Mesa/RADV 26.0.8 |
| Disk | About 20 GB free needed (15.1 GB model plus the engine) |

The model streams from RAM on every token, so decode speed is set by memory
bandwidth. Flags, threads and batch sizes barely move it (see
[Still being tuned](#still-being-tuned)).

## Recipe

| Piece | Setting | Status |
|---|---|---|
| Model | Inkling 2 Mini **Q4_0** GGUF from [bartowski](https://huggingface.co/bartowski/Inkling-2-Mini-GGUF), 15.1 GB, sha256 `e8a58319...7b9e37` | Proven |
| Engine | [Unsloth llama.cpp](https://github.com/unslothai/llama.cpp/releases/tag/b12649-mix-c7059d6) `b12649-mix-c7059d6`, `linux-x64-vulkan` prebuilt, sha256 `debe9f92...2abee` | Proven |
| Backend | Vulkan on Mesa RADV (the distro `mesa-vulkan-drivers`) | Tuning |
| Slots / context | `--parallel 2`, `-c 65536` (total across slots) | Proven |
| KV cache | `q8_0` for K and V | Proven |
| Flash attention | `-fa on` | Proven |
| Offload | `-ngl 99` (whole model on the iGPU) | Proven |
| Batch | `-b 2048 -ub 256` | Tuning |
| Loading | `--no-host --load-mode mmap` | Proven |
| Reasoning | `--reasoning off`, `reasoning_effort: none` | Proven |
| Kernel | `amdgpu.gttsize=20480 ttm.pages_limit=5242880 ttm.page_pool_size=5242880` (20 GiB GTT) via a GRUB drop-in | Tuning |
| CPU power | governor, EPP and platform profile set to `performance` at boot | Proven |
| GPU access | service user in the `render` and `video` groups, plus a GPU guard | Proven |
| Service | `openmono-7940hs-llama.service` on port 7474, API key from a 0600 key file | Proven |

Full hashes live in [`profile.conf`](profile.conf), which is the single place
the recipe is pinned.

Not used, on purpose:

- **ROCm / HIP.** The Unsloth `rocm-gfx110X` build hung on the first request,
  and with `HSA_OVERRIDE_GFX_VERSION=11.0.0` it crashed in `MUL_MAT`.
- **Strata.** Strata 0.2.20 has no 780M build. Its generic AMD engine caused
  MES queue errors and MODE2 GPU resets on two separate days.

## Results

All runs used llama.cpp `b12649` Vulkan with the full GPU offload above,
thinking off. Speed is decode tokens per second on a 512-token answer: one
user, and the total for two users at once.

### Full sets, 498 questions

Sets: knowledge 80, MMLU 228, GSM8K 100, HumanEval 50, IFEval 40.

| Set | Qwen3.8 27B Q4_K_M + MTP | Qwen3.8 27B Q4_K_XL + MTP | Inkling 2 Mini Q4_K_XL |
|---|---|---|---|
| Knowledge (80) | 79 | 79 | 80 |
| MMLU (228) | 209 | 210 | 204 |
| GSM8K (100) | 96 | 95 | 97 |
| HumanEval (50) | 47 | 49 | 47 |
| IFEval (40) | 36 | 36 | 38 |
| **Total (498)** | **467 (93.8%)** | **469 (94.2%)** | **466 (93.6%)** |
| 1 user tok/s | 10.0 | 9.8 | 13.7 |
| 2 users, total tok/s | 16.0 | 15.6 | 18.9 |

All three are within 3 answers of each other, which is noise at this size,
so Inkling 2 won on speed (about 40% faster at 1 user). Qwen3.8 rows use the
built-in MTP draft head with `--spec-type draft-mtp`, draft n-max 3, 2 slots
and 32K context for speed. Their accuracy runs used 1 slot, because 2 slots
of Qwen3.8 27B on long work left under 2 GiB of free RAM.

### Inkling 2 Q4 sweep, 40 questions

Same flags as the recipe (2 slots, 64K, q8_0 KV). 40 questions catch a real
drop but cannot separate quants within a point or two.

| Quant | Source | Size | 1 user tok/s | 2 users total (per user) | Score |
|---|---|---|---|---|---|
| UD-Q4_K_XL | Unsloth | 17.6 GB | 13.4 | 18.7 (9.7) | 39/40 |
| UD-Q4_K_M | Unsloth | 16.6 GB | 13.8 | 19.3 (10.0) | 39/40 |
| UD-IQ4_XS | Unsloth | 15.7 GB | 14.1 | 19.7 (10.3) | 38/40 |
| Q4_K_S | bartowski | 16.9 GB | 14.1 | 20.0 (10.3) | 38/40 |
| IQ4_NL | bartowski | 16.1 GB | 14.6 | 20.7 (10.7) | 40/40 |
| **Q4_0** | bartowski | **15.1 GB** | **16.4** | **22.7 (11.8)** | **39/40** |

Q4_0 is about 22% faster than UD-Q4_K_XL at the same score. A full
498-question run of Q4_0 is still pending; if it drops more than a point or
two against UD-Q4_K_XL, the recipe goes back to UD-Q4_K_XL (set
`OMA_MODEL_REPO`, `OMA_MODEL_FILE`, `OMA_MODEL_SHA256` and `OMA_MODEL_SIZE`).

Measured on the live service after the switch: a 600-token answer at
16.8 tok/s, with 13.4 GiB of RAM still free at idle.

### Thermals

Over about 1,200 loaded samples the GPU clock averaged 2,784 of 2,800 MHz
with a peak Tctl of 87.9 C. There is no meaningful throttling, so better
cooling buys little on this box.

## Install

Run as your normal user on the inference box (sudo is used where needed):

```bash
cd ~/openmono.ai            # or wherever OpenMono is installed
scripts/7940hs/oma-7940hs.sh preflight
scripts/7940hs/oma-7940hs.sh install
sudo reboot                 # only needed the first time, for the GTT params
scripts/7940hs/oma-7940hs.sh check
```

`install` is idempotent; re-run it after changing `profile.conf` or env
values. It does, in order:

1. Installs `libvulkan1 mesa-vulkan-drivers vulkan-tools libgomp1 curl` if missing.
2. Downloads the llama.cpp tarball, checks its sha256 and size, and unpacks it
   to `/opt/openmono-7940hs/llama.cpp/<tag>`.
3. Downloads the model (resumable) to `/var/lib/openmono-7940hs/models` and
   checks its sha256. A file that fails the check is deleted and fetched again.
4. Applies each tweak in `tweaks.d/` (groups, GTT drop-in, CPU performance).
5. Writes `/etc/openmono-7940hs/oma.conf` and the key file (see below).
6. Stops the `llama-server` container and sets it to `restart=no` (it is not
   removed), and sets `LLAMA_HOST_NATIVE=1` in `docker/.env`.
7. Installs and enables `openmono-7940hs-llama.service`, and starts it when
   the GTT window is already active.

Options:

| Option | Effect |
|---|---|
| `--yes` | No prompts |
| `--force` | Continue on a CPU that is not a 7940HS, or a failed preflight |
| `--skip-model` | Do not download the model (use one already at `OMA_MODEL_DIR`) |
| `--skip-tweak ID` | Skip one tweak (`gpu-access`, `gtt`, `cpu-performance`); repeatable |

Env overrides: any `OMA_*` value in `profile.conf` (for example
`OMA_MODEL_DIR=/data/models` or `OMA_GTT_MIB=22528`), plus
`OMA_SERVICE_USER` (default: the invoking user) and `OPENMONO_HOME`.

**API key.** The service requires the same bearer key as the rest of
OpenMono. Install takes `OMA_API_KEY` if set, else `llm.api_key` from
`~/.openmono/settings.json`, else `LLAMA_API_KEY` from `docker/.env`, else
generates one and stores it as `LLAMA_API_KEY`. It is written to
`/etc/openmono-7940hs/api-keys` (mode 0600, owned by the service user) and
read by `llama-server --api-key-file`. It is never printed. After changing the
key with `openmono config set llm.api_key ...`, run
`scripts/7940hs/oma-7940hs.sh sync-key`.

**OpenMono CLI.** With `LLAMA_HOST_NATIVE=1`, `openmono start` and
`openmono agent` treat the host service as the backend: they never start the
container, the agent container reaches the server through
`host.docker.internal`, and the Caddy gateway is layered with
`docker-compose.strata.yml` so it upstreams to the host as well.

**Tuning a value.** Edit `/etc/openmono-7940hs/oma.conf`, then
`sudo systemctl restart openmono-7940hs-llama` and run the check. Re-running
`install` regenerates that file from `profile.conf` plus env, so put lasting
changes in env or `profile.conf`.

## Verify

```bash
scripts/7940hs/oma-7940hs.sh status     # service, health, GTT, GPU memory, tweaks
scripts/7940hs/oma-7940hs.sh check      # add --restart to restart first
```

`check` (also `scripts/7940hs/oma-check.sh`) passes when:

- `/health` answers on the port,
- a request with no key gets HTTP 401 and one with the key gets 200,
- a 600-token answer comes back, with decode tok/s printed (expect about 16
  to 17 for one user),
- free RAM stays above 2 GiB during the request,
- VRAM plus GTT in use is at least `OMA_GPU_GUARD_MIN_MIB` (8000).

The service also has a **GPU guard**. Before launch it fails if the service
user cannot open `/dev/dri/renderD*`. After launch (`ExecStartPost`) it waits
for `/health` and fails the unit when VRAM plus GTT in use is under 8000 MiB.
This catches the silent CPU fallback seen in testing (3 to 4 tok/s with the
GPU idle) when the service user was not in the `render` group.

Logs: `journalctl -u openmono-7940hs-llama -f`.

## Rollback

```bash
scripts/7940hs/oma-7940hs.sh uninstall           # same as: rollback
sudo reboot                                      # returns GTT to the default window
```

Uninstall undoes exactly what install recorded in
`/var/lib/openmono-7940hs/state.env`: it stops and removes the service,
reverts the tweaks in reverse order (removes the GRUB drop-in and runs
`update-grub`, restores the governor, EPP and platform profile values found at
install time, removes only the groups install added), deletes the config and
key file, removes `LLAMA_HOST_NATIVE` from `docker/.env`, and restores the
container's restart policy (starting it again if it was running). The model is
kept unless you pass `--purge-model`; `--keep-llama` keeps the engine.
Packages are left installed.

Any system file the profile overwrites is copied to
`/var/lib/openmono-7940hs/backups` first and put back on uninstall. The GTT
setting is a separate drop-in, so `/etc/default/grub` itself is never edited.

## Caveats

- **BIOS carve-out.** These boxes ship with a 6 GiB UMA frame buffer and a
  limited BIOS. Linux sees 24.7 GiB, which is why 2 slots of a 27B dense
  model are tight while Inkling 2 Q4_0 at 2 slots and 64K leaves 11 to 13 GiB
  free. Different carve-outs have not been tested yet.
- **Vision is off.** The recipe loads no mmproj. Keep
  `OPENMONO_VISION_ENABLED=0` on agents that use this box.
- **ROCm and Strata fail on the 780M** (see [Recipe](#recipe)). Do not switch
  this box to either.
- **`scripts/opt_7940hs.sh`.** The default setup path can apply its own
  `amdgpu.gttsize=28672` to `/etc/default/grub`. If both are present, this
  profile's drop-in comes later on the kernel command line and its value wins.
  Install warns when it sees that.
- **Context per slot.** `-c 65536` with 2 slots gives each user about 32K of
  context.
- **Thinking off.** Inkling 2 still writes a short `reasoning_content` with
  reasoning off; the scored runs had the same behavior.
- **`openmono status` / `openmono logs`** still describe the container. Use
  `oma-7940hs.sh status` and `journalctl` for the native service.
- **Tested once.** The profile scripts were linted and dry-run tested, and the
  recipe itself ran for days on the reference box, but this installer has not
  yet been run end to end on a fresh box.

## Still being tuned

Each line is a test in progress. The keep rule is a gain of 3% or more on
decode, or a clear prefill gain with decode unchanged, with output still
correct. Winners move into `profile.conf` (marked Proven) or become a new
tweak.

| Area | What is being tried | Result so far |
|---|---|---|
| llama.cpp build | upstream ggml-org `b12728` Vulkan; Unsloth `b12675` / `b12676` | b12728: +9% prefill, +1.6% decode (llama-bench). Unsloth newer builds: within 1% |
| llama.cpp flags | `-ub` 64 to 1024, `-b`, threads 4/8/16, flash attention off | decode stays at about 17.3 tok/s; `-ub` 512 to 1024 adds about 3 to 4% prefill; FA off costs decode |
| Vulkan env | `GGML_VK_ALLOW_GRAPHICS_QUEUE=1`, `GGML_VK_DISABLE_COOPMAT`, `GGML_VK_DISABLE_INTEGER_DOT` | the two DISABLE flags cut prefill 12 to 20%; graphics queue pending |
| Mesa / RADV | Mesa 26.3.1 RADV built from source, side by side through `VK_DRIVER_FILES` | pending |
| Kernel | newer kernel / amdgpu | pending |
| GTT | 20480 MiB now; 22528 is the next step | 20 GiB is enough for this model |
| Power | raise the APU power limits with RyzenAdj (`ryzen_smu` module) | pending |
| BIOS | UMA carve-out size, memory settings | waiting on BIOS screenshots; RAM already runs at 5600 MT/s, the CPU maximum |

Free-form knobs for these tests are already wired in: `OMA_EXTRA_ARGS`
(extra `llama-server` flags), `OMA_ENV_EXTRA` (extra env such as
`GGML_VK_ALLOW_GRAPHICS_QUEUE=1`), `OMA_LLAMA_*` (another engine build),
`OMA_BATCH` / `OMA_UBATCH` and `OMA_GTT_MIB`.

## Adding a tweak

System-level changes live in `tweaks.d/`, one file each, run in name order.
Copy [`tweaks.d/_TEMPLATE.sh.example`](tweaks.d/_TEMPLATE.sh.example) to
`NN-name.sh` and fill in:

- `TWEAK_ID`, `TWEAK_DESC`, `TWEAK_TAG` (`TUNING` or `PROVEN`), `TWEAK_REBOOT`
- `tweak_check` (return 0 active, 1 not, 2 reboot pending)
- `tweak_apply`, idempotent: call `backup_file` before changing a system file
  and `state_set_once` to record original values
- `tweak_revert`, which restores what apply changed

`install` picks the file up, `status` and `tweaks` list it, `uninstall` reverts
it, and `--skip-tweak ID` skips it. Add a row to the tables above with the
before and after numbers.

## Files

| Path | Purpose |
|---|---|
| `oma-7940hs.sh` | Entry point: `preflight`, `install`, `status`, `check`, `tweaks`, `sync-key`, `uninstall` |
| `profile.conf` | The pinned recipe (URLs, sha256, flags, paths) |
| `oma-check.sh` | Health, auth, tok/s, RAM and GPU check |
| `lib/common.sh` | State, backup and restore, verified downloads, GPU memory readers |
| `libexec/launch-llama.sh` | Service `ExecStart`: reads `oma.conf`, checks the render node, execs `llama-server` |
| `libexec/gpu-guard.sh` | Service `ExecStartPost`: fails the unit on CPU fallback |
| `libexec/cpu-perf.sh` | Sets governor, EPP and platform profile |
| `systemd/*.service.in` | Unit templates |
| `tweaks.d/` | System tweaks, one per file |

Installed on the box: `/opt/openmono-7940hs` (engine and helper scripts),
`/etc/openmono-7940hs` (config and key file), `/var/lib/openmono-7940hs`
(model, state and backups), `/etc/systemd/system/openmono-7940hs-*.service`
and `/etc/default/grub.d/90-openmono-7940hs.cfg`.
