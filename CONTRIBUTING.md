# Contributing to OpenMono

OpenMono is early and moving fast. Contributions are welcome — but read this first so your PR doesn't get closed.

---

## What's welcome

- Bug fixes
- New built-in tools
- New LLM providers
- LSP server additions
- Playbook improvements
- Docker / install / setup fixes
- Docs and examples

UI changes or anything touching the core agentic loop should have an issue discussion first.

---

## Before opening a PR

**Open an issue first** for anything non-trivial. Comment on an issue to get it assigned before starting work. PRs without a linked issue may be closed without review.

Look for issues tagged `help wanted` or `good first issue` if you're just getting started.

---

## Development setup

**Requirements:** .NET 10 SDK, Docker

```bash
git clone https://github.com/your-org/openmono
cd openmono/src/OpenMono.Cli
dotnet build
dotnet run -- --endpoint http://localhost:7474 --classic
```

Run tests:
```bash
dotnet test
```

---

## Pull request standards

- **Link the issue** — use `Fixes #123` or `Closes #123` in the PR description
- **Keep it small** — one thing per PR
- **Explain the problem**, not just the change
- **UI changes** need a screenshot or short video
- **Logic changes** need a note on how you tested it
- Avoid lengthy AI-generated descriptions

**Commit format** — follow conventional commits:

```
feat: add web search tool
fix: resolve doom-loop false positive on empty output
docs: update dual-box setup guide
chore: bump llama.cpp version
refactor: simplify tool pipeline cache logic
```

---

## Code style

Standard .NET conventions. Run `dotnet format` before committing. No enforced linter beyond that.

---

## Reporting bugs

Open an issue with:

- OS, hardware (GPU/CPU, VRAM/RAM)
- OpenMono version (`openmono --version`)
- Steps to reproduce
- Relevant output from `--verbose`
