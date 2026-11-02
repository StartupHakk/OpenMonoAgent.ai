# Git auth profiles

The agent container commits and pushes using git credentials. By default the
launcher passes the host's credentials through read-only (`~/.gitconfig`,
`~/.ssh`, `~/.git-credentials`, plus the SSH agent socket) — one identity per
machine, whatever the host happens to have.

**Git auth profiles** let you define multiple named credential sets in
`~/.openmono/settings.json` and pick which one a session uses. Pick a profile
per session with `openmono agent --git-profile NAME` (or the
`OPENMONO_GIT_PROFILE` env var), or make one the standing default with
`git.default` in settings.json. No profile selected = legacy host
pass-through, exactly as before.

## Configuration

`~/.openmono/settings.json`:

```jsonc
{
  "git": {
    // Standing default for sessions launched without --git-profile.
    // Omit to keep host pass-through by default.
    "default": "work",

    "profiles": {
      "work": {
        "type": "ssh",
        "ssh_key": "~/.ssh/id_ed25519_work",   // must live under ~/.ssh
        "user": "Jane Dev",
        "email": "jane@work.dev"
      },
      "personal": {
        "type": "https",
        "host": "github.com",                  // default when omitted
        "token_env": "GITHUB_PAT_PERSONAL",    // preferred: keep the token
                                               // out of settings.json
        // "token": "ghp_...",                  // literal — only if the token
                                               // must live in the file
        "user": "jane-dev",
        "email": "jane@example.com"
      }
    }
  }
}
```

| Key | Applies to | Meaning |
|-----|-----------|---------|
| `type` | both | `"ssh"` or `"https"` |
| `ssh_key` | ssh | Private key path, `~`-expanded. **Must be under `~/.ssh`** — that is the directory the container launcher mounts read-only, so keys outside it are unreachable and rejected at startup |
| `token` | https | Literal token (personal access token, app password, …) |
| `token_env` | https | Name of an env var holding the token. Read at startup; takes precedence over `token`. The token never lands in settings.json or on any command line |
| `host` | https | Credential scope, default `github.com`. GitLab/Bitbucket/Gitea work the same way |
| `user` / `email` | both | `user.name` / `user.email` applied for the session (commits) |

## Adding a profile

You don't have to hand-edit JSON. From inside a session, the `/git add`
wizard walks you through one profile end to end and writes it into
`~/.openmono/settings.json` (preserving every other section of the file):

```
/git add            # start the wizard
/git add work       # same, pre-fills the profile name
```

The steps, each with a sensible default in brackets:

1. **Name** — letters/digits/`-`/`_`. If the name already exists you're asked
   whether to overwrite it (or pick another name).
2. **Type** — `1) ssh` (name an existing key under `~/.ssh`; available keys are
   listed) or `2) https` (personal access token for any HTTPS git host).
3. **Credential** —
   - *ssh*: the private key path. A bare filename is accepted and stored as
     `~/.ssh/<name>`. Keys outside `~/.ssh` are rejected — that directory is
     the one the launcher mounts read-only into the container.
   - *https*: the host (default `github.com`), then the token source —
     **an env-var name** (recommended: the secret never touches the file; the
     variable must be set before the agent launches) **or paste the token
     now** (masked input; stored in the file, which is locked to `0600` when
     the wizard creates it).
4. **Identity** — `user.name` and `user.email`, prefilled from your host's
     `git config` so the common case is two Enters.

After a confirm step the profile is saved, and you're asked to activate it for
the current session (equivalent to `/git <name>`). Non-interactive runs
(piped stdin) skip the wizard and point you back to the hand-edited file above.

## Selecting a profile

Resolution order (highest first):

1. **The session's own saved choice** — the last explicit selection made for
   this session via `/git <name>`, `/git off`, or the ACP `POST /git` endpoint.
   It is written into the session record, so a **resumed** session comes back
   on exactly that profile — including an explicit `/git off`, which must not
   silently re-enable `git.default`.
2. `--git-profile NAME` / `OPENMONO_GIT_PROFILE` — the launch-time selection.
   Applies to a session that has not chosen a profile of its own yet.
3. `git.default` in settings.json — the standing default.
4. Nothing selected → legacy pass-through: the host's own git config and
   credentials, no profile env vars set.

Once a session has made an explicit choice (step 1), it is "stuck" to that
profile for its whole lifetime — a later relaunch with a different
`--git-profile` / env var does **not** override it for that session. This
matches the intent that a session's profile is the session's, not the
launcher's.

A selected name (from any step) that is missing or malformed in the current
settings.json does **not** kill the session: the agent warns at startup
(`git profile 'nope' not found … — continuing with host git credentials`) and
falls back to pass-through rather than silently switching to a different
profile (which could push the wrong credentials).

```bash
openmono agent                     # git.default, or host pass-through
openmono agent --git-profile personal
OPENMONO_GIT_PROFILE=work openmono agent
openmono agent --host --git-profile work   # works in host mode too
```

The flag forwards only the **name** into the sandbox (as an env var). The
credentials themselves are resolved inside the container from settings.json
and the host environment, so no token or key ever appears on the `docker run`
command line or in process listings.

## Hot-swapping mid-session

The profile a session uses is stored on the session itself, so it can be
changed at any time — the new credentials apply to the next `git` command the
session runs, with no restart. Under ACP each session is independent, so two
sessions can carry different profiles at once.

- **TUI** — the `/git` command:
  ```
  /git              # list configured profiles, mark the active one
  /git personal     # switch to the 'personal' profile
  /git off          # clear → legacy host pass-through
  /git add          # wizard: create a new profile (see above)
  ```
- **ACP** — `POST /api/v1/sessions/{id}/git` with `{ "profile": "<name>" }`
  (or `{ "profile": null }` for pass-through). Returns
  `{ ok, git_profile, error? }`; an unknown name is `ok:false` with a readable
  `error`, matching the `/host` endpoint's guidance style.

The TUI selection is what `/git` writes; ACP sessions start from the profile
resolved at startup (`OPENMONO_GIT_PROFILE` / `git.default`) and the endpoint
overwrites it per session. Every choice — in either interface — is persisted
with the session, so it survives a restart and is restored on resume (see the
resolution order above).

## Per-directory (project) config

The `git` section in a project's `.openmono/settings.json` is merged on top
of the user-global one (same layering as every other setting), so a repo can
carry its own profiles and/or default:

```jsonc
// /some/project/.openmono/settings.json
{
  "git": {
    "default": "work",
    "profiles": {
      "work": {
        "type": "https",
        "token_env": "GH_TOKEN",
        "email": "me@work"
      }
    }
  }
}
```

Discovery is from the directory the session was started in (it does not walk
up to parent directories yet).

## How it works

The selected profile's env is layered onto **each `git` child at spawn time**
(the session carries the profile name; `BashTool` resolves it per child):

- **HTTPS** — `credential.https://<host>.username` / `.password` supplied via
  git's env config (`GIT_CONFIG_COUNT` / `GIT_CONFIG_KEY_n` /
  `GIT_CONFIG_VALUE_n`), plus `credential.helper=!true` so a host-side helper
  (store, cache, keychain) cannot shadow the profile credentials.
- **SSH** — `GIT_SSH_COMMAND` overridden to `ssh -i <key> -o IdentitiesOnly=yes
  -o BatchMode=yes`, so exactly the profile key is offered.
- **Identity** — `user.name` / `user.email` from the profile, applied for the
  session's commits.

The launcher's own `safe.directory` env config is preserved (appended to, not
clobbered); any inherited `credential.helper` is dropped so it cannot shadow
the profile.

### PATs the host doesn't have

This is the scenario profiles exist for: a personal token on the host shell
(`export GITHUB_PAT_PERSONAL=…`) that the host's git itself never uses. The
agent reads the named env var at startup and scopes it to the profile's host.
The token is visible to the agent process (and therefore to the model it
controls) — treat `token_env` profiles as a trust boundary, and prefer
repo-scoped tokens.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `git profile 'x' not found` warning at startup | Typo in `--git-profile`/`git.default`, or the profile is missing from settings.json (list what's configured with `/git`, or create one with `/git add`) |
| `token env var 'FOO' is not set` warning | Export the var before launching (`export FOO=…`), or switch the profile to a literal `token` |
| `ssh_key '…' must live under ~/.ssh` warning | Move the key into `~/.ssh` (e.g. a `deploy_<repo>` key) and update `ssh_key` |
| `Authentication failed` on push with an https profile | Check the token's scopes (repo / read+write), its expiry, and that `host` matches the remote's host |
| `Permission denied (publickey)` with an ssh profile | The key is mounted read-only from the host — verify it's the right key and belongs to the right account: `ssh -i ~/.ssh/<key> -T git@github.com` on the host |
| Commits use the wrong author | Set `user` / `email` on the profile; the profile identity overrides the host `~/.gitconfig` |
