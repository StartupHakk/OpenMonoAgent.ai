# AMD Ryzen 9 7940HS / Radeon 780M native profile

An opt-in inference profile for 7940HS mini PCs. It replaces the
`llama-server` container on the box with a native `llama-server` (upstream
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
- [Host settings outside the installer](#host-settings-outside-the-installer)
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
| BIOS UMA carve-out | 512M (`UMA_SPECIFIED`), which leaves 30.1 GiB visible to Linux. Factory default is 6 GiB (24.7 GiB visible); see [Host settings](#host-settings-outside-the-installer) |
| OS | Ubuntu 26.04.1, kernel 7.0, Mesa/RADV 26.0.8 |
| Disk | About 20 GB free needed (15.1 GB model plus the engine) |

The model streams from RAM on every token, so decode speed is set by memory
bandwidth. Flags, threads and batch sizes barely move it (see
[Still being tuned](#still-being-tuned)).

## Recipe

| Piece | Setting | Status |
|---|---|---|
| Model | Inkling 2 Mini **Q4_0** GGUF from [bartowski](https://huggingface.co/bartowski/Inkling-2-Mini-GGUF), 15.1 GB, sha256 `e8a58319...7b9e37` | Proven |
| Engine | [llama.cpp](https://github.com/ggml-org/llama.cpp/releases/tag/b12728) `b12728`, `ubuntu-vulkan-x64` prebuilt, sha256 `99b1a7a5...ba4979` | Proven |
| Backend | Vulkan on Mesa RADV (the distro `mesa-vulkan-drivers`, 26.0.8) with `GGML_VK_ALLOW_GRAPHICS_QUEUE=1` | Proven |
| Slots / context | `--parallel 2`, `-c 131072` (total across slots, so **64K per slot**) | Proven |
| KV cache | `f16` for K and V | Proven |
| Flash attention | `-fa on` | Proven |
| Offload | `-ngl 99` (whole model on the iGPU) | Proven |
| Batch | `-b 2048 -ub 512` | Proven |
| Prompt cache | `-cram 2048` (host prompt cache capped at 2 GiB) | Proven |
| Loading | `--no-host --load-mode mmap` | Proven |
| Reasoning | `--reasoning off`, `reasoning_effort: none` | Proven |
| Kernel | `amdgpu.gttsize=28672 ttm.pages_limit=7340032 ttm.page_pool_size=7340032` (28 GiB GTT) via a GRUB drop-in | Proven |
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

## Host settings outside the installer

Two settings on the reference box are not applied by `install`, because they
need the BIOS or Secure Boot changes:

- **UMA carve-out 512M.** BIOS, iGPU Configuration = `UMA_SPECIFIED`, size
  512M. This frees about 5.5 GiB for Linux and is what makes the 28 GiB GTT
  and 64K per slot fit. On the factory 6 GiB carve-out, install with
  `OMA_GTT_MIB=20480 OMA_CTX=65536` (32K per slot).
- **54 W power limit.** Stock firmware runs at 28 W STAPM. With Secure Boot
  off, the `ryzen_smu` module plus RyzenAdj
  (`--stapm-limit=54000 --fast-limit=64000 --slow-limit=54000`) at boot gave
  +5% served speed (16.8 to 17.7 tok/s at 1 user). 65 W added nothing to
  decode. The chip's own temperature limit is left alone; peak Tctl stayed
  under 80 C.

Without either one the recipe still runs, just with less free RAM or about
5% less speed.

## Results

The results tables below were measured on earlier recipes (llama.cpp
`b12649` Vulkan, `-ub 256`, q8_0 KV, 32K per slot). The current recipe on the
same box: **466/498** on the full set, **17.8 tok/s** at 1 user, **24.0 total**
at 2 users (12.4 each), and 64K per slot at the same speed.

Earlier runs used llama.cpp `b12649` Vulkan with the full GPU offload above,
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

Flags of the recipe at the time (2 slots, 32K each, q8_0 KV, b12649). 40 questions catch a real
drop but cannot separate quants within a point or two.

| Quant | Source | Size | 1 user tok/s | 2 users total (per user) | Score |
|---|---|---|---|---|---|
| UD-Q4_K_XL | Unsloth | 17.6 GB | 13.4 | 18.7 (9.7) | 39/40 |
| UD-Q4_K_M | Unsloth | 16.6 GB | 13.8 | 19.3 (10.0) | 39/40 |
| UD-IQ4_XS | Unsloth | 15.7 GB | 14.1 | 19.7 (10.3) | 38/40 |
| Q4_K_S | bartowski | 16.9 GB | 14.1 | 20.0 (10.3) | 38/40 |
| IQ4_NL | bartowski | 16.1 GB | 14.6 | 20.7 (10.7) | 40/40 |
| **Q4_0** | bartowski | **15.1 GB** | **16.4** | **22.7 (11.8)** | **39/40** |

Q4_0 is about 22% faster than UD-Q4_K_XL at the same score. The full
498-question run confirmed it: Q4_0 scored 464/498 with q8_0 KV and 466/498
with f16 KV, the same as UD-Q4_K_XL (466).

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
- a 600-token answer comes back, with decode tok/s printed (expect about 17.5
  to 18.5 for one user on the full recipe),
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

- **BIOS carve-out.** These boxes ship with a 6 GiB UMA frame buffer. The
  recipe assumes 512M (see [Host settings](#host-settings-outside-the-installer)).
  At 512M and 2 x 64K f16 KV, about 4.5 GiB of RAM is free at idle and 2.5 GiB
  at the worst point of a 60K prompt.
- **Vision is off.** The recipe loads no mmproj. Keep
  `OPENMONO_VISION_ENABLED=0` on agents that use this box.
- **ROCm and Strata fail on the 780M** (see [Recipe](#recipe)). Do not switch
  this box to either.
- **`scripts/opt_7940hs.sh`.** The default setup path can apply its own
  `amdgpu.gttsize=28672` to `/etc/default/grub`. If both are present, this
  profile's drop-in comes later on the kernel command line and its value wins.
  Install warns when it sees that.
- **Context per slot.** `-c` is the total across slots: `-c 131072` with 2
  slots gives each user 64K. **Do not raise it on a 32 GB box.** 96K per slot
  (`-c 196608`) dropped under 2 GiB free on a 60K prompt, and 128K per slot
  (`-c 262144`) ran the kernel out of memory while loading (panic "System is
  deadlocked on memory", the box needed a power cycle). The GPU buffers are
  pinned, so the OOM killer cannot free them. 3 slots at 32K each
  (`--parallel 3 -c 98304`) works: 27.2 tok/s total, 9.4 per user.
- **Long prompts are slow to read.** Prefill runs at about 145 tok/s at 8K,
  112 at 32K and 88 at 60K, so a 60K prompt takes about 11 minutes before the
  first token. Prompt caching makes follow-up turns fast.
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
| llama.cpp build | upstream `b12728` vs Unsloth `b12649` / `b12675` / `b12676` | b12728 adopted (+18% prefill, +7% decode with the graphics queue). Unsloth newer builds within 1% |
| llama.cpp flags | `-ub` 64 to 2048, `-b`, threads 4/8/16, flash attention off | `-ub 512` adopted; 1024 within 1%; threads and `-b` no change; FA off costs decode |
| Vulkan env | `GGML_VK_ALLOW_GRAPHICS_QUEUE=1`, `GGML_VK_DISABLE_COOPMAT`, `GGML_VK_DISABLE_INTEGER_DOT` | graphics queue adopted (+7% decode); the two DISABLE flags cut prefill 12 to 20% |
| Mesa / RADV | Mesa 26.3.1 RADV built from source, through `VK_DRIVER_FILES` | 465/498, +2% served: under the 3% bar, not adopted |
| Kernel | mainline 7.3.6 vs 7.0.0-44 | under 1%, not adopted |
| KV cache | q8_0 vs f16 | f16 adopted (466 vs 464/498, same speed, about 1 GiB more RAM) |
| Context | 32K, 64K, 96K, 128K per slot | 64K adopted; 96K and 128K do not fit 32 GB (see [Caveats](#caveats)) |
| Power | RyzenAdj 28 / 45 / 54 / 65 W | 54 W kept (+5%); decode flat past 45 W |
| Reasoning | Inkling 2 reasoning on (low / medium effort) | pending |

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
