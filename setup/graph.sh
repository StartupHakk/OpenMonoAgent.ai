#!/usr/bin/env bash
# Delegates to scripts/setup-graph.sh — kept here so `openmono graph` works
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/scripts/setup-graph.sh" "$@"
