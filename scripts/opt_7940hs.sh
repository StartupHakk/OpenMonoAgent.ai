#!/usr/bin/env bash
set -euo pipefail

# ──────────────────────────────────────────────────────────────────────────────
# AMD Ryzen (Phoenix family) Vulkan Optimization Script
#
# Detects AMD Ryzen 7x40HS/8x45HS CPUs w/ Radeon 760M/780M iGPU (e.g. 7940HS,
# 8945HS) and applies kernel/system tuning for optimal iGPU performance via
# Vulkan. GTT sizing is derived from total system RAM.
#
# Usage:
#   opt_7940hs.sh [OPTIONS]
#
# Options:
#   --cpu           Force CPU-only mode (skip iGPU optimizations)
#   --igpu          Force iGPU mode (skip confirmation prompt)
#   --help          Show this help message
#
# Exit codes:
#   0 = Optimizations applied (or skipped)
#   1 = Not on target hardware or failed
# ──────────────────────────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/log.sh
source "$SCRIPT_DIR/lib/log.sh"

# Flags
FORCE_CPU=0
FORCE_IGPU=0

# Parse options
while [[ $# -gt 0 ]]; do
    case "$1" in
        --cpu)
            FORCE_CPU=1
            shift
            ;;
        --igpu)
            FORCE_IGPU=1
            shift
            ;;
        --help)
            grep "^#" "$0" | grep -E "^# (Usage|Options|Exit)" -A 100 | head -20
            exit 0
            ;;
        *)
            die "Unknown option: $1"
            ;;
    esac
done

# ─────────────────────────────────────────────────────────────────────────────
# Hardware Detection
# ─────────────────────────────────────────────────────────────────────────────

detect_cpu_model() {
    if [[ ! -f /proc/cpuinfo ]]; then
        return 1
    fi
    grep -m1 "^model name" /proc/cpuinfo | sed 's/model name[[:space:]]*:[[:space:]]*//g'
}

has_radeon_780m() {
    lspci 2>/dev/null | grep -qi "radeon.*7[68]0m" || \
    lspci 2>/dev/null | grep -qi "amd.*radeon.*7.*m" || \
    lspci 2>/dev/null | grep -qi "amd.*phoenix" || \
    (lsmod 2>/dev/null | grep -q "amdgpu" && grep -q "amdgpu" /proc/modules)
}

# Supported CPU families: Ryzen 7x40HS (Phoenix, e.g. 7940HS) and
# Ryzen 8x45HS (Hawk Point / Phoenix3, e.g. 8945HS) incl. PRO variants,
# or any AMD CPU that advertises its Radeon 7x0M iGPU in the model name.
is_supported_cpu() {
    echo "${1:-}" | grep -qiE "7[689]40HS|8[689]45HS|Radeon 7[68]0M"
}

# ─────────────────────────────────────────────────────────────────────────────
# Main Optimization Functions
# ─────────────────────────────────────────────────────────────────────────────

install_system_packages() {
    info "Installing Vulkan development packages..."
    run sudo apt update
    run sudo apt install -y \
        build-essential cmake git clang lld \
        vulkan-tools libvulkan-dev vulkan-validationlayers \
        mesa-vulkan-drivers \
        glslc glslang-tools libshaderc-dev \
        libomp-dev libssl-dev ccache nvtop cpufrequtils
    ok "System packages installed"
}

add_user_groups() {
    info "Adding user to render and video groups..."
    run sudo usermod -aG render,video "$USER"
    ok "User groups updated"
    detail "Note: You may need to log out and back in, or run: newgrp render"
}

edit_grub_config() {
    info "Configuring GRUB kernel parameters..."

    GRUB_FILE="/etc/default/grub"
    GRUB_BACKUP="/etc/default/grub.backup.$(date +%s)"

    if [[ ! -f "$GRUB_FILE" ]]; then
        die "GRUB configuration not found at $GRUB_FILE"
    fi

    run sudo cp "$GRUB_FILE" "$GRUB_BACKUP"
    detail "Backup created: $GRUB_BACKUP"

    # GTT sizing derived from total system RAM: the iGPU uses system RAM as
    # VRAM via the GTT window. gttsize is in MiB, pages_limit in 4KiB pages.
    #   128GB+ RAM → 96GB GTT   64GB+ RAM → 28GB GTT   else → 16GB GTT
    # Parsed from /proc/meminfo (locale-safe; `free` labels are localized) with
    # thresholds below nominal size since the kernel reports usable RAM only.
    TOTAL_RAM_GB=$(awk '/^MemTotal/{print int($2/1048576)}' /proc/meminfo)
    TOTAL_RAM_GB=${TOTAL_RAM_GB:-0}
    if [[ "$TOTAL_RAM_GB" -ge 120 ]]; then
        GTT_MB=98304;  GTT_PAGES=25165824
    elif [[ "$TOTAL_RAM_GB" -ge 60 ]]; then
        GTT_MB=28672;  GTT_PAGES=7340032
    else
        GTT_MB=16384;  GTT_PAGES=4194304
    fi
    detail "RAM: ${TOTAL_RAM_GB}GB → GTT window: $(( GTT_MB / 1024 ))GB"

    # Define our required performance parameters
    PERF_PARAMS="amd_iommu=off amdgpu.gttsize=$GTT_MB amdttm.pages_limit=$GTT_PAGES ttm.pages_limit=$GTT_PAGES amdgpu.ppfeaturemask=0xffffffff"

    # Extract current default line
    CURRENT_LINE=$(sudo grep "^GRUB_CMDLINE_LINUX_DEFAULT=" "$GRUB_FILE" | sed -e 's/^GRUB_CMDLINE_LINUX_DEFAULT=//' -e 's/"//g')

    # Build new line avoiding duplicates. Compare by parameter KEY (before '='),
    # not the full key=value string: a param already tuned to a different value
    # (manually or by a previous run on different hardware) is left untouched —
    # never add a second, conflicting instance of e.g. amdgpu.gttsize.
    NEW_LINE="$CURRENT_LINE"
    for param in $PERF_PARAMS; do
        _key="${param%%=*}"
        if [[ " $CURRENT_LINE " != *" $_key="* && " $CURRENT_LINE " != *" $_key "* ]]; then
            NEW_LINE="$NEW_LINE $param"
        else
            detail "Kernel param '$_key' already configured — keeping existing value"
        fi
    done
    # Trim leading/trailing spaces
    NEW_LINE=$(echo "$NEW_LINE" | xargs)

    run sudo sed -i "s|^GRUB_CMDLINE_LINUX_DEFAULT=.*|GRUB_CMDLINE_LINUX_DEFAULT=\"$NEW_LINE\"|" "$GRUB_FILE"

    run sudo update-grub
    ok "GRUB configuration updated"
    warn "A system reboot is required for kernel parameters to take effect"
}

set_performance_governors() {
    info "Setting performance governors for iGPU and CPU (with persistence)..."

    # CPU Governor: persist via cpufrequtils config
    run sudo bash -c 'echo "GOVERNOR=\"performance\"" | tee /etc/default/cpufrequtils'
    run sudo systemctl restart cpufrequtils
    ok "CPU governor: performance (persistent)"

    # Power profile: persist via systemd drop-in
    if command -v powerprofilesctl &>/dev/null; then
        detail "Configuring persistent power profile via systemd..."
        run sudo mkdir -p /etc/systemd/system/power-profiles-daemon.service.d
        run sudo bash -c 'cat > /etc/systemd/system/power-profiles-daemon.service.d/performance.conf <<'\''EOF'\''
[Service]
ExecStartPost=/usr/bin/powerprofilesctl set performance
EOF'
        run sudo systemctl daemon-reload
        run sudo systemctl restart power-profiles-daemon
        ok "CPU power profile: performance (persistent)"
    else
        detail "powerprofilesctl not found (non-critical)"
    fi

    # iGPU performance level: persist via udev rule (no ROCm required)
    detail "Configuring persistent iGPU performance via udev..."
    run sudo bash -c 'cat > /etc/udev/rules.d/99-amdgpu-perf.rules <<'\''EOF'\''
SUBSYSTEM=="drm", KERNEL=="card[0-9]*", ATTR{device/power_dpm_force_performance_level}=="*", ATTR{device/power_dpm_force_performance_level}="high"
EOF'
    run sudo udevadm control --reload-rules
    run sudo udevadm trigger
    ok "iGPU performance level: high (persistent)"
}

tune_kernel_memory() {
    info "Tuning kernel memory management parameters..."

    run sudo bash -c 'echo madvise | tee /sys/kernel/mm/transparent_hugepage/enabled'
    run sudo sysctl -w vm.swappiness=10
    run sudo sysctl -w vm.overcommit_memory=1

    ok "Kernel memory tuning applied"
}

# ─────────────────────────────────────────────────────────────────────────────
# Main Flow
# ─────────────────────────────────────────────────────────────────────────────

main() {
    banner "AMD Ryzen + Radeon iGPU Vulkan Optimization"

    # Detect hardware
    info "Detecting hardware..."
    CPU_MODEL=$(detect_cpu_model || echo "unknown")
    detail "CPU: $CPU_MODEL"

    HAS_RADEON=0
    if has_radeon_780m; then
        HAS_RADEON=1
        detail "✓ Radeon 780M detected"
    else
        detail "✗ Radeon 780M not detected (may not be visible via lspci on this system)"
    fi

    if ! is_supported_cpu "$CPU_MODEL"; then
        if [[ $FORCE_IGPU -eq 0 ]]; then
            err "This script is optimized for AMD Ryzen 7x40HS/8x45HS + Radeon iGPU"
            err "Detected: $CPU_MODEL"
            err "Use --igpu to force installation anyway"
            exit 1
        fi
    fi

    # Determine mode
    USE_IGPU=0

    if [[ $FORCE_CPU -eq 1 ]]; then
        info "CPU-only mode (--cpu flag)"
        USE_IGPU=0
    elif [[ $FORCE_IGPU -eq 1 ]]; then
        info "iGPU mode (--igpu flag)"
        USE_IGPU=1
    else
        echo ""
        echo "  This system can run optimizations in two modes:"
        echo ""
        echo "  1) CPU only — standard inference (no kernel changes)"
        echo "  2) iGPU accelerated — Vulkan on Radeon iGPU (modifies kernel)"
        echo ""
        printf "  Choose mode [1=cpu, 2=igpu] [default: 1]: "
        read -r -n 1 choice
        echo ""
        choice="${choice:-1}"
        case "$choice" in
            1) USE_IGPU=0 ;;
            2) USE_IGPU=1 ;;
            *)
                err "Invalid choice: $choice"
                exit 1
                ;;
        esac
    fi

    if [[ $USE_IGPU -eq 1 ]]; then
        echo ""
        printf "${YELLOW}${BOLD}WARNING: Experimental${NC}\n"
        printf "This will modify your kernel configuration.\n"
        printf "Recommended for dedicated inference setups only!\n"
        echo ""
        printf "Press ENTER to continue or Ctrl+C to abort: "
        read -r _confirm
        echo ""
    fi

    # Execute optimizations
    if [[ $USE_IGPU -eq 1 ]]; then
        step 1 5 "Installing system packages"
        install_system_packages

        step 2 5 "Adding user to graphics groups"
        add_user_groups

        step 3 5 "Configuring GRUB kernel parameters"
        edit_grub_config

        step 4 5 "Setting performance governors (with persistence)"
        set_performance_governors

        step 5 5 "Tuning kernel memory"
        tune_kernel_memory

        ok "All iGPU optimizations applied!"
        echo ""
        warn "A system reboot is REQUIRED before changes take effect"
        warn "After reboot, Vulkan will have access to the configured GTT memory window"
        warn "You will be prompted to reboot at the end of setup."
    else
        info "CPU-only mode selected — no system changes required"
        ok "Ready for CPU-based inference"
    fi

    show_log_location
}

# Sanity check: must be on Linux
if [[ "$(uname -s)" != "Linux" ]]; then
    err "This script requires Linux. Detected: $(uname -s)"
    exit 1
fi

main "$@"
