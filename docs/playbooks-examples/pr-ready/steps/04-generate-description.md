# Step: Generate PR Description

Inspect the commit history and write a PR description that accurately reflects
the work done. Present it for the user to review and edit before submitting.

## Instructions

1. Get the full commit list for this PR:

   ```bash
   git log origin/{{params.target-branch}}..HEAD --oneline --no-merges
   ```

2. Get the detailed diff stat:

   ```bash
   git diff origin/{{params.target-branch}}..HEAD --stat | tail -20
   ```

3. Read CHANGELOG.md or RELEASING.md if present, to match the project's style:

   ```bash
   ls CHANGELOG.md RELEASING.md 2>/dev/null | head -1
   ```

4. Write the PR description using this template. Base every section on actual
   commits — do not invent or guess features:

```markdown
## Summary

<2-4 bullet points: what this PR does. Start each with a verb. Be specific.>

## Changes

<bullet list of meaningful changes. Group by type: Features / Fixes / Refactors / Chores>

## Testing

- [ ] Tests pass locally (`<detected test command>`)
- [ ] <any manual test steps specific to these changes>
- [ ] No regressions in <key area>

## Notes

<optional: breaking changes, migration steps, deploy order dependencies, anything a reviewer needs to know>

---
🤖 Generated with [OpenMono Playbooks](https://openmono.ai)
```

## Output

Return the complete PR description text so the user can review it at the Review gate.
