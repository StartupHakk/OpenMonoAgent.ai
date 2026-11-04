# Secrets on the roster host

Everything stays on the roster host, in `~/.omabot/secrets/` (folder 0700, files 0600). Nothing here is ever committed, printed, or sent to cloud inference.

| What | Where | Command |
|---|---|---|
| Inference key for the main backend | `secrets/inference.key` | `oma-bot secrets set inference < keyfile` |
| Key for another backend | the backend's `key_file` (must be under `secrets/`) | `oma-bot secrets set backend-NAME < keyfile` |
| FRPC credentials if this host ever runs its own tunnel | `secrets/relay/NAME` | `oma-bot secrets set relay-NAME < file` |
| A secret for one bot | `secrets/bots/<bot>/NAME`, mounted read only at `/run/secrets/bot/NAME` in that bot only | `oma-bot secrets set NAME --bot BOT < file` or `--file PATH` |
| Per bot gateway token | `secrets/bots/<bot>.token` | `oma-bot secrets rotate-bot BOT` |

Rules the code enforces:

- Values come from standard input or `--file`, never from a command line argument (`--value` is refused), so `ps` and shell history never show them.
- Empty, short, repeated and placeholder values are refused, and the refusal never prints the value. `oma-bot serve` refuses to start with a placeholder key (use `--dev` only on a test box).
- Team files reference secrets as `{ file: PATH }` or `{ env: NAME }`. When a bot starts, the roster copies them into that bot's own folder. Secrets later removed from the team file are removed from the folder; secrets stored by hand with `secrets set --bot` are left alone.
- Every stored value is registered with the redactor at start and on every `secrets set`, so it is replaced in logs and memory writes. `oma-bot doctor` reports any stored value found in a log or memory file without printing it.
- `oma-bot secrets list` shows names, modes, sizes and dates only.

Residual exposure (decision B12): the Docker Engine API has no env file option, so each bot's gateway token is in its container env and visible to `docker inspect` for members of the docker group, who are root equivalent on the host anyway. It is only a per bot gateway token, never the inference key, and `secrets rotate-bot` revokes it within 10 seconds. Bot secrets are files, never env.
