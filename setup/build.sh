#!/usr/bin/env bash
# Delegates to scripts/install.sh — kept here so `openmono setup` works
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/scripts/install.sh" "$@"
