#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai: shared helpers for the 7940HS native profile.
# Sourced by oma-7940hs.sh and tweaks.d/*.sh after scripts/lib/log.sh.
# Do not execute directly.
# ──────────────────────────────────────────────────────────────────────────────

# as_root CMD...: run as root (directly when already root, else via sudo).
as_root() {
    if [[ "$(id -u)" -eq 0 ]]; then "$@"; else sudo "$@"; fi
}

# ── State file: records what install changed, so uninstall undoes exactly that.
# Format: KEY=VALUE lines (values are %q-quoted), root-owned, 0600.
_state_file() { printf '%s/state.env' "$OMA_STATE"; }

state_get() {
    local key="$1" f; f="$(_state_file)"
    as_root test -f "$f" || return 0
    local line
    line="$(as_root grep -m1 "^${key}=" "$f" 2>/dev/null || true)"
    [[ -n "$line" ]] || return 0
    # Values were written with printf %q; eval-free decode via bash itself.
    local val="${line#*=}"
    # shellcheck disable=SC2059
    eval "printf '%s' $val"
}

state_set() {
    local key="$1" val="$2" f tmp
    f="$(_state_file)"
    as_root install -d -m 0755 "$OMA_STATE"
    tmp="$(mktemp)"
    if as_root test -f "$f"; then
        as_root grep -v "^${key}=" "$f" > "$tmp" 2>/dev/null || true
    fi
    printf '%s=%q\n' "$key" "$val" >> "$tmp"
    as_root install -m 0600 -o root -g root "$tmp" "$f"
    rm -f "$tmp"
}

# state_set_once KEY VAL: record the ORIGINAL value only on the first install,
# so re-running install never overwrites what uninstall must restore.
state_set_once() {
    local key="$1" val="$2"
    [[ -n "$(state_has "$key")" ]] && return 0
    state_set "$key" "$val"
}

state_has() {
    local f; f="$(_state_file)"
    as_root test -f "$f" || return 0
    as_root grep -q "^${1}=" "$f" 2>/dev/null && printf 'yes'
    return 0
}

state_del() {
    local key="$1" f tmp; f="$(_state_file)"
    as_root test -f "$f" || return 0
    tmp="$(mktemp)"
    as_root grep -v "^${key}=" "$f" > "$tmp" 2>/dev/null || true
    as_root install -m 0600 -o root -g root "$tmp" "$f"
    rm -f "$tmp"
}

# backup_file PATH: copy an existing system file into the backup dir once
# (first install wins) and record where it went. No-op when PATH is absent.
backup_file() {
    local path="$1" key dest
    key="BACKUP_$(printf '%s' "$path" | tr -c 'A-Za-z0-9' '_')"
    [[ -n "$(state_has "$key")" ]] && return 0
    if as_root test -e "$path"; then
        dest="$OMA_STATE/backups/$(printf '%s' "$path" | tr '/' '_')"
        as_root install -d -m 0700 "$OMA_STATE/backups"
        as_root cp -a "$path" "$dest"
        state_set "$key" "$dest"
        detail "Backed up $path -> $dest"
    else
        state_set "$key" "ABSENT"
    fi
}

# restore_file PATH: put back the backed-up original, or remove the file if it
# did not exist before install.
restore_file() {
    local path="$1" key src
    key="BACKUP_$(printf '%s' "$path" | tr -c 'A-Za-z0-9' '_')"
    src="$(state_get "$key")"
    if [[ -z "$src" ]]; then
        return 0
    elif [[ "$src" == "ABSENT" ]]; then
        as_root rm -f "$path"
    elif as_root test -e "$src"; then
        as_root cp -a "$src" "$path"
    fi
    state_del "$key"
}

# install_text DEST MODE CONTENT: write a root-owned file only when it changed.
# Returns 0 when the file was written, 1 when it was already identical.
install_text() {
    local dest="$1" mode="$2" content="$3" tmp
    tmp="$(mktemp)"
    printf '%s\n' "$content" > "$tmp"
    if as_root test -f "$dest" && as_root cmp -s "$tmp" "$dest"; then
        rm -f "$tmp"; return 1
    fi
    as_root install -D -m "$mode" -o root -g root "$tmp" "$dest"
    rm -f "$tmp"
    return 0
}

# sha256_of FILE
sha256_of() { sha256sum "$1" | cut -d' ' -f1; }

# download_verified URL DEST SHA256 [SIZE]
# Resumable download with up to 3 attempts. The file only lands at DEST after
# its sha256 matches; a mismatch deletes the partial and retries.
download_verified() {
    local url="$1" dest="$2" want="$3" size="${4:-}" part try have
    part="${dest}.part"
    if [[ -f "$dest" ]]; then
        if [[ "$(sha256_of "$dest")" == "$want" ]]; then
            ok "Already present, sha256 OK: $(basename "$dest")"
            return 0
        fi
        warn "Existing $(basename "$dest") fails sha256; downloading again"
        rm -f "$dest"
    fi
    for try in 1 2 3; do
        info "Downloading $(basename "$dest") (try $try)"
        if ! curl -fL --retry 5 --retry-delay 5 -C - -o "$part" "$url"; then
            warn "Download interrupted; will resume"
            continue
        fi
        if [[ -n "$size" ]]; then
            have="$(stat -c %s "$part" 2>/dev/null || echo 0)"
            if [[ "$have" != "$size" ]]; then
                warn "Size $have, expected $size; resuming"
                continue
            fi
        fi
        info "Verifying sha256 of $(basename "$dest")"
        if [[ "$(sha256_of "$part")" == "$want" ]]; then
            mv -f "$part" "$dest"
            ok "sha256 OK: $(basename "$dest")"
            return 0
        fi
        warn "sha256 mismatch on $(basename "$dest"); deleting and retrying"
        rm -f "$part"
    done
    rm -f "$part"
    err "Could not download a verified copy of $(basename "$dest")"
    return 1
}

# gpu_mem_used_mib: VRAM + GTT in use on the first amdgpu card, in MiB.
gpu_mem_used_mib() {
    local dev vram gtt
    for dev in /sys/class/drm/card[0-9]*/device; do
        [[ -r "$dev/mem_info_gtt_used" ]] || continue
        vram="$(cat "$dev/mem_info_vram_used" 2>/dev/null || echo 0)"
        gtt="$(cat "$dev/mem_info_gtt_used" 2>/dev/null || echo 0)"
        printf '%s' $(( (vram + gtt) / 1048576 ))
        return 0
    done
    printf '0'
}

# gtt_total_mib: GTT window size on the first amdgpu card, in MiB.
gtt_total_mib() {
    local dev
    for dev in /sys/class/drm/card[0-9]*/device; do
        [[ -r "$dev/mem_info_gtt_total" ]] || continue
        printf '%s' $(( $(cat "$dev/mem_info_gtt_total") / 1048576 ))
        return 0
    done
    printf '0'
}

# vram_total_mib: BIOS carve-out (UMA frame buffer) size, in MiB.
vram_total_mib() {
    local dev
    for dev in /sys/class/drm/card[0-9]*/device; do
        [[ -r "$dev/mem_info_vram_total" ]] || continue
        printf '%s' $(( $(cat "$dev/mem_info_vram_total") / 1048576 ))
        return 0
    done
    printf '0'
}
