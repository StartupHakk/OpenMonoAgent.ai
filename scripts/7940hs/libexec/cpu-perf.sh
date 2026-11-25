#!/usr/bin/env bash
# OpenMono.ai 7940HS profile: set CPU governor, EPP and ACPI platform profile.
# Run as root by openmono-7940hs-perf.service (defaults = performance), and by
# uninstall with the values recorded before install.
#   cpu-perf.sh [--governor G] [--epp E] [--profile P]
set -uo pipefail

GOV="performance"; EPP="performance"; PROFILE="performance"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --governor) GOV="${2:-}"; shift 2 ;;
        --epp)      EPP="${2:-}"; shift 2 ;;
        --profile)  PROFILE="${2:-}"; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

# The EPP must be written while the governor is not "performance" on
# amd-pstate-epp, so write EPP first, then the governor.
if [[ -n "$EPP" ]]; then
    for f in /sys/devices/system/cpu/cpu[0-9]*/cpufreq/energy_performance_preference; do
        [[ -w "$f" ]] && echo "$EPP" > "$f" 2>/dev/null
    done
fi
if [[ -n "$GOV" ]]; then
    for f in /sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_governor; do
        [[ -w "$f" ]] && echo "$GOV" > "$f" 2>/dev/null
    done
fi
if [[ -n "$PROFILE" && -w /sys/firmware/acpi/platform_profile ]]; then
    if grep -qw "$PROFILE" /sys/firmware/acpi/platform_profile_choices 2>/dev/null; then
        echo "$PROFILE" > /sys/firmware/acpi/platform_profile 2>/dev/null
    fi
fi
echo "governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null) \
epp=$(cat /sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference 2>/dev/null) \
platform_profile=$(cat /sys/firmware/acpi/platform_profile 2>/dev/null)"
exit 0
