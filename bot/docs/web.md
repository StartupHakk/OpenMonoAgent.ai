# The web view

The roster serves a small web view (plan 7.15, option U1): bots and their state, approvals with answer buttons, routines, rooms, memory with undo, and the audit log. It is plain HTML and one ES module with no framework and no build step (decision B6).

## Local mode

`oma-bot serve` starts it on `http://127.0.0.1:7477/` (`api.bind_address` must be a loopback address in local mode). Sign in from the same computer:

```bash
oma-bot web open
```

The command prints a link that works once and expires in 5 minutes. Opening it gives the browser a session cookie for `web.session_hours` (12). Sign out from the page or let it expire.

## How it is hardened

- A strict Content Security Policy on every response: scripts and styles only from the roster itself, no inline script, no frames, no plugins. The page puts every value in as text, never as HTML.
- `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, no server banner, `Cache-Control: no-store` for pages and data.
- The Host header must be `127.0.0.1:7477` or `localhost:7477`, which stops DNS rebinding.
- Session ids are 256 random bits in an HttpOnly, SameSite=Strict cookie; only their hash is stored (users.db). Expiry is enforced by the server.
- Every change needs this page's Origin and the session's CSRF token.
- Viewers only read; only the owner reads the audit log.
- An answer to a card must carry the card's action hash, so an answer can never apply to a different action. Answers go through the same approver policy as the CLI.

## Web-single mode (one owner, behind HTTPS)

Web-single puts the same view on the internet behind an HTTPS front (plan 7.19). The roster keeps listening on 127.0.0.1:7477; the front terminates TLS and forwards to it. Per-user isolation (web-multi) is phase 11.

```bash
oma-bot web admin-secret                                  # secrets/web-admin.secret, 0600, never printed
oma-bot users add spencer --role owner < password-file     # 12 to 128 characters
cat ~/.omabot/web/enroll/spencer.txt                      # add the key to an authenticator app
oma-bot users confirm spencer <<< 123456                  # the app's current code
oma-bot serve &                                           # local mode is fine for the check
cd bot/docker/web-front
CADDY_DIGEST=sha256:... OMABOT_WEB_HOST=roster.example.com docker compose up -d
oma-bot web enable --public-origin https://roster.example.com
# restart oma-bot serve, then:
oma-bot doctor --online
```

`oma-bot web enable` refuses, and `oma-bot serve` refuses to start in web mode, when any of these is missing: an https public origin with a DNS name, `web.behind_https_front`, a loopback API address, the password and code login (passkeys are not built yet), a strong 0600 admin secret, a private users.db with an active owner. `web enable` and the running service also ask the public address for a value only this roster knows (`/healthz`), so the certificate must be valid and the front must forward here and mark requests as https. Until that check passes, sign-in and the API answer 503; the service re-checks every 5 minutes.

### Signing in

Account name, password and the current 6-digit code. Every refusal reads the same. Five failures lock the account for 15 minutes, and each lockout right after another doubles, up to a day; 20 failures from one address in 15 minutes lock that address the same way. Every failure is audited (never the password). A code works once.

### Sessions

A new session id at every sign-in (a cookie the browser already had is ended, never reused). The cookie is `__Host-omabot_session`: Secure, HttpOnly, SameSite=Strict, path /. Sessions end after `web.idle_minutes` (30) without a request and at `web.session_hours` (12) in any case. "Sign out everywhere" ends every session of the account; changing a password or role, disabling an account and `oma-bot web disable` end sessions too.

### Roles

| Role | Can |
|---|---|
| owner | Everything in the view, including the audit log |
| operator | Answer cards, mark notices, post in rooms, undo memory |
| viewer | Read only |

Accounts are made on the host with `oma-bot users`; the web view has no sign-up or account page.

### The HTTPS front

`docker/web-front` holds the Caddy recipe: the image pinned by digest, host networking to reach 127.0.0.1:7477, ports 80 (redirect and certificate checks) and 443, the admin API off, all capabilities dropped except binding low ports, a read-only root and no new privileges. The front sets X-Forwarded-Proto and X-Forwarded-For itself; the roster refuses any request that does not say https and takes the client address from the last X-Forwarded-For entry only when the request comes from loopback.

### Source code

Every page links to `/source`, which points at `web.source_url` (AGPL-3.0 section 13).
