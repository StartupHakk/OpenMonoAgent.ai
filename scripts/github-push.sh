#!/usr/bin/env bash
# Push the current branch. Prefer a logged-in GitHub CLI for HTTPS remotes.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_DIR"

BRANCH="$(git branch --show-current)"
echo "Pushing branch: $BRANCH"

push_ok() {
    echo "OK: pushed $BRANCH to origin"
    exit 0
}

# One-shot credential helper. Does not rewrite the repo's git config.
if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    if git -c credential.helper='!gh auth git-credential' push -u origin HEAD; then
        push_ok
    fi
fi

if git push -u origin HEAD; then
    push_ok
fi

REMOTE="$(git remote get-url origin 2>/dev/null || true)"
echo ""
echo "Push failed."
echo ""
if [[ "$REMOTE" == https://* ]]; then
    echo "origin is HTTPS and this session has no GitHub credentials."
    echo "  gh auth login"
    echo "  ./scripts/github-push.sh"
    echo ""
fi
echo "If you use SSH instead:"
echo "  1. Copy your public key:"
echo "       pbcopy < ~/.ssh/id_ed25519.pub"
ssh-keygen -lf ~/.ssh/id_ed25519.pub 2>/dev/null || true
echo "  2. Add it at: https://github.com/settings/ssh/new"
echo "     Title suggestion: openmono-imac"
echo "  3. Re-run:  ./scripts/github-push.sh"
echo ""
exit 1
