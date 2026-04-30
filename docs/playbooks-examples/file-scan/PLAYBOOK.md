---
name: file-scan
version: 1.0.0
description: Creates two workspace files then greps the codebase and reports the results.
trigger: manual
trigger-patterns:
  - "file scan"
  - "run file-scan"
user-invocable: true

steps:
  - id: create-files
    inline-prompt: |
      The create-files script has run and produced this output:

      {{shell:bash {{playbook.base-path}}/scripts/create-files.sh}}

      Report exactly which files were created and their full paths.
    gate: None

  - id: grep-scan
    requires: [create-files]
    inline-prompt: |
      The grep scan script has run and produced this output:

      {{shell:bash {{playbook.base-path}}/scripts/scan.sh}}

      Summarise the results: how many matches were found, which files they
      came from, and print the top 20 matching lines verbatim.
    gate: None
---

You are a workspace assistant. Execute each step's shell script using the Shell
tool, capture the output, and report the results clearly to the user.

- Always print the exact stdout from each script before your summary.
- If a script exits non-zero, report the error and stop.
