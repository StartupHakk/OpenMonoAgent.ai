#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Shared logging and command-execution helpers
#
# Sourced by install.sh and install_prereqs.sh. Do not execute directly.
#
# Modes:
#   Default      — clean step-based progress messages
#   Verbose      — shows all command output (enable via OPENMONO_VERBOSE=1)
#
# All output (both modes) is appended to OPENMONO_LOG_FILE for post-mortem.
# ──────────────────────────────────────────────────────────────────────────────

# Colors (disabled when not a TTY)
if [ -t 1 ]; then
    RED=$'\033[0;31m'
    GREEN=$'\033[0;32m'
    YELLOW=$'\033[1;33m'
    BLUE=$'\033[38;2;163;255;102m'
    CYAN=$'\033[0;36m'
    DIM=$'\033[2m'
    BOLD=$'\033[1m'
    NC=$'\033[0m'
else
    RED=""; GREEN=""; YELLOW=""; BLUE=""; CYAN=""; DIM=""; BOLD=""; NC=""
fi

# ── Terminal cleanup on exit ─────────────────────────────────────────────────
# Ensures cursor is visible and terminal state is reset on exit or interrupt

_cleanup_terminal() {
    # Show cursor (in case it was hidden)
    printf '\033[?25h'
    # Reset all attributes
    printf '\033[0m'
    # Clear any partial line
    printf '\n'
}

# Set trap for common exit signals
trap _cleanup_terminal EXIT INT TERM HUP

# ERR trap — fires when any command exits non-zero under set -e.
# Logs the failing command and line number so the log is always useful.
# Does not fire for intentionally handled errors (||, if, &&).
set -o errtrace
_trap_err() {
    local exit_code=$?
    local line="${BASH_LINENO[0]:-?}"
    local cmd="${BASH_COMMAND:-?}"
    _log "FATAL: exit $exit_code at line $line — $cmd"
}
trap '_trap_err' ERR

OPENMONO_VERBOSE="${OPENMONO_VERBOSE:-0}"

# Log file: shared across install_prereqs.sh and install.sh within one setup run
if [ -z "${OPENMONO_LOG_FILE:-}" ]; then
    OPENMONO_LOG_DIR="${OPENMONO_LOG_DIR:-$HOME/.openmono/logs}"
    mkdir -p "$OPENMONO_LOG_DIR"
    OPENMONO_LOG_FILE="$OPENMONO_LOG_DIR/setup-$(date +%Y%m%d-%H%M%S).log"
    export OPENMONO_LOG_FILE
    : > "$OPENMONO_LOG_FILE"
fi

# ── Internal ────────────────────────────────────────────────────────────────

_log() {
    printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$OPENMONO_LOG_FILE"
}

# ── Public helpers ──────────────────────────────────────────────────────────

# banner — top-of-script title
banner() {
    local title="$1"
    local width=60
    local pad=$(( (width - ${#title} - 2) / 2 ))
    echo ""
    printf "${BOLD}${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 $width))"
    printf "${BOLD}${BLUE}%*s %s %*s${NC}\n" $pad "" "$title" $pad ""
    printf "${BOLD}${BLUE}%s${NC}\n" "$(printf '─%.0s' $(seq 1 $width))"
    echo ""
    _log "=== $title ==="
}

# step — numbered section header, e.g. step 3 8 "Download model"
step() {
    local n="$1" total="$2"
    shift 2
    local msg="$*"
    echo ""
    printf "${BOLD}${BLUE}[%s/%s]${NC} ${BOLD}%s${NC}\n" "$n" "$total" "$msg"
    _log "STEP [$n/$total]: $msg"
}

info()  { printf "  ${BLUE}ℹ${NC}  %s\n" "$*"; _log "INFO: $*"; }
ok()    { printf "  ${GREEN}✓${NC}  %s\n" "$*"; _log "OK: $*"; }
warn()  { printf "  ${YELLOW}⚠${NC}  %s\n" "$*"; _log "WARN: $*"; }
err()   { printf "  ${RED}✗${NC}  %s\n" "$*" >&2; _log "ERROR: $*"; }

# detail — only shown in verbose mode, always logged
detail() {
    _log "DETAIL: $*"
    if [ "$OPENMONO_VERBOSE" = "1" ]; then
        printf "     ${DIM}%s${NC}\n" "$*"
    fi
}

# run — execute a command, log all output
#   Verbose: stream output live AND to log
#   Default: suppress output (still captured in log)
# Returns the command's exit code.
run() {
    _log "RUN: $*"
    if [ "$OPENMONO_VERBOSE" = "1" ]; then
        printf "     ${DIM}\$ %s${NC}\n" "$*"
        "$@" 2>&1 | tee -a "$OPENMONO_LOG_FILE"
        return "${PIPESTATUS[0]}"
    else
        "$@" >> "$OPENMONO_LOG_FILE" 2>&1
    fi
}

# run_live — always stream output to user AND log (for downloads, builds)
run_live() {
    _log "RUN_LIVE: $*"
    "$@" 2>&1 | tee -a "$OPENMONO_LOG_FILE"
    return "${PIPESTATUS[0]}"
}

# show_log_tail — print last N lines of the log (for error diagnostics)
show_log_tail() {
    local lines="${1:-20}"
    echo ""
    printf "${DIM}─── Last %s log lines ───${NC}\n" "$lines"
    tail -"$lines" "$OPENMONO_LOG_FILE" | sed "s/^/    /"
    printf "${DIM}─────────────────────────${NC}\n"
    echo ""
}

# die — print error, show log tail, exit
die() {
    err "$*"
    show_log_tail 30
    echo ""
    err "Full log: $OPENMONO_LOG_FILE"
    exit 1
}

# show_summary — print log-file pointer at the end of a run
show_log_location() {
    echo ""
    printf "${DIM}Full log: %s${NC}\n" "$OPENMONO_LOG_FILE"
}

# get_shell_rc_files — output rc file paths for the user's current shell
# Outputs one file per line
get_shell_rc_files() {
    local shell_name
    shell_name=$(basename "$SHELL")

    case "$shell_name" in
        zsh)
            echo "$HOME/.zshrc"
            echo "$HOME/.zprofile"
            ;;
        bash)
            echo "$HOME/.bash_profile"
            echo "$HOME/.bashrc"
            ;;
        fish)
            echo "$HOME/.config/fish/config.fish"
            ;;
        *)
            echo "$HOME/.bashrc"
            echo "$HOME/.bash_profile"
            echo "$HOME/.zshrc"
            ;;
    esac
}

# ── Setup preferences persistence ────────────────────────────────────────────
# Stores role and GPU mode choices across script restarts (e.g. mid-install reboot).

_SETUP_PREFS_FILE="$HOME/.openmono/.setup_prefs"

_save_setup_pref() {
    local key="$1" val="$2"
    mkdir -p "$(dirname "$_SETUP_PREFS_FILE")"
    if [[ -f "$_SETUP_PREFS_FILE" ]]; then
        grep -v "^${key}=" "$_SETUP_PREFS_FILE" > "${_SETUP_PREFS_FILE}.tmp" && \
            mv "${_SETUP_PREFS_FILE}.tmp" "$_SETUP_PREFS_FILE" || true
    fi
    echo "${key}=${val}" >> "$_SETUP_PREFS_FILE"
}

clear_setup_prefs() {
    rm -f "$_SETUP_PREFS_FILE"
}

# flush_stdin — discard input buffered before an interactive prompt.
# A long step (model/driver download, apt, docker build) can accumulate stray
# keystrokes — usually impatient Enter presses — in the terminal's input
# buffer. Without draining them, the next `read` consumes one immediately and
# silently auto-selects the prompt's default, so the user never sees the prompt
# they just "answered". Only drains a real TTY, so piped/unattended stdin
# (heredocs, CI) is left intact. No-ops on shells too old for `read -t <frac>`.
flush_stdin() {
    [ -t 0 ] || return 0
    local _discard
    # Tiny non-zero timeout: each buffered line is consumed at once; when the
    # buffer empties, read blocks for the timeout, fails (>128), and we stop.
    while read -r -t 0.1 _discard 2>/dev/null; do :; done
    return 0
}

# role_prompt — interactive role selection (used during setup if OPENMONO_ROLE not set)
# Sets $OPENMONO_ROLE to: full, inference, or agent
role_prompt() {
    if [[ -n "${OPENMONO_ROLE:-}" ]]; then
        return 0  # Already set via env/flag, skip prompt
    fi

    # Restore from a previous interrupted run
    if [[ -f "$_SETUP_PREFS_FILE" ]]; then
        # shellcheck source=/dev/null
        source "$_SETUP_PREFS_FILE"
        if [[ -n "${OPENMONO_ROLE:-}" ]]; then
            echo ""
            info "Restoring saved role from previous session: ${BOLD}$OPENMONO_ROLE${NC}"
            export OPENMONO_ROLE
            return 0
        fi
    fi

    # Drop keystrokes buffered during any prior long step so they don't
    # auto-answer this prompt.
    flush_stdin
    _role_invalid=0
    while true; do
        echo ""
        [ "$_role_invalid" -eq 1 ] && printf "  ${RED}Please enter 1, 2, or 3.${NC}\n\n"
        echo "  What do you want to install on this machine?"
        echo ""
        echo "  1) Both — agent + inference server on one box (single-box mode)"
        echo "  2) Inference server only — dedicated box that runs the model (GPU or CPU)"
        echo "             (pair with a separate agent box via openmono tunnel)"
        echo "  3) Agent only — laptop/workstation that talks to a remote inference server"
        echo "             (dual-box mode; point at inference box with openmono config)"
        echo ""
        printf "  Enter 1, 2 or 3 [default: 1]: "
        read -r -n 1 _role_choice
        echo ""
        _role_choice="${_role_choice:-1}"
        case "$_role_choice" in
            1) OPENMONO_ROLE=full      ; break ;;
            2) OPENMONO_ROLE=inference ; break ;;
            3) OPENMONO_ROLE=agent     ; break ;;
            *) _role_invalid=1 ;;
        esac
    done
    export OPENMONO_ROLE
    _save_setup_pref "OPENMONO_ROLE" "$OPENMONO_ROLE"
    echo ""
}
