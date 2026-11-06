#!/usr/bin/env bash
# OpenMono v100 chassis fan controller.
#
# Reads Tesla V100 / V100S temperatures only (the hotter card wins) and drives
# the motherboard header labeled CHA_FAN1. The GTX 1650 and every other GPU
# are ignored. CPU_FAN and every other header are never written.
#
# Curve (duty percent, easy to change): at or below 40 C stay near 50 percent,
# ramp linearly, and sit at 100 percent from 55 C up. 75 C is the practical
# ceiling on this hardware, so the header is already at full duty well before
# that. The floor is never zero: fans are not stopped.
#
# If nvidia-smi fails, no V100 temperature is usable, or a duty write fails,
# CHA_FAN1 is set to 100 percent when that channel is known. If CHA_FAN1
# cannot be identified, nothing is written.
#
# NCT6796D on the ASUS PRIME X299-DELUXE does not publish fan or pwm labels.
# On that board only, and only when those label files are absent, pwm1 is
# CHA_FAN1. pwm2 is the CPU header and is never written. Any other board
# with no labels is left alone.
#
# Usage:
#   scripts/v100-gpu-fan.sh            # control loop (systemd)
#   scripts/v100-gpu-fan.sh --once     # one cycle, then exit
#   sudo scripts/v100-gpu-fan.sh --install
#       Install and enable the systemd unit. Refuses unless nvidia-smi shows
#       two or more Tesla V100S cards with about 32 GB each.
set -euo pipefail

# Duty curve. Temperatures are degrees C. Percents are PWM duty.
V100_FAN_FLOOR_C="${V100_FAN_FLOOR_C:-40}"
V100_FAN_CEIL_C="${V100_FAN_CEIL_C:-55}"
V100_FAN_MIN_PCT="${V100_FAN_MIN_PCT:-50}"
V100_FAN_MAX_PCT="${V100_FAN_MAX_PCT:-100}"
V100_FAN_LOOP_SEC="${V100_FAN_LOOP_SEC:-2}"
V100_FAN_HEARTBEAT_SEC="${V100_FAN_HEARTBEAT_SEC:-60}"
# Practical ceiling used on this workstation. The curve is already at
# V100_FAN_MAX_PCT before this point. Crossing it is logged, not acted on
# beyond holding full duty.
V100_FAN_WARN_C="${V100_FAN_WARN_C:-75}"

V100_FAN_STATE_DIR="${V100_FAN_STATE_DIR:-/var/lib/openmono}"
V100_FAN_STATE_FILE="${V100_FAN_STATE_FILE:-$V100_FAN_STATE_DIR/v100-cha-fan1}"
V100_FAN_LOG_FILE="${V100_FAN_LOG_FILE:-$V100_FAN_STATE_DIR/v100-gpu-fan.log}"
V100_FAN_SYSFS="${V100_FAN_SYSFS:-/sys/class/hwmon}"
V100_FAN_INSTALL_BIN="${V100_FAN_INSTALL_BIN:-/usr/local/lib/openmono/v100-gpu-fan.sh}"
V100_FAN_UNIT_PATH="${V100_FAN_UNIT_PATH:-/etc/systemd/system/openmono-v100-gpu-fan.service}"
V100_FAN_MODULES_CONF="${V100_FAN_MODULES_CONF:-/etc/modules-load.d/openmono-nct6775.conf}"
# Saved when CHA_FAN1 was chosen by the Prime X299-DELUXE pwm1 rule.
V100_FAN_BOARD_SOURCE="prime-x299-deluxe-nct6796-pwm1"

# Last resolved channel for this process. Re-detected every cycle.
V100_FAN_PWM=""
V100_FAN_ENABLE=""
V100_FAN_LABEL_FILE=""
V100_FAN_SOURCE=""

fan_log() {
    local line
    line="$(date '+%Y-%m-%d %H:%M:%S') v100-gpu-fan: $*"
    printf '%s\n' "$line"
    if [ -n "${V100_FAN_STATE_DIR:-}" ] && [ -d "$V100_FAN_STATE_DIR" ] && [ -w "$V100_FAN_STATE_DIR" ]; then
        # Keep the on-disk log small. Drop it rather than rotating through a
        # path that could pick up a hostname.
        if [ -f "$V100_FAN_LOG_FILE" ]; then
            local bytes
            bytes="$(wc -c < "$V100_FAN_LOG_FILE" 2>/dev/null || echo 0)"
            if [ "${bytes:-0}" -gt 262144 ]; then
                : > "$V100_FAN_LOG_FILE" 2>/dev/null || true
            fi
        fi
        printf '%s\n' "$line" >> "$V100_FAN_LOG_FILE" 2>/dev/null || true
    fi
}

# Lowercase, collapse separators to a single underscore, trim edges.
fan_norm_label() {
    printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/_/g; s/^_+//; s/_+$//'
}

# Exact CHA_FAN1 names only. cha_fan2, cpu_fan, and chassis_fan2 do not match.
fan_is_cha_fan1() {
    case "$(fan_norm_label "$1")" in
        cha_fan1|cha_fan_1|chassis_fan1|chassis_fan_1) return 0 ;;
        *) return 1 ;;
    esac
}

# True when nvidia-smi lists two or more Tesla V100S cards of about 32 GB.
# Same gate as scripts/install.sh v100_profile_detect. Ignores the GTX 1650.
# Tests may set V100_GPU_QUERY to CSV lines "index, name, memory_mib".
v100_fan_dual_v100s() {
    local query count
    query="${V100_GPU_QUERY:-}"
    if [ -z "$query" ]; then
        command -v nvidia-smi >/dev/null 2>&1 || return 1
        query="$(nvidia-smi --query-gpu=index,name,memory.total --format=csv,noheader,nounits 2>/dev/null || true)"
    fi
    [ -n "$query" ] || return 1
    count="$(printf '%s\n' "$query" | awk -F',' '
        {
            name=$2; mem=$3
            gsub(/^[ \t]+|[ \t]+$/, "", name)
            gsub(/^[ \t]+|[ \t]+$/, "", mem)
            lname=tolower(name)
            if (lname ~ /1650/ || lname ~ /geforce/) next
            if (lname ~ /v100s/ || lname ~ /v100-s/ || lname ~ /v100 s/) {
                if ((mem + 0) >= 30000) c++
            }
        }
        END { print c+0 }
    ')"
    [ "${count:-0}" -ge 2 ]
}

# Print the hotter Tesla V100 / V100S temperature.
# Prints "temp count" on success. Returns 1 when nvidia-smi fails, when no
# V100 is listed, or when a listed V100 has no numeric temperature (the hot
# card might be the one we cannot read).
# Tests may set V100_TEMP_QUERY to CSV lines "name, temperature".
v100_fan_max_temp() {
    local query
    query="${V100_TEMP_QUERY:-}"
    if [ -z "$query" ]; then
        command -v nvidia-smi >/dev/null 2>&1 || return 1
        if ! query="$(nvidia-smi --query-gpu=name,temperature.gpu --format=csv,noheader,nounits 2>/dev/null)"; then
            return 1
        fi
    fi
    [ -n "$query" ] || return 1
    printf '%s\n' "$query" | awk -F',' '
        {
            name=$1; temp=$2
            gsub(/^[ \t]+|[ \t]+$/, "", name)
            gsub(/^[ \t]+|[ \t]+$/, "", temp)
            lname=tolower(name)
            if (lname ~ /1650/ || lname ~ /geforce/) next
            if (lname !~ /v100/) next
            n++
            if (temp !~ /^[0-9]+$/) { bad=1; next }
            if (temp+0 > max) max=temp+0
            good++
        }
        END {
            if (n+0 < 1 || bad || good+0 < 1) exit 1
            printf "%d %d\n", max+0, n+0
        }
    '
}

# Map a temperature to a duty percent. Never returns 0.
v100_fan_percent() {
    local temp="$1" floor ceil minp maxp span
    floor="$V100_FAN_FLOOR_C"
    ceil="$V100_FAN_CEIL_C"
    minp="$V100_FAN_MIN_PCT"
    maxp="$V100_FAN_MAX_PCT"
    if [ "$temp" -le "$floor" ]; then
        printf '%s' "$minp"
        return 0
    fi
    if [ "$temp" -ge "$ceil" ]; then
        printf '%s' "$maxp"
        return 0
    fi
    span=$((ceil - floor))
    [ "$span" -gt 0 ] || { printf '%s' "$maxp"; return 0; }
    printf '%s' $(( minp + (temp - floor) * (maxp - minp) / span ))
}

# 0-255 PWM value. 100 percent is 255. Never returns 0.
v100_fan_pwm_value() {
    local pct="$1" value
    # Any non-positive request still produces a non-zero duty. The curve
    # itself never asks for less than V100_FAN_MIN_PCT.
    if [ "$pct" -lt 1 ]; then pct=1; fi
    if [ "$pct" -gt 100 ]; then pct=100; fi
    value=$(( (pct * 255 + 50) / 100 ))
    if [ "$value" -lt 1 ]; then value=1; fi
    if [ "$value" -gt 255 ]; then value=255; fi
    printf '%s' "$value"
}

# Record one candidate. Arguments: pwm enable label_file
_fan_add_candidate() {
    local pwm="$1" enable="$2" label_file="$3" real
    [ -e "$pwm" ] && [ -e "$enable" ] || return 0
    real="$(readlink -f "$pwm" 2>/dev/null || printf '%s' "$pwm")"
    _FAN_CANDIDATES="${_FAN_CANDIDATES}${real}|${pwm}|${enable}|${label_file}"$'\n'
}

# Scan hwmon for a single CHA_FAN1 pwm. Prints "pwm enable label_file".
# Returns 1 if none or more than one distinct pwm matches.
v100_fan_find_pwm() {
    local root="${1:-$V100_FAN_SYSFS}" dir label_file base num label pwm enable
    _FAN_CANDIDATES=""
    [ -d "$root" ] || return 1
    for dir in "$root"/hwmon*; do
        [ -d "$dir" ] || continue
        for label_file in "$dir"/fan*_label "$dir"/pwm*_label; do
            [ -f "$label_file" ] || continue
            label="$(tr -d '\r' < "$label_file" 2>/dev/null || true)"
            fan_is_cha_fan1 "$label" || continue
            base="$(basename "$label_file")"
            case "$base" in
                fan*_label)
                    num="${base#fan}"
                    num="${num%_label}"
                    ;;
                pwm*_label)
                    num="${base#pwm}"
                    num="${num%_label}"
                    # A fan label that names a different header wins. Do not
                    # treat a mismatched pair as CHA_FAN1.
                    if [ -f "$dir/fan${num}_label" ]; then
                        fan_is_cha_fan1 "$(tr -d '\r' < "$dir/fan${num}_label")" || continue
                    fi
                    ;;
                *) continue ;;
            esac
            case "$num" in
                ''|*[!0-9]*) continue ;;
            esac
            pwm="$dir/pwm${num}"
            enable="$dir/pwm${num}_enable"
            _fan_add_candidate "$pwm" "$enable" "$label_file"
        done
    done
    [ -n "$_FAN_CANDIDATES" ] || return 1
    # One physical pwm, even if both fanN_label and pwmN_label matched it.
    local uniq
    uniq="$(printf '%s' "$_FAN_CANDIDATES" | awk -F'|' 'NF && !seen[$1]++ { print $2 "|" $3 "|" $4 }')"
    [ "$(printf '%s\n' "$uniq" | awk 'NF{c++} END{print c+0}')" -eq 1 ] || return 1
    printf '%s' "$uniq" | head -1
}

# Board name from DMI. Tests may set V100_FAN_BOARD_NAME, including empty.
v100_fan_board_name() {
    if [ -n "${V100_FAN_BOARD_NAME+set}" ]; then
        printf '%s' "$V100_FAN_BOARD_NAME"
        return 0
    fi
    local f
    for f in /sys/class/dmi/id/board_name /sys/devices/virtual/dmi/id/board_name; do
        if [ -r "$f" ]; then
            tr -d '\r' < "$f"
            return 0
        fi
    done
    return 1
}

v100_fan_is_prime_x299_deluxe() {
    local name
    name="$(v100_fan_board_name 2>/dev/null || true)"
    [ "$(fan_norm_label "$name")" = "prime_x299_deluxe" ]
}

# True when this hwmon directory has no fan*_label or pwm*_label files.
v100_fan_labels_absent() {
    local dir="$1" f
    for f in "$dir"/fan*_label "$dir"/pwm*_label; do
        [ -e "$f" ] && return 1
    done
    return 0
}

# PRIME X299-DELUXE + nct6796, labels absent: pwm1 is CHA_FAN1.
# Prints "pwm|enable". Returns 1 for any other board, any labeled chip,
# or more than one matching chip. Never returns pwm2.
v100_fan_find_board_pwm() {
    local root="${1:-$V100_FAN_SYSFS}" dir name hits="" pwm enable real
    v100_fan_is_prime_x299_deluxe || return 1
    [ -d "$root" ] || return 1
    for dir in "$root"/hwmon*; do
        [ -d "$dir" ] || continue
        [ -f "$dir/name" ] || continue
        name="$(tr -d '[:space:]' < "$dir/name" 2>/dev/null || true)"
        [ "$(fan_norm_label "$name")" = "nct6796" ] || continue
        v100_fan_labels_absent "$dir" || continue
        pwm="$dir/pwm1"
        enable="$dir/pwm1_enable"
        [ -e "$pwm" ] && [ -e "$enable" ] || continue
        case "$(basename "$pwm")" in
            pwm1) ;;
            *) continue ;;
        esac
        real="$(readlink -f "$pwm" 2>/dev/null || printf '%s' "$pwm")"
        hits="${hits}${real}|${pwm}|${enable}"$'\n'
    done
    [ -n "$hits" ] || return 1
    local uniq
    uniq="$(printf '%s' "$hits" | awk -F'|' 'NF && !seen[$1]++ { print $2 "|" $3 }')"
    [ "$(printf '%s\n' "$uniq" | awk 'NF{c++} END{print c+0}')" -eq 1 ] || return 1
    printf '%s' "$uniq" | head -1
}

v100_fan_save_channel() {
    local pwm="$1" enable="$2" label_file="${3:-}" source="${4:-}"
    [ -n "$pwm" ] || return 0
    mkdir -p "$V100_FAN_STATE_DIR" 2>/dev/null || return 0
    [ -w "$V100_FAN_STATE_DIR" ] || return 0
    local tmp
    tmp="$(mktemp "$V100_FAN_STATE_DIR/.v100-cha-fan1.XXXXXX" 2>/dev/null)" || return 0
    {
        printf 'pwm=%s\n' "$pwm"
        printf 'enable=%s\n' "$enable"
        printf 'label_file=%s\n' "$label_file"
        printf 'source=%s\n' "$source"
    } > "$tmp"
    mv -f "$tmp" "$V100_FAN_STATE_FILE" 2>/dev/null || rm -f "$tmp"
}

# Saved board fallback is still pwm1 on an unlabeled nct6796, on this board.
v100_fan_saved_board_ok() {
    local pwm="$1" enable="$2" dir name
    [ "$(basename "$pwm")" = "pwm1" ] || return 1
    [ -e "$pwm" ] && [ -e "$enable" ] || return 1
    [ "$(basename "$enable")" = "pwm1_enable" ] || return 1
    dir="$(dirname "$pwm")"
    [ -f "$dir/name" ] || return 1
    name="$(tr -d '[:space:]' < "$dir/name" 2>/dev/null || true)"
    [ "$(fan_norm_label "$name")" = "nct6796" ] || return 1
    v100_fan_labels_absent "$dir" || return 1
    v100_fan_is_prime_x299_deluxe || return 1
    return 0
}

# Load a previously identified channel only if its label still says CHA_FAN1.
v100_fan_load_channel() {
    local pwm="" enable="" label_file="" source="" line key val label
    [ -f "$V100_FAN_STATE_FILE" ] || return 1
    while IFS= read -r line || [ -n "$line" ]; do
        key="${line%%=*}"
        val="${line#*=}"
        case "$key" in
            pwm) pwm="$val" ;;
            enable) enable="$val" ;;
            label_file) label_file="$val" ;;
            source) source="$val" ;;
        esac
    done < "$V100_FAN_STATE_FILE"
    [ -n "$pwm" ] && [ -e "$pwm" ] && [ -n "$enable" ] && [ -e "$enable" ] || return 1
    if [ "$source" = "$V100_FAN_BOARD_SOURCE" ]; then
        v100_fan_saved_board_ok "$pwm" "$enable" || return 1
        V100_FAN_PWM="$pwm"
        V100_FAN_ENABLE="$enable"
        V100_FAN_LABEL_FILE=""
        V100_FAN_SOURCE="$source"
        return 0
    fi
    [ -n "$label_file" ] && [ -f "$label_file" ] || return 1
    label="$(tr -d '\r' < "$label_file" 2>/dev/null || true)"
    fan_is_cha_fan1 "$label" || return 1
    V100_FAN_PWM="$pwm"
    V100_FAN_ENABLE="$enable"
    V100_FAN_LABEL_FILE="$label_file"
    V100_FAN_SOURCE=""
    return 0
}

# Set V100_FAN_PWM / ENABLE / LABEL_FILE. Prefer a fresh label detect.
# On the Prime X299-DELUXE, an unlabeled nct6796 uses pwm1. A saved channel
# is reused only when it still checks out. Anything else writes nothing.
v100_fan_resolve() {
    local found pwm enable label_file
    V100_FAN_PWM=""
    V100_FAN_ENABLE=""
    V100_FAN_LABEL_FILE=""
    V100_FAN_SOURCE=""
    if found="$(v100_fan_find_pwm "$V100_FAN_SYSFS")"; then
        IFS='|' read -r pwm enable label_file <<< "$found"
        V100_FAN_PWM="$pwm"
        V100_FAN_ENABLE="$enable"
        V100_FAN_LABEL_FILE="$label_file"
        v100_fan_save_channel "$pwm" "$enable" "$label_file" "" || true
        return 0
    fi
    if found="$(v100_fan_find_board_pwm "$V100_FAN_SYSFS")"; then
        IFS='|' read -r pwm enable <<< "$found"
        [ "$(basename "$pwm")" = "pwm1" ] || return 1
        V100_FAN_PWM="$pwm"
        V100_FAN_ENABLE="$enable"
        V100_FAN_SOURCE="$V100_FAN_BOARD_SOURCE"
        v100_fan_save_channel "$pwm" "$enable" "" "$V100_FAN_BOARD_SOURCE" || true
        if [ "${_FAN_LOGGED_BOARD:-0}" -eq 0 ]; then
            fan_log "CHA_FAN1 label absent on nct6796, using pwm1 for PRIME X299-DELUXE"
            _FAN_LOGGED_BOARD=1
        fi
        return 0
    fi
    if v100_fan_load_channel; then
        fan_log "CHA_FAN1 detect failed this cycle, reusing the last checked channel"
        return 0
    fi
    fan_log "error: CHA_FAN1 was not identified. No pwm written."
    return 1
}

# Shell redirect errors (permission denied) stay off the log. The caller logs
# a short reason instead.
_fan_write_sysfs() {
    local path="$1" value="$2"
    { printf '%s\n' "$value" > "$path"; } 2>/dev/null
}

# Write manual mode (enable 1) and a duty. Never writes enable 0 or pwm 0.
# On a duty write failure, tries 100 percent so the header is not left low.
v100_fan_write_duty() {
    local value="$1" full
    [ -n "$V100_FAN_PWM" ] && [ -n "$V100_FAN_ENABLE" ] || return 1
    if [ "$value" -lt 1 ]; then value=1; fi
    if ! _fan_write_sysfs "$V100_FAN_ENABLE" 1; then
        fan_log "error: could not set manual mode on CHA_FAN1"
        return 1
    fi
    if _fan_write_sysfs "$V100_FAN_PWM" "$value"; then
        return 0
    fi
    full="$(v100_fan_pwm_value "$V100_FAN_MAX_PCT")"
    fan_log "error: duty write failed, setting CHA_FAN1 to 100 percent"
    if _fan_write_sysfs "$V100_FAN_ENABLE" 1 && _fan_write_sysfs "$V100_FAN_PWM" "$full"; then
        return 2
    fi
    fan_log "error: could not set CHA_FAN1 to 100 percent"
    return 1
}

v100_fan_force_full() {
    v100_fan_resolve || return 1
    v100_fan_write_duty "$(v100_fan_pwm_value "$V100_FAN_MAX_PCT")"
}

# One control cycle. Always returns 0 so the loop keeps running.
v100_fan_cycle() {
    local reading temp count pct value
    if ! reading="$(v100_fan_max_temp)"; then
        fan_log "error: no usable V100 temperature. Forcing 100 percent if CHA_FAN1 is known."
        v100_fan_force_full || true
        return 0
    fi
    temp="${reading%% *}"
    count="${reading##* }"
    pct="$(v100_fan_percent "$temp")"
    value="$(v100_fan_pwm_value "$pct")"
    if ! v100_fan_resolve; then
        return 0
    fi
    local rc=0
    v100_fan_write_duty "$value" || rc=$?
    if [ "$rc" -eq 2 ]; then
        pct="$V100_FAN_MAX_PCT"
        value="$(v100_fan_pwm_value "$pct")"
    elif [ "$rc" -ne 0 ]; then
        return 0
    fi
    if [ "$temp" -ge "$V100_FAN_WARN_C" ] && [ "${_FAN_LAST_WARN:-0}" -eq 0 ]; then
        fan_log "warning: V100 temperature ${temp} C is at or above ${V100_FAN_WARN_C} C. CHA_FAN1 at ${pct} percent."
    fi
    if [ "$temp" -ge "$V100_FAN_WARN_C" ]; then
        _FAN_LAST_WARN=1
    else
        _FAN_LAST_WARN=0
    fi
    _fan_note_reading "$temp" "$count" "$pct" "$value"
    return 0
}

_FAN_LAST_PCT=""
_FAN_LAST_BEAT="${SECONDS:-0}"

_fan_note_reading() {
    local temp="$1" count="$2" pct="$3" value="$4" now
    now="${SECONDS:-0}"
    if [ "$pct" != "${_FAN_LAST_PCT:-}" ] || [ $((now - _FAN_LAST_BEAT)) -ge "$V100_FAN_HEARTBEAT_SEC" ]; then
        fan_log "v100_count=${count} max_c=${temp} pct=${pct} duty=${value} pwm=${V100_FAN_PWM}"
        _FAN_LAST_PCT="$pct"
        _FAN_LAST_BEAT="$now"
    fi
}

# The nct6775 driver is what publishes NCT6796 pwm nodes. Skip this when
# tests point sysfs at a fake tree.
v100_fan_load_module() {
    [ "$V100_FAN_SYSFS" = "/sys/class/hwmon" ] || return 0
    local dir name
    for dir in "$V100_FAN_SYSFS"/hwmon*; do
        [ -f "$dir/name" ] || continue
        name="$(tr -d '[:space:]' < "$dir/name" 2>/dev/null || true)"
        [ "$(fan_norm_label "$name")" = "nct6796" ] && return 0
    done
    if command -v modprobe >/dev/null 2>&1; then
        modprobe nct6775 2>/dev/null || fan_log "warning: modprobe nct6775 failed. Motherboard pwm nodes may be missing."
    fi
}

v100_fan_modules_conf() {
    printf '%s\n' "# NCT6796D chassis fan control for the v100 profile." "nct6775"
}

v100_fan_unit_body() {
    cat <<EOF
[Unit]
Description=OpenMono V100 chassis fan (CHA_FAN1 only)
After=systemd-modules-load.service multi-user.target

[Service]
Type=simple
ExecStart=${V100_FAN_INSTALL_BIN}
Restart=always
RestartSec=${V100_FAN_LOOP_SEC}

[Install]
WantedBy=multi-user.target
EOF
}

v100_fan_loop() {
    v100_fan_load_module || true
    fan_log "starting CHA_FAN1 control loop, interval ${V100_FAN_LOOP_SEC}s, floor ${V100_FAN_MIN_PCT} percent at or below ${V100_FAN_FLOOR_C} C, full at ${V100_FAN_CEIL_C} C"
    while true; do
        v100_fan_cycle || true
        sleep "$V100_FAN_LOOP_SEC"
    done
}

_fan_as_root() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    else
        sudo "$@"
    fi
}

v100_fan_install() {
    if ! v100_fan_dual_v100s; then
        fan_log "error: not a dual Tesla V100S 32GB workstation. Fan service not installed."
        return 1
    fi
    local src unit_body
    src="$(readlink -f "$0" 2>/dev/null || printf '%s' "$0")"
    unit_body="$(v100_fan_unit_body)"
    _fan_as_root mkdir -p "$(dirname "$V100_FAN_INSTALL_BIN")" "$(dirname "$V100_FAN_MODULES_CONF")" "$V100_FAN_STATE_DIR"
    v100_fan_modules_conf | _fan_as_root tee "$V100_FAN_MODULES_CONF" >/dev/null
    if command -v modprobe >/dev/null 2>&1; then
        _fan_as_root modprobe nct6775 2>/dev/null || fan_log "warning: modprobe nct6775 failed. The module file is installed and loads at next boot."
    fi
    _fan_as_root cp "$src" "$V100_FAN_INSTALL_BIN"
    _fan_as_root chmod 0755 "$V100_FAN_INSTALL_BIN"
    printf '%s\n' "$unit_body" | _fan_as_root tee "$V100_FAN_UNIT_PATH" >/dev/null
    if ! command -v systemctl >/dev/null 2>&1; then
        fan_log "error: systemctl is not available. Unit file written, service not enabled."
        return 1
    fi
    _fan_as_root systemctl daemon-reload
    _fan_as_root systemctl enable --now openmono-v100-gpu-fan.service
    _fan_as_root systemctl restart openmono-v100-gpu-fan.service
    fan_log "enabled openmono-v100-gpu-fan.service (CHA_FAN1 only)"
}

v100_fan_main() {
    case "${1:-}" in
        --install)
            v100_fan_install
            ;;
        --once)
            v100_fan_cycle
            ;;
        --help|-h)
            sed -n '2,20p' "$0"
            ;;
        "")
            v100_fan_loop
            ;;
        *)
            fan_log "error: unknown argument. Use --install, --once, or no arguments."
            return 1
            ;;
    esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    v100_fan_main "$@"
fi
