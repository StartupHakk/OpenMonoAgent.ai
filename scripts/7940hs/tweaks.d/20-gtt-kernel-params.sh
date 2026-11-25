#!/usr/bin/env bash
# shellcheck disable=SC2034  # TWEAK_* metadata is read by oma-7940hs.sh
# Tweak: raise the GTT window so the whole model fits on the iGPU.
#
# Default GTT on the reference box was 12.4 GiB, too small for a 15 GB model
# plus 64K of KV. A GRUB drop-in adds amdgpu.gttsize + ttm limits; this never
# edits /etc/default/grub itself, so rollback is deleting one file.

TWEAK_ID="gtt"
TWEAK_DESC="GTT window ${OMA_GTT_MIB} MiB via GRUB drop-in (amdgpu.gttsize, ttm.pages_limit, ttm.page_pool_size)"
TWEAK_TAG="TUNING"
TWEAK_REBOOT=1

_gtt_dropin="${OMA_GRUB_DROPIN:-/etc/default/grub.d/90-openmono-7940hs.cfg}"

_gtt_params() {
    local pages=$(( OMA_GTT_MIB * 256 ))
    printf 'amdgpu.gttsize=%s ttm.pages_limit=%s ttm.page_pool_size=%s' "$OMA_GTT_MIB" "$pages" "$pages"
}

tweak_check() {
    local want total
    want="$(_gtt_params)"
    total="$(gtt_total_mib)"
    if grep -q "amdgpu.gttsize=${OMA_GTT_MIB}" /proc/cmdline && (( total >= OMA_GTT_MIB - 64 )); then
        echo "active: GTT ${total} MiB"; return 0
    fi
    if [[ -f "$_gtt_dropin" ]] && grep -qF "$want" "$_gtt_dropin"; then
        echo "configured, reboot pending (GTT now ${total} MiB)"; return 2
    fi
    echo "not configured (GTT ${total} MiB)"; return 1
}

tweak_apply() {
    if [[ ! -f /etc/default/grub && ! -d "$(dirname "$_gtt_dropin")" ]] || ! command -v update-grub >/dev/null 2>&1; then
        die "GRUB (update-grub) not found; add these kernel params by hand: $(_gtt_params)"
    fi
    if grep -qE 'amdgpu\.gttsize=' /etc/default/grub 2>/dev/null; then
        warn "/etc/default/grub already sets amdgpu.gttsize (likely scripts/opt_7940hs.sh)."
        warn "This drop-in is appended after it, so its value wins on the kernel cmdline."
    fi
    backup_file "$_gtt_dropin"
    local content
    content="# Managed by OpenMono scripts/7940hs (tweak: gtt). Remove with: oma-7940hs.sh uninstall
GRUB_CMDLINE_LINUX_DEFAULT=\"\${GRUB_CMDLINE_LINUX_DEFAULT} $(_gtt_params)\""
    if install_text "$_gtt_dropin" 0644 "$content"; then
        run as_root update-grub || die "update-grub failed; the drop-in is at $_gtt_dropin"
        state_set REBOOT_NEEDED 1
        ok "GTT drop-in written, update-grub done (reboot required)"
    else
        ok "GTT drop-in already in place"
    fi
}

tweak_revert() {
    [[ -n "$(state_has "BACKUP_$(printf '%s' "$_gtt_dropin" | tr -c 'A-Za-z0-9' '_')")" ]] || return 0
    restore_file "$_gtt_dropin"
    run as_root update-grub || warn "update-grub failed; run it by hand"
    ok "GTT drop-in removed (reboot to return to the default window)"
}
