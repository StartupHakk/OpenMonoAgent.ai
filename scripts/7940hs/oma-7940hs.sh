#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai: AMD Ryzen 9 7940HS / Radeon 780M native inference profile
#
# Opt-in. Replaces the llama-server container on this box with a native
# llama-server (Unsloth llama.cpp Vulkan build) run by systemd on the usual
# inference port, tuned from multi-day testing on a 7940HS mini PC. Nothing
# here runs unless you call it; other hardware paths are unchanged.
# See scripts/7940hs/README.md.
#
# Usage:
#   oma-7940hs.sh preflight              # read-only hardware and software checks
#   oma-7940hs.sh install [--yes] [--force] [--skip-model] [--skip-tweak ID]...
#   oma-7940hs.sh status                 # service, tweaks, GTT, GPU memory
#   oma-7940hs.sh check [--restart]      # health, 401, tok/s, RAM (oma-check.sh)
#   oma-7940hs.sh tweaks                 # list tweaks and whether each is active
#   oma-7940hs.sh sync-key               # re-copy the OMA API key into the key file
#   oma-7940hs.sh uninstall [--yes] [--purge-model] [--keep-llama]
#   oma-7940hs.sh rollback               # same as uninstall
#
# Env overrides: any OMA_* value in profile.conf, plus
#   OMA_SERVICE_USER  user the server runs as (default: the invoking user)
#   OMA_API_KEY       key to require (default: settings.json llm.api_key,
#                     then LLAMA_API_KEY in docker/.env, else generated)
#   OPENMONO_HOME     OpenMono install dir (default: this checkout)
#   OPENMONO_VERBOSE=1  show every command
#
# Exit codes: 0 ok, 1 failed or not on supported hardware, 2 usage error.
# ──────────────────────────────────────────────────────────────────────────────

PROFILE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="${OPENMONO_HOME:-$(cd "$PROFILE_DIR/../.." && pwd)}"
DOCKER_DIR="$REPO_DIR/docker"
DOCKER_ENV_FILE="$DOCKER_DIR/.env"
SETTINGS_FILE="${OPENMONO_SETTINGS:-$HOME/.openmono/settings.json}"

# shellcheck source=../lib/log.sh
source "$PROFILE_DIR/../lib/log.sh"
# shellcheck source=lib/common.sh
source "$PROFILE_DIR/lib/common.sh"
# shellcheck source=profile.conf
source "$PROFILE_DIR/profile.conf"

OMA_SERVICE_USER="${OMA_SERVICE_USER:-${SUDO_USER:-$(id -un)}}"
export PROFILE_DIR OMA_SERVICE_USER

YES=0; FORCE=0; SKIP_MODEL=0; PURGE_MODEL=0; KEEP_LLAMA=0
SKIP_TWEAKS=()
CHECK_ARGS=()

usage() { sed -n '4,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

# ── Small helpers ─────────────────────────────────────────────────────────────

confirm() {
    [[ "$YES" == 1 ]] && return 0
    flush_stdin
    printf "  %s [y/N]: " "$1"
    local a; read -r a
    [[ "$a" =~ ^[Yy]$ ]]
}

dk() {
    if docker info >/dev/null 2>&1; then docker "$@"; else as_root docker "$@"; fi
}

has_docker() { command -v docker >/dev/null 2>&1; }

env_get() {
    [[ -f "$DOCKER_ENV_FILE" ]] || return 0
    local line; line="$(grep -m1 "^${1}=" "$DOCKER_ENV_FILE" 2>/dev/null || true)"
    [[ -n "$line" ]] && printf '%s' "${line#*=}"
    return 0
}

# docker/.env edits keep the file's owner (it belongs to the OpenMono user
# even when this script runs under sudo) and its 0600 mode.
_env_write() {
    local tmp="$1" owner
    owner="$(stat -c '%u:%g' "$DOCKER_ENV_FILE" 2>/dev/null || echo "")"
    mv "$tmp" "$DOCKER_ENV_FILE"; chmod 0600 "$DOCKER_ENV_FILE"
    if [[ -n "$owner" && "$(id -u)" -eq 0 ]]; then chown "$owner" "$DOCKER_ENV_FILE"; fi
}

env_set() {
    local key="$1" val="$2" tmp
    [[ -d "$DOCKER_DIR" ]] || return 0
    touch "$DOCKER_ENV_FILE"; tmp="$DOCKER_ENV_FILE.tmp"
    grep -v "^${key}=" "$DOCKER_ENV_FILE" > "$tmp" 2>/dev/null || true
    printf '%s=%s\n' "$key" "$val" >> "$tmp"
    _env_write "$tmp"
}

env_del() {
    local key="$1" tmp
    [[ -f "$DOCKER_ENV_FILE" ]] || return 0
    tmp="$DOCKER_ENV_FILE.tmp"
    grep -v "^${key}=" "$DOCKER_ENV_FILE" > "$tmp" 2>/dev/null || true
    _env_write "$tmp"
}

llama_dir() { printf '%s/llama.cpp/%s' "$OMA_PREFIX" "$OMA_LLAMA_TAG"; }
model_path() { printf '%s/%s' "$OMA_MODEL_DIR" "$OMA_MODEL_FILE"; }
unit_path() { printf '%s/%s' "${OMA_SYSTEMD_DIR:-/etc/systemd/system}" "$OMA_UNIT"; }

# Runs one tweak file's function in a subshell so tweaks can't leak state.
tweak_call() {
    local file="$1" fn="$2"
    (
        # shellcheck source=/dev/null
        source "$file"
        "$fn"
    )
}

tweak_files() {
    local f
    for f in "$PROFILE_DIR"/tweaks.d/[0-9][0-9]-*.sh; do
        [[ -f "$f" ]] && printf '%s\n' "$f"
    done
}

tweak_meta() {
    # shellcheck source=/dev/null
    ( source "$1"; printf '%s|%s|%s|%s' "$TWEAK_ID" "$TWEAK_TAG" "$TWEAK_REBOOT" "$TWEAK_DESC" )
}

tweak_skipped() {
    local id="$1" s
    for s in "${SKIP_TWEAKS[@]+"${SKIP_TWEAKS[@]}"}"; do [[ "$s" == "$id" ]] && return 0; done
    return 1
}

# ── preflight ─────────────────────────────────────────────────────────────────

cmd_preflight() {
    banner "7940HS / Radeon 780M preflight"
    local bad=0 cpu ram_mib vram gtt
    [[ "$(uname -s)" == "Linux" ]] || { err "Linux only"; return 1; }
    cpu="$(grep -m1 '^model name' /proc/cpuinfo | sed 's/.*: //')"
    if [[ "$cpu" == *7940HS* ]]; then ok "CPU: $cpu"
    elif [[ "$FORCE" == 1 ]]; then warn "CPU: $cpu (not a 7940HS, continuing because of --force)"
    else err "CPU: $cpu. This profile is tuned for the Ryzen 9 7940HS (use --force to try anyway)"; bad=1
    fi
    if grep -qw amdgpu /proc/modules 2>/dev/null; then ok "amdgpu kernel driver loaded"
    else err "amdgpu driver not loaded"; bad=1; fi
    if compgen -G "/dev/dri/renderD*" >/dev/null; then ok "render node present"
    else err "no /dev/dri/renderD* node"; bad=1; fi
    if command -v vulkaninfo >/dev/null 2>&1; then
        if vulkaninfo --summary 2>/dev/null | grep -qi 'radv\|780M'; then ok "Vulkan: RADV device visible"
        else warn "vulkaninfo does not list a RADV device (install mesa-vulkan-drivers)"; fi
    else
        info "vulkaninfo not installed yet (install adds vulkan-tools)"
    fi
    ram_mib=$(( $(awk '/MemTotal/{print $2}' /proc/meminfo) / 1024 ))
    vram="$(vram_total_mib)"; gtt="$(gtt_total_mib)"
    info "RAM visible ${ram_mib} MiB, BIOS carve-out (VRAM) ${vram} MiB, GTT ${gtt} MiB"
    (( ram_mib >= 22000 )) || { warn "Under about 22 GiB visible RAM: the 15 GB model plus 64K KV will not fit"; bad=1; }
    if (( gtt < OMA_GTT_MIB - 64 )); then
        info "GTT is below the profile's ${OMA_GTT_MIB} MiB; install adds the kernel params (reboot needed)"
    fi
    local free_gb probe="$OMA_MODEL_DIR"
    while [[ ! -d "$probe" && "$probe" != "/" ]]; do probe="$(dirname "$probe")"; done
    free_gb=$(( $(df -Pk "$probe" 2>/dev/null | awk 'NR==2{print $4}' || echo 0) / 1048576 ))
    if (( free_gb >= 20 )); then ok "Disk: ${free_gb} GB free"
    else warn "Disk: ${free_gb} GB free, need about 20 GB"; bad=1; fi
    if ss -tln 2>/dev/null | grep -q ":${OMA_PORT} "; then
        info "Port ${OMA_PORT} is in use (install hands it over from the llama-server container)"
    fi
    if [[ -f /etc/os-release ]]; then
        # shellcheck source=/dev/null
        info "OS: $(. /etc/os-release; echo "${PRETTY_NAME:-unknown}"), kernel $(uname -r)"
    fi
    if [[ "$bad" == 0 ]]; then ok "Preflight passed"; else err "Preflight found problems"; fi
    return "$bad"
}

# ── install ───────────────────────────────────────────────────────────────────

install_deps() {
    local pkgs=(libvulkan1 mesa-vulkan-drivers vulkan-tools libgomp1 curl ca-certificates)
    local missing=() p
    for p in "${pkgs[@]}"; do
        dpkg-query -W -f='${Status}' "$p" 2>/dev/null | grep -q 'install ok installed' || missing+=("$p")
    done
    if (( ${#missing[@]} == 0 )); then ok "Packages present"; return 0; fi
    info "Installing: ${missing[*]}"
    run as_root apt-get update || die "apt-get update failed"
    run as_root apt-get install -y "${missing[@]}" || die "apt-get install failed"
    ok "Packages installed (kept on uninstall)"
}

install_llama() {
    local dir; dir="$(llama_dir)"
    if as_root test -x "$dir/llama-server" && as_root test -f "$dir/.sha256-ok"; then
        ok "llama.cpp $OMA_LLAMA_TAG already installed"; return 0
    fi
    local dl="$OMA_STATE/downloads"
    as_root install -d -m 0755 -o "$(id -un)" "$dl"
    download_verified "$OMA_LLAMA_URL" "$dl/$OMA_LLAMA_ASSET" "$OMA_LLAMA_SHA256" "$OMA_LLAMA_SIZE" \
        || die "llama.cpp download failed sha256 verification"
    as_root rm -rf "$dir.tmp"
    as_root install -d -m 0755 "$dir.tmp"
    run as_root tar -xzf "$dl/$OMA_LLAMA_ASSET" -C "$dir.tmp" --no-same-owner || die "extract failed"
    as_root test -x "$dir.tmp/llama-server" || die "llama-server not found in $OMA_LLAMA_ASSET"
    printf '%s  %s\n' "$OMA_LLAMA_SHA256" "$OMA_LLAMA_ASSET" | as_root tee "$dir.tmp/.sha256-ok" >/dev/null
    as_root rm -rf "$dir"; as_root mv "$dir.tmp" "$dir"
    as_root ln -sfn "$dir" "$OMA_PREFIX/llama.cpp/current"
    rm -f "$dl/$OMA_LLAMA_ASSET"
    ok "llama.cpp $OMA_LLAMA_TAG installed to $dir"
}

install_model() {
    if [[ "$SKIP_MODEL" == 1 ]]; then
        [[ -f "$(model_path)" ]] || warn "--skip-model: $(model_path) is missing; the service will not start"
        return 0
    fi
    as_root install -d -m 0755 -o "$(id -un)" "$OMA_MODEL_DIR"
    download_verified "$OMA_MODEL_URL" "$(model_path)" "$OMA_MODEL_SHA256" "$OMA_MODEL_SIZE" \
        || die "Model download failed sha256 verification"
    chmod 0644 "$(model_path)"
}

apply_tweaks() {
    local f meta id
    while IFS= read -r f; do
        meta="$(tweak_meta "$f")"; id="${meta%%|*}"
        if tweak_skipped "$id"; then warn "Skipping tweak $id (--skip-tweak)"; continue; fi
        info "Tweak $id: $(cut -d'|' -f4 <<<"$meta")"
        tweak_call "$f" tweak_apply
    done < <(tweak_files)
}

write_conf() {
    local conf
    conf="$(cat <<CONF
# Generated by scripts/7940hs/oma-7940hs.sh install. Read by the llama service.
# Edit values here, then: sudo systemctl restart ${OMA_UNIT}
# Re-running install regenerates this file from profile.conf and the env.
$(for v in OMA_LLAMA_TAG OMA_MODEL_ALIAS OMA_HOST OMA_PORT OMA_PARALLEL OMA_CTX \
           OMA_CACHE_K OMA_CACHE_V OMA_FLASH_ATTN OMA_NGL OMA_BATCH OMA_UBATCH \
           OMA_LOAD_MODE OMA_REASONING OMA_TEMPLATE_KWARGS OMA_EXTRA_ARGS OMA_ENV_EXTRA \
           OMA_GPU_GUARD_MIN_MIB OMA_KEY_FILE OMA_UNIT; do
    printf '%s=%q\n' "$v" "${!v}"
done)
OMA_LLAMA_BIN=$(printf '%q' "$(llama_dir)/llama-server")
OMA_MODEL_PATH=$(printf '%q' "$(model_path)")
CONF
)"
    backup_file "$OMA_ETC/oma.conf"
    if install_text "$OMA_ETC/oma.conf" 0644 "$conf"; then ok "Wrote $OMA_ETC/oma.conf"
    else ok "$OMA_ETC/oma.conf unchanged"; fi
}

resolve_key() {
    local key="${OMA_API_KEY:-}"
    if [[ -z "$key" && -f "$SETTINGS_FILE" ]] && command -v jq >/dev/null 2>&1; then
        key="$(jq -r '.llm.api_key // empty' "$SETTINGS_FILE" 2>/dev/null || true)"
    fi
    [[ -n "$key" ]] || key="$(env_get LLAMA_API_KEY)"
    printf '%s' "$key"
}

write_key() {
    local key; key="$(resolve_key)"
    if [[ -z "$key" ]]; then
        if as_root test -s "$OMA_KEY_FILE"; then ok "Keeping the existing key file"; return 0; fi
        key="$(openssl rand -hex 24 2>/dev/null || head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
        if [[ -d "$DOCKER_DIR" ]]; then
            backup_file "$DOCKER_ENV_FILE"
            env_set LLAMA_API_KEY "$key"
            info "Generated a new API key and stored it as LLAMA_API_KEY in docker/.env"
        else
            info "Generated a new API key (read it with: sudo cat $OMA_KEY_FILE)"
        fi
    fi
    local tmp; tmp="$(mktemp)"; chmod 0600 "$tmp"
    printf '%s\n' "$key" > "$tmp"; unset key
    as_root install -D -m 0600 -o "$OMA_SERVICE_USER" -g "$(id -gn "$OMA_SERVICE_USER")" "$tmp" "$OMA_KEY_FILE"
    rm -f "$tmp"
    ok "API key file $OMA_KEY_FILE (mode 600, owner $OMA_SERVICE_USER)"
}

handover_docker() {
    has_docker || return 0
    if ! dk inspect llama-server >/dev/null 2>&1; then
        detail "No llama-server container; nothing to hand over"
    else
        state_set_once DOCKER_LLAMA_RESTART "$(dk inspect -f '{{.HostConfig.RestartPolicy.Name}}' llama-server 2>/dev/null || echo no)"
        state_set_once DOCKER_LLAMA_RUNNING "$(dk inspect -f '{{.State.Running}}' llama-server 2>/dev/null || echo false)"
        run dk update --restart=no llama-server || warn "Could not change the container restart policy"
        run dk stop llama-server || true
        ok "llama-server container stopped and set to restart=no (it is not removed)"
    fi
    if [[ -d "$DOCKER_DIR" ]]; then
        backup_file "$DOCKER_ENV_FILE"
        state_set_once ORIG_LLAMA_HOST_NATIVE "$(env_get LLAMA_HOST_NATIVE)"
        env_set LLAMA_HOST_NATIVE 1
        ok "docker/.env: LLAMA_HOST_NATIVE=1 (the openmono CLI targets the host server)"
    fi
}

install_service() {
    as_root install -d -m 0755 "$OMA_PREFIX/bin"
    local b
    for b in launch-llama.sh gpu-guard.sh; do
        run as_root install -m 0755 -o root -g root "$PROFILE_DIR/libexec/$b" "$OMA_PREFIX/bin/$b"
    done
    backup_file "$(unit_path)"
    local unit
    unit="$(sed -e "s|@USER@|$OMA_SERVICE_USER|g" \
                -e "s|@GROUP@|$(id -gn "$OMA_SERVICE_USER")|g" \
                -e "s|@PREFIX@|$OMA_PREFIX|g" \
                -e "s|@ETC@|$OMA_ETC|g" \
                -e "s|@PERF_UNIT@|$OMA_PERF_UNIT|g" \
                "$PROFILE_DIR/systemd/openmono-7940hs-llama.service.in")"
    install_text "$(unit_path)" 0644 "$unit" || true
    run as_root systemctl daemon-reload || die "systemctl daemon-reload failed"
    run as_root systemctl enable "$OMA_UNIT" || die "Could not enable $OMA_UNIT"
    ok "Installed and enabled $OMA_UNIT"
}

gtt_ready() {
    local f
    f="$(grep -l '^TWEAK_ID="gtt"' "$PROFILE_DIR"/tweaks.d/[0-9][0-9]-*.sh 2>/dev/null | head -1)"
    [[ -n "$f" ]] || return 0
    tweak_call "$f" tweak_check >/dev/null
}

cmd_install() {
    banner "OpenMono 7940HS native profile: install"
    [[ "$OMA_SERVICE_USER" != "root" ]] || die "Run as your normal user (sudo is used where needed), or set OMA_SERVICE_USER"
    id "$OMA_SERVICE_USER" >/dev/null 2>&1 || die "User $OMA_SERVICE_USER does not exist"
    cmd_preflight || { [[ "$FORCE" == 1 ]] || die "Preflight failed (re-run with --force to override)"; }
    echo ""
    info "This changes system settings: GRUB kernel params (drop-in), CPU power,"
    info "group membership, a systemd service, and stops the llama-server container."
    info "Everything is recorded in $OMA_STATE and undone by: oma-7940hs.sh uninstall"
    confirm "Continue?" || { info "Cancelled"; return 0; }
    as_root true || die "sudo is required"

    local total=7
    step 1 $total "System packages";         install_deps
    step 2 $total "llama.cpp ($OMA_LLAMA_TAG, Vulkan)"; install_llama
    step 3 $total "Model ($OMA_MODEL_FILE)"; install_model
    step 4 $total "System tweaks";           apply_tweaks
    step 5 $total "Config and API key";      write_conf; write_key
    step 6 $total "Hand over from the container"; handover_docker
    step 7 $total "systemd service";         install_service
    state_set INSTALLED_AT "$(date -u +%Y-%m-%dT%H:%M:%SZ)"

    if gtt_ready; then
        info "Starting $OMA_UNIT (model load plus GPU guard can take a few minutes)"
        if run as_root systemctl restart "$OMA_UNIT"; then
            ok "Service is up and the GPU guard passed"
            bash "$PROFILE_DIR/oma-check.sh" || warn "oma-check reported a problem (see above)"
        else
            err "Service failed to start. See: journalctl -u $OMA_UNIT -n 50"
            return 1
        fi
    else
        warn "GTT kernel params are not active yet. Reboot, then the service starts on its own."
        warn "After the reboot, verify with: $PROFILE_DIR/oma-7940hs.sh check"
    fi
    show_log_location
}

# ── uninstall ─────────────────────────────────────────────────────────────────

cmd_uninstall() {
    banner "OpenMono 7940HS native profile: uninstall"
    as_root test -f "$OMA_STATE/state.env" || { info "Nothing recorded in $OMA_STATE; nothing to undo"; return 0; }
    confirm "Undo every change this profile made?" || { info "Cancelled"; return 0; }

    step 1 5 "Stop and remove the service"
    run as_root systemctl disable --now "$OMA_UNIT" 2>/dev/null || true
    restore_file "$(unit_path)"
    as_root rm -f "$OMA_PREFIX/bin/launch-llama.sh" "$OMA_PREFIX/bin/gpu-guard.sh"
    run as_root systemctl daemon-reload || true

    step 2 5 "Revert tweaks (reverse order)"
    local f
    while IFS= read -r f; do
        tweak_call "$f" tweak_revert || warn "Revert of $(basename "$f") reported a problem"
    done < <(tweak_files | sort -r)

    step 3 5 "Config and key"
    restore_file "$OMA_ETC/oma.conf"
    as_root rm -f "$OMA_KEY_FILE"
    as_root rmdir "$OMA_ETC" 2>/dev/null || true

    step 4 5 "Give the port back to the container"
    if [[ -d "$DOCKER_DIR" ]]; then
        local orig; orig="$(state_get ORIG_LLAMA_HOST_NATIVE)"
        if [[ -n "$orig" ]]; then env_set LLAMA_HOST_NATIVE "$orig"; else env_del LLAMA_HOST_NATIVE; fi
        state_del ORIG_LLAMA_HOST_NATIVE
    fi
    if has_docker && dk inspect llama-server >/dev/null 2>&1; then
        local pol run_; pol="$(state_get DOCKER_LLAMA_RESTART)"; run_="$(state_get DOCKER_LLAMA_RUNNING)"
        [[ -n "$pol" ]] && { run dk update --restart="$pol" llama-server || warn "Could not restore restart=$pol"; }
        [[ "$run_" == "true" ]] && { run dk start llama-server || warn "Could not start the llama-server container"; }
        ok "llama-server container restored (restart=${pol:-unchanged})"
    fi
    state_del DOCKER_LLAMA_RESTART; state_del DOCKER_LLAMA_RUNNING

    step 5 5 "Files"
    [[ "$KEEP_LLAMA" == 1 ]] || as_root rm -rf "$OMA_PREFIX/llama.cpp"
    as_root rmdir "$OMA_PREFIX/bin" "$OMA_PREFIX" 2>/dev/null || true
    if [[ "$PURGE_MODEL" == 1 ]]; then as_root rm -f "$(model_path)"; ok "Model deleted"
    else info "Model kept at $(model_path) (use --purge-model to delete it)"; fi
    local reboot; reboot="$(state_get REBOOT_NEEDED)"
    as_root rm -f "$OMA_STATE/state.env"
    as_root rm -rf "$OMA_STATE/backups" "$OMA_STATE/downloads"
    as_root rmdir "$OMA_MODEL_DIR" "$OMA_STATE" 2>/dev/null || true
    # docker/.env is restored field by field above; its full backup is not
    # copied back so keys rotated after install are kept.
    ok "Uninstall complete"
    [[ -n "$reboot" ]] && warn "Reboot to return the GTT window to its default"
    return 0
}

# ── status / tweaks / key ─────────────────────────────────────────────────────

cmd_tweaks() {
    local f meta id tag rb desc st rc
    printf '  %-16s %-7s %-7s %s\n' "TWEAK" "TAG" "REBOOT" "STATE"
    while IFS= read -r f; do
        meta="$(tweak_meta "$f")"
        IFS='|' read -r id tag rb desc <<<"$meta"
        rc=0; st="$(tweak_call "$f" tweak_check 2>/dev/null)" || rc=$?
        printf '  %-16s %-7s %-7s %s\n' "$id" "$tag" "$([[ "$rb" == 1 ]] && echo yes || echo no)" "$st"
        detail "$id: $desc (rc $rc)"
    done < <(tweak_files)
}

cmd_status() {
    banner "OpenMono 7940HS native profile: status"
    if systemctl is-active --quiet "$OMA_UNIT" 2>/dev/null; then ok "$OMA_UNIT active"
    else warn "$OMA_UNIT $(systemctl is-active "$OMA_UNIT" 2>/dev/null || echo inactive)"; fi
    if curl -sf "http://127.0.0.1:${OMA_PORT}/health" >/dev/null 2>&1; then ok "health OK on port $OMA_PORT"
    else warn "no /health on port $OMA_PORT"; fi
    info "GTT ${OMA_GTT_MIB} MiB wanted, $(gtt_total_mib) MiB active; VRAM+GTT in use $(gpu_mem_used_mib) MiB"
    if has_docker && dk inspect llama-server >/dev/null 2>&1; then
        info "llama-server container: $(dk inspect -f '{{.State.Status}} restart={{.HostConfig.RestartPolicy.Name}}' llama-server)"
    fi
    echo ""; cmd_tweaks
}

cmd_sync_key() {
    write_key
    if systemctl is-active --quiet "$OMA_UNIT" 2>/dev/null; then
        run as_root systemctl restart "$OMA_UNIT" && ok "Service restarted with the current key"
    fi
}

# ── main ──────────────────────────────────────────────────────────────────────

[[ "$(uname -s)" == "Linux" ]] || { echo "Linux only" >&2; exit 1; }
CMD="${1:-}"; [[ $# -gt 0 ]] && shift
while [[ $# -gt 0 ]]; do
    case "$1" in
        --yes|-y)      YES=1 ;;
        --force)       FORCE=1 ;;
        --skip-model)  SKIP_MODEL=1 ;;
        --skip-tweak)  SKIP_TWEAKS+=("${2:?--skip-tweak needs an id}"); shift ;;
        --purge-model) PURGE_MODEL=1 ;;
        --keep-llama)  KEEP_LLAMA=1 ;;
        --restart)     CHECK_ARGS=(--restart) ;;
        -h|--help)     usage; exit 0 ;;
        *) err "Unknown option: $1"; usage; exit 2 ;;
    esac
    shift
done

case "$CMD" in
    preflight)          cmd_preflight ;;
    install)            cmd_install ;;
    uninstall|rollback) cmd_uninstall ;;
    status)             cmd_status ;;
    tweaks)             cmd_tweaks ;;
    check)              bash "$PROFILE_DIR/oma-check.sh" "${CHECK_ARGS[@]+"${CHECK_ARGS[@]}"}" ;;
    sync-key)           cmd_sync_key ;;
    ""|-h|--help|help)  usage ;;
    *) err "Unknown command: $CMD"; usage; exit 2 ;;
esac
