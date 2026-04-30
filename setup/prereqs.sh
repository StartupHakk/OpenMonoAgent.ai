#!/usr/bin/env bash
# Delegates to scripts/install_prereqs.sh — kept here so `openmono setup` works
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/scripts/install_prereqs.sh" "$@"
