# Step: Generate Changelog Entry

Using the analysis from the previous step, write a new changelog entry and
prepend it to `CHANGELOG.md` (creating the file if it does not exist).

## Changelog format

Follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) strictly.

```markdown
## [{{parameters.tag-prefix}}{{state.new_version}}] - {{date}}

### Added
- <Feature description derived from feat: commits>

### Changed
- <Behaviour change descriptions>

### Fixed
- <Bug fix descriptions derived from fix: commits>

### Deprecated
- <Items deprecated in this release>

### Removed
- <Items removed in this release>

### Security
- <Security-relevant changes>
```

Rules:
- Only include sections that have at least one entry. Omit empty sections.
- Write each entry as a user-facing sentence, not a raw commit subject.
  Translate `fix(auth): null-check token before decode` → `Fixed a crash when
  decoding an unauthenticated request token.`
- Do not mention internal refactors, test changes, or CI tweaks unless they
  directly affect users.
- If there are BREAKING CHANGES, add a `### Breaking Changes` section at the
  top of the entry with a migration note.

## Write the file

1. Read the existing `CHANGELOG.md` (or start with an empty string if absent).
2. Prepend the new entry immediately after any `# Changelog` heading (or at the
   top of the file if no heading exists).
3. Write the updated file back.
4. Report: "Changelog updated — N entries written." with the section names used.

## Gate note

This step has a **Review** gate. After generating the changelog entry, you will
present it to the user for approval before writing it to disk.
