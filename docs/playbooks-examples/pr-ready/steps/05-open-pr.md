# Step: Open Pull Request

Open the PR using the description approved at the Review gate.

## Instructions

1. Verify `gh` CLI is available and authenticated:

   ```bash
   gh auth status 2>&1
   ```

   If not authenticated, abort: "Run `gh auth login` first, then retry."

2. Build the `gh pr create` command:

   ```bash
   gh pr create \
     --base {{params.target-branch}} \
     --title "<title derived from branch name or first commit subject>" \
     --body "<approved PR description from state.pr_description>" \
     {{shell:[ "{{params.draft}}" = "true" ] && echo "--draft" || echo ""}} \
     {{shell:[ -n "{{params.labels}}" ] && echo "--label {{params.labels}}" || echo ""}} \
     {{shell:[ -n "{{params.reviewers}}" ] && echo "--reviewer {{params.reviewers}}" || echo ""}}
   ```

3. Capture the PR URL from the output.

4. Report the result.

## Output

```
Pull Request Opened
───────────────────
Title    : <pr title>
Base     : {{params.target-branch}}
Branch   : <current branch>
Draft    : {{params.draft}}
Labels   : {{params.labels}}
Reviewers: {{params.reviewers}}

URL      : <pr url>
```
