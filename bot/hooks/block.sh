#!/bin/bash
# SPDX-License-Identifier: AGPL-3.0-or-later
# Blocks a tool call whose input matched a roster pattern (self approval,
# docker socket, metadata address and similar). Exit 2 blocks the tool.
reason="${1:-blocked_input}"
if [[ ! "$reason" =~ ^[a-z_]{1,40}$ ]]; then reason=blocked_input; fi
echo "roster: blocked ($reason). Bots may not call ACP, the roster, docker or metadata addresses." >&2
exit 2
