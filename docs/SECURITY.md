# OpenMono dual-box security

Local-first does not mean “open on the LAN.” Dual-box (agent ↔ inference) should treat the inference HTTP API as a private service.

## Threat model (short)

| Risk | Mitigation |
|------|------------|
| Anyone on Wi‑Fi can call `:7474` | Prefer Tailscale-only reachability; require `LLAMA_API_KEY` |
| Stolen/leaked API key | Rotate: `openmono tunnel rotate-key`; never commit `docker/.env` |
| Agent box with empty/placeholder key | Agent refuses to start until a real key is set |
| Cloud relay OTP path | Optional; Tailscale avoids exposing a public relay |

## Required checklist

1. **Same private tailnet** — both boxes logged into Tailscale (`tailscale status` → `Running`).
2. **API key always on** — inference must start with `--api-key` / `LLAMA_API_KEY`; agent `llm.api_key` must match.
3. **No placeholders** — reject values like `PASTE_THE_KEY_HERE` or empty strings.
4. **Secrets stay local** — `docker/.env`, `~/.openmono/settings.json`, and auth keys are never committed (see `.gitignore`).
5. **Tailscale ACLs** — allow TCP **7474** only between your agent and inference tags/users: [Tailscale ACL docs](https://tailscale.com/kb/1018/acls).
6. **Auth keys vs API tokens** — device join needs `tskey-auth-…`, not `tskey-api-…`.

## Recommended dual-box flow

```bash
# Inference box
openmono tunnel setup --tailscale
# note MagicDNS endpoint + LLAMA_API_KEY

# Agent box (same tailnet)
openmono config set llm.endpoint  http://<magicdns>:7474
openmono config set llm.api_key   <key-from-inference>
openmono tunnel prepare --agent   # should report api_key set + Tailscale Running
```

## Hardening knobs

| Setting | Purpose |
|---------|---------|
| `LLAMA_HOST` | Bind llama-server (default `0.0.0.0`). Set to your Tailscale `100.x` to avoid LAN exposure. |
| `LLAMA_REQUIRE_API_KEY=1` | Refuse to start native llama-server without a key (default on when Tailscale tunnel cache exists). |
| `openmono tunnel rotate-key` | Rotate inference key and print agent `config set` commands. |

## Verify

```bash
openmono tunnel status
openmono tunnel doctor             # prepare + health + API-key probe
# From agent box — health may be open; chat must require Authorization:
curl -sf http://<magicdns>:7474/health
curl -sf -H "Authorization: Bearer $KEY" http://<magicdns>:7474/v1/models
```

If `/health` is open without a key, that is normal for llama.cpp health probes — protect the **API** with the key and network (Tailscale + ACL), not health alone.

## Agent Mac helpers

| Script | Purpose |
|--------|---------|
| `./scripts/secure-mini-ssh-and-key.sh` | Interactive SSH to Mini: install iMac pubkey + print `LLAMA_API_KEY` |
| `./scripts/apply-mini-endpoint.sh <key>` | Write `llm.endpoint` + `llm.api_key` locally (masked) |
| `./scripts/github-push.sh` | Push branch; print GitHub SSH fix steps on failure |
