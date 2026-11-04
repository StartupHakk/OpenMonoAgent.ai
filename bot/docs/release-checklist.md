# Release checklist

Work through this before any release of the roster, and before any release that changes approvals (plan 11.8).

## The automatic gate

- [ ] `oma-bot release check --repo bot --online` (or `--advisories FILE` offline) passes: no dashes, no secrets, every package pinned, no known critical advisory, image recipes that require digests, and the sign-off below (`--multi` for a web-multi release).

## Build and tests

- [ ] `dotnet build Omabot.sln` with no warnings (warnings are errors).
- [ ] `dotnet test Omabot.sln` passes. Every skipped test names what it needs; read the list in docs/PROGRESS.md and confirm none is skipped for a fixable reason.
- [ ] The live runs listed as skipped were done on the roster host where they matter for this release (Docker, the relay, the inference server, desktops).
- [ ] SEC-01 to SEC-33 pass (live parts on the roster host), and HEALTH-01 to HEALTH-04 pass.
- [ ] REG-02: the OMA checkout outside `bot/` is unchanged.

## Docs

- [ ] Dash check: no em dash or en dash in any roster file (REG-06 runs it).
- [ ] Secret scan: no key, token or password in docs or templates (the docs test runs it).
- [ ] docs/cli.md covers every command (the docs test checks it).
- [ ] docs/PROGRESS.md and README.md describe this release; docs/decisions.md lists every builder decision still waiting for Spencer.

## Configuration and host

- [ ] `oma-bot egress check` passes on the roster host.
- [ ] `oma-bot doctor --online` passes.
- [ ] Image digests are pinned for the release (agent image, gate base images, desktop base).
- [ ] `oma-bot gateway bench --save` and `oma-bot gateway toolbench` were run on the current model and server.

## Web modes

- [ ] Web-single: the HTTPS front recipe is in use, `oma-bot web enable` passes its checks, the owner has a second factor.
- [ ] Web-multi: an outside security review is done (plan 7.20), and the signoff file below is filled in.

## Sign-off

- [ ] Spencer signs off: name, date, version, and the commit, in `bot/docs/signoff.md`.
- [ ] Web-multi only: the same fields plus `outside_review: done` in `~/.omabot/web/release-signoff.md` on the host (docs/multi-user.md).
