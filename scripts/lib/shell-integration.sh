#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# OpenMono.ai — Shell Integration Helper
#
# Handles cross-platform, cross-shell PATH setup for the openmono command.
# Ensures persistence across shell reloads and system reboots.
#
# Sourced by openmono CLI during setup. Do not execute directly.
# Requires: log.sh functions (err, warn, detail, ok, info)
# ──────────────────────────────────────────────────────────────────────────────

# Fallback logging functions if not already sourced
if ! declare -f err &>/dev/null; then
    err()    { printf "  \033[0;31m✗\033[0m  %s\n" "$*" >&2; }
    warn()   { printf "  \033[1;33m⚠\033[0m  %s\n" "$*"; }
    detail() { printf "     %s\n" "$*"; }
    ok()     { printf "  \033[0;32m✓\033[0m  %s\n" "$*"; }
    info()   { printf "  \033[38;2;163;255;102mℹ\033[0m  %s\n" "$*"; }
fi

# install_to_system_path — install openmono to /usr/local/bin (primary method)
# Works on: macOS, Linux, and other Unix-like systems
# Returns: 0 if successful, 1 if not writable, 2 if creation failed
install_to_system_path() {
    local install_dir="$1"
    local target="/usr/local/bin/openmono"

    if [ ! -x "$install_dir/openmono" ]; then
        err "Source not executable: $install_dir/openmono"
        return 2
    fi

    # Check if /usr/local/bin is writable
    if [ ! -w /usr/local/bin ]; then
        detail "Insufficient permissions for /usr/local/bin (non-writable)"
        # Try with sudo if available
        if command -v sudo &>/dev/null; then
            detail "Attempting with sudo..."
            if sudo ln -sf "$install_dir/openmono" "$target" 2>/dev/null; then
                detail "Symlinked $target (with sudo)"
                return 0
            else
                detail "Sudo symlink also failed"
                return 1
            fi
        fi
        return 1
    fi

    # /usr/local/bin is writable, create symlink
    if ln -sf "$install_dir/openmono" "$target" 2>/dev/null; then
        detail "Symlinked $target"
        return 0
    else
        err "Failed to create symlink at $target"
        return 2
    fi
}

# update_shell_rc_file — add openmono PATH entry to a shell rc file
# Handles macOS (BSD sed) and Linux (GNU sed) differences
# Removes any existing OpenMono block and adds a fresh one
# Arguments: rc_file install_dir
# Returns: 0 if successful, 1 if file doesn't exist
update_shell_rc_file() {
    local rc_file="$1"
    local install_dir="$2"

    [ -f "$rc_file" ] || return 1

    # Create backup
    cp "$rc_file" "${rc_file}.openmono.bak" 2>/dev/null || true

    # Remove any existing OpenMono block more robustly
    # This handles both cases: with/without trailing blank line
    local temp_file="${rc_file}.tmp"
    {
        # Output all lines EXCEPT those in the OpenMono block
        local in_block=0
        while IFS= read -r line; do
            if [[ "$line" == "# OpenMono.ai"* ]]; then
                in_block=1
                continue
            fi
            # Exit block when we hit a non-empty line after the block started
            if [ "$in_block" -eq 1 ] && [ -z "$line" ]; then
                in_block=0
                continue
            fi
            if [ "$in_block" -eq 0 ]; then
                echo "$line"
            fi
        done < "$rc_file"
    } > "$temp_file"

    # Add fresh OpenMono block
    {
        echo ""
        echo "# OpenMono.ai — Command integration"
        echo "export PATH=\"$install_dir:\$PATH\""
    } >> "$temp_file"

    # Atomically replace the original file
    if mv "$temp_file" "$rc_file" 2>/dev/null; then
        detail "Updated $(basename "$rc_file")"
        return 0
    else
        # Restore backup if something went wrong
        if [ -f "${rc_file}.openmono.bak" ]; then
            cp "${rc_file}.openmono.bak" "$rc_file"
        fi
        err "Failed to update $rc_file"
        return 1
    fi
}

# setup_shell_integration — add openmono to PATH via shell rc files
# Handles all major shells: bash, zsh, fish, and fallback
# Arguments: install_dir
# Returns: 0 if at least one file was updated, 1 if all failed
setup_shell_integration() {
    local install_dir="$1"
    local shell_name
    shell_name=$(basename "$SHELL")

    local -a rc_files=()
    local -a attempted=()
    local -a succeeded=()

    # Determine which rc files to update based on current shell
    case "$shell_name" in
        zsh)
            rc_files=("$HOME/.zshrc" "$HOME/.zprofile")
            ;;
        bash)
            rc_files=("$HOME/.bash_profile" "$HOME/.bashrc")
            ;;
        fish)
            rc_files=("$HOME/.config/fish/config.fish")
            ;;
        *)
            # Fallback: only update rc files that already exist
            # Don't create files for shells the user doesn't use
            rc_files=()
            for candidate in "$HOME/.zshrc" "$HOME/.zprofile" "$HOME/.bash_profile" "$HOME/.bashrc" "$HOME/.config/fish/config.fish"; do
                [ -f "$candidate" ] && rc_files+=("$candidate")
            done
            ;;
    esac

    # Ensure parent directories exist and create rc files if needed
    # (but only for the detected shell, not for unknown shells in fallback)
    for rc_file in "${rc_files[@]}"; do
        local dir
        dir=$(dirname "$rc_file")
        mkdir -p "$dir" 2>/dev/null || true

        if [ ! -f "$rc_file" ]; then
            # For known shells (not fallback), create the file
            # For fallback, we only process existing files (see case statement above)
            if [ "$shell_name" != "*" ]; then
                touch "$rc_file" 2>/dev/null || true
            fi
        fi

        attempted+=("$rc_file")
        if update_shell_rc_file "$rc_file" "$install_dir"; then
            succeeded+=("$rc_file")
        fi
    done

    if [ ${#succeeded[@]} -gt 0 ]; then
        detail "Updated ${#succeeded[@]} shell rc file(s)"
        return 0
    else
        warn "Could not update any shell rc files"
        return 1
    fi
}

# verify_openmono_accessible — check if openmono command is accessible
# Tests: direct invocation, via symlink, and via PATH
# Arguments: install_dir
verify_openmono_accessible() {
    local install_dir="$1"

    # Test 1: Can we run it directly?
    if "$install_dir/openmono" --version &>/dev/null 2>&1; then
        detail "openmono is accessible directly (PATH: $install_dir)"
        return 0
    fi

    # Test 2: Is /usr/local/bin/openmono available?
    if command -v openmono &>/dev/null; then
        detail "openmono is accessible via PATH"
        return 0
    fi

    # Test 3: Is the symlink present?
    if [ -L /usr/local/bin/openmono ]; then
        detail "openmono symlink exists at /usr/local/bin/openmono"
        return 0
    fi

    return 1
}

# print_setup_instructions — show user what to do if manual setup is needed
# Arguments: install_dir
print_setup_instructions() {
    local install_dir="$1"

    echo ""
    info "Manual shell integration (if auto-setup didn't work):"
    echo ""
    echo "  Option 1: Add to your shell rc file (~/.bashrc, ~/.zshrc, etc.):"
    echo "    export PATH=\"$install_dir:\$PATH\""
    echo ""
    echo "  Option 2: Create a symlink (may require sudo):"
    echo "    sudo ln -sf \"$install_dir/openmono\" /usr/local/bin/openmono"
    echo ""
    echo "  Then reload your shell:"
    echo "    exec -l \$SHELL"
    echo ""
}
