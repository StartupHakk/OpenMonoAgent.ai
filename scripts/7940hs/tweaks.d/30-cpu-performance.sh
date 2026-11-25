#!/usr/bin/env bash
# shellcheck disable=SC2034  # TWEAK_* metadata is read by oma-7940hs.sh
# Tweak: CPU governor, EPP and platform profile to performance at every boot.
#
# Installs a small oneshot unit (openmono-7940hs-perf.service). The values
# found before install are recorded, and uninstall puts them back.

TWEAK_ID="cpu-performance"
TWEAK_DESC="CPU governor, EPP and platform_profile set to performance at boot"
TWEAK_TAG="PROVEN"
TWEAK_REBOOT=0

_perf_unit_path() { printf '%s/%s' "${OMA_SYSTEMD_DIR:-/etc/systemd/system}" "$OMA_PERF_UNIT"; }

tweak_check() {
    local gov
    gov="$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null || echo unknown)"
    if systemctl is-enabled --quiet "$OMA_PERF_UNIT" 2>/dev/null && [[ "$gov" == "performance" ]]; then
        echo "active: governor performance"; return 0
    fi
    echo "not active (governor $gov)"; return 1
}

tweak_apply() {
    state_set_once ORIG_GOVERNOR "$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null || true)"
    state_set_once ORIG_EPP "$(cat /sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference 2>/dev/null || true)"
    state_set_once ORIG_PLATFORM_PROFILE "$(cat /sys/firmware/acpi/platform_profile 2>/dev/null || true)"
    backup_file "$(_perf_unit_path)"
    run as_root install -D -m 0755 -o root -g root "$PROFILE_DIR/libexec/cpu-perf.sh" "$OMA_PREFIX/bin/cpu-perf.sh"
    install_text "$(_perf_unit_path)" 0644 \
        "$(sed "s|@PREFIX@|$OMA_PREFIX|g" "$PROFILE_DIR/systemd/openmono-7940hs-perf.service.in")" || true
    run as_root systemctl daemon-reload
    run as_root systemctl enable --now "$OMA_PERF_UNIT" || die "Could not start $OMA_PERF_UNIT"
    ok "CPU performance mode on (persists across reboots)"
}

tweak_revert() {
    run as_root systemctl disable --now "$OMA_PERF_UNIT" 2>/dev/null || true
    restore_file "$(_perf_unit_path)"
    as_root rm -f "$OMA_PREFIX/bin/cpu-perf.sh"
    run as_root systemctl daemon-reload || true
    local gov epp prof
    gov="$(state_get ORIG_GOVERNOR)"; epp="$(state_get ORIG_EPP)"; prof="$(state_get ORIG_PLATFORM_PROFILE)"
    if [[ -n "$gov" ]]; then
        as_root "$PROFILE_DIR/libexec/cpu-perf.sh" --governor "$gov" --epp "${epp:-}" --profile "${prof:-}" >/dev/null || true
    fi
    state_del ORIG_GOVERNOR; state_del ORIG_EPP; state_del ORIG_PLATFORM_PROFILE
    ok "CPU power settings restored to their pre-install values"
}
