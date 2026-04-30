# Step: Analyze Changes Since Last Release

Your goal in this step is to build a complete picture of what changed since the
last git tag so the subsequent steps can produce an accurate changelog and pick
the correct semver bump.

## Instructions

1. Find the most recent release tag:
   ```
   git describe --tags --abbrev=0
   ```
   If no tag exists, use the first commit: `git rev-list --max-parents=0 HEAD`

2. List all commits between that tag and HEAD:
   ```
   git log <last-tag>..HEAD --oneline --no-merges
   ```

3. For each commit, classify it into one of these buckets using conventional
   commit prefixes (feat, fix, docs, chore, refactor, test, perf, style, ci,
   build, revert) and the presence of `BREAKING CHANGE` in the body:

   | Bucket          | Semver impact |
   |-----------------|---------------|
   | BREAKING CHANGE | major         |
   | feat            | minor         |
   | fix / perf      | patch         |
   | all others      | none          |

4. Count commits per bucket and identify the highest semver impact.

5. List the changed files across all commits:
   ```
   git diff <last-tag>..HEAD --stat
   ```

## Output format

Produce a structured report:

```
Last tag        : <tag or "none">
Commits since   : <count>
Changed files   : <count>
Highest impact  : <major | minor | patch | none>

Breakdown
─────────
Breaking changes : <count> commits
Features         : <count> commits
Bug fixes        : <count> commits
Other            : <count> commits

Notable commits
───────────────
<sha> <type>: <subject>
...
```

Do not write any files in this step — only produce the analysis report above.
