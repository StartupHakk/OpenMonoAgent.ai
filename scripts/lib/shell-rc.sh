#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# Helper library for shell RC file operations
#
# Provides utilities for working with shell configuration files (~/.zshrc, etc.)
# especially those that may be symlinks.
# ──────────────────────────────────────────────────────────────────────────────

# Resolve a shell RC file path, following symlinks to the actual file.
# Usage: resolve_shell_rc_path ~/.zshrc
# Output: Prints the resolved path to stdout
#
# This function resolves symlink chains to the actual target file.

resolve_shell_rc_path() {
    local rc_path="$1"

    # If the file doesn't exist, return as-is (caller will handle the error)
    if [ ! -e "$rc_path" ]; then
        echo "$rc_path"
        return 0
    fi

    # Use readlink -f to resolve all symlinks in the chain
    local resolved
    if command -v readlink &>/dev/null; then
        # readlink -f works on both macOS and Linux (GNU coreutils)
        resolved="$(readlink -f "$rc_path" 2>/dev/null || echo "$rc_path")"
    else
        # Fallback if readlink is not available (unlikely)
        resolved="$rc_path"
    fi

    # If resolved path differs from original, we followed a symlink
    if [ "$resolved" != "$rc_path" ]; then
        if command -v detail &>/dev/null; then
            detail "Resolved symlink: $rc_path → $resolved"
        fi
    fi

    echo "$resolved"
}
