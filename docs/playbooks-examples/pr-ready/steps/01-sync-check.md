# Step: Sync Check

Verify the branch is in a valid state to open a PR from.

## Instructions

1. Get the current branch name:

   ```bash
   git symbolic-ref --short HEAD
   ```

2. Guard against opening a PR from the target branch itself:
   If current branch equals `{{params.target-branch}}`, abort with:
   "Cannot open a PR from '{{params.target-branch}}' — create a feature branch first."

3. Check how far ahead/behind the branch is:

   ```bash
   git fetch origin {{params.target-branch}} --quiet
   git rev-list --left-right --count origin/{{params.target-branch}}...HEAD
   ```

   - If 0 commits ahead: abort — "Branch has no new commits ahead of {{params.target-branch}}."
   - If behind: warn the user they should rebase, but allow continuation.

4. List commits that will be in the PR:

   ```bash
   git log origin/{{params.target-branch}}..HEAD --oneline --no-merges
   ```

## Output

```
Branch          : <current branch>
Target          : {{params.target-branch}}
Commits ahead   : <count>
Commits behind  : <count> (rebase recommended if > 0)

Commits in this PR
──────────────────
<sha> <subject>
<sha> <subject>
...
```
