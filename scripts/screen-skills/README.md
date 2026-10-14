# screen-skills

macOS screen record → replay → 30m skill distillation.

Personal meta-skill (rubric + agent instructions):

`~/.cursor/skills/screen-workflow-capture/SKILL.md`

```bash
./scripts/screen-skills/screen-skills help
./scripts/screen-skills/screen-skills doctor
./scripts/screen-skills/screen-skills record --interval 30
./scripts/screen-skills/screen-skills replay latest
./scripts/screen-skills/screen-skills distill --minutes 30
./scripts/screen-skills/screen-skills loop-help
./scripts/screen-skills/screen-skills install-launchd   # optional unattended distill ticks
```

Optional symlink:

```bash
ln -sf "$PWD/scripts/screen-skills/screen-skills" /usr/local/bin/screen-skills
```

Data: `~/Library/Application Support/screen-skills/`
