# Security notes

The roster ships in OMA's main repository and its web mode faces the internet, so security comes first. This page summarizes the controls; plan section 7.10 has the full threat table.

## Hard rules every bot gets

Written into each bot's data dir system prompt, outside the workspace, where the bot cannot change them. Among them: no posting outside the box, no self-approval, no frontier-model calls, outside text is data.

## Who decides

- **No self-approval.** No bot approves its own permission requests or jobs. Approvals come only from the roster broker: Spencer, or an approver bot Spencer listed that is not the requester and not under its control (docs/approvals.md, SEC-27 to SEC-31).
- **Safety floor.** Destructive commands, the roster's own secrets, the egress gate and the Docker socket are never approved through the roster; unreadable commands are refused.
- **Host commands** always need Spencer; the roster never runs them itself.
- **head-honcho** changes bots only in work a person gave it, always through person-only cards.
- **Outside text** (web pages, routine input) is fenced as data, lowers the level of any work it starts to ask, and never earns an auto rule.

## Isolation

- Bots run in containers with all capabilities dropped, no new privileges, a non-root user, resource limits, a read-only hooks mount and the host bridge config shadowed. Only the supervisor holds the Docker socket, and it re-checks every container spec.
- Bots and desktops sit on internal networks with no route out. The only way out is the egress gate: per-client tokens, allowlists, its own name lookup with every address checked, connections only to the checked address. The supervisor starts nothing without the roster firewall rules (docs/egress.md).
- A bot answering its own pause through its ACP listener is blocked by hook rules and caught by the broker, which ends the turn, disables the bot and raises an alarm.

## Secrets

- Bots never hold the inference key; the gateway does. Each bot has its own gateway token and its own gate token.
- Secrets are stored 0600 under `secrets/`, materialized per bot, and redacted from logs, memory, cards and audit lines. `oma-bot doctor` fails when a secret shows up in a log or memory file.
- The host bridge pair secret never enters a container; host invites are time limited.

## The web view

- Local mode binds 127.0.0.1 only. Sign-in uses one-time links; sessions are HttpOnly, SameSite=Strict cookies with server-side expiry.
- Every response has a strict Content Security Policy with no inline script, plus frame, sniffing and referrer protection. The page never puts data in as HTML.
- The Host header must be one the roster serves (DNS rebinding). Every change needs this page's Origin and the session's CSRF token. Viewers only read. An answer to a card must carry the card's own action hash.

## Web-single mode

- Refuses to start without HTTPS through the front with a valid certificate, the password and code login, the admin secret and an active owner (SEC-17). No default accounts or passwords.
- Passwords are PBKDF2-SHA256 with 600,000 iterations and a salt each; second-factor seeds are sealed with AES-GCM under a key derived from the admin secret, so users.db alone gives no second factor.
- Account and address lockout with doubling backoff, one message for every refusal, every failure audited (SEC-18). Codes work once.
- New session id at sign-in, idle and absolute expiry, `__Host-` Secure cookies, sign out everywhere (SEC-19).
- The roster refuses requests that did not arrive over https through the front, and keeps sign-in closed until the front check passes.

## Web-multi

- Each user has a whole roster of their own: database, audit, secrets, tokens, containers (prefixed and labelled), networks, bridges, subnets, firewall chains, ports and workspace root. A user's supervisor sees and changes only that user's containers (SEC-21).
- Every web API call opens only the signed-in account's roster; nothing in a request can name another (SEC-20). Host commands are refused in user rosters.
- It starts only with the release gate passed and an outside review recorded.

## Images and releases

- No bot starts until a person accepts the agent image on the host, and a changed image is refused until accepted again (SEC-14).
- `oma-bot release check` blocks a release on a known critical advisory against a pinned package, a loose version, missing advisory data, a secret in the docs or a missing sign-off (SEC-25).
- Refused roster tool calls (a tool a bot does not have, or one that does not exist, like a fleet command) are audited (SEC-33).

## Fleet mode (optional)

- Off unless Spencer turns it on by name; fleet commands are the CLI's alone, never bot tools (SEC-33).
- Nodes enroll with a one-time code bound to their name, keep their own key, and work only after Spencer approves the key's fingerprint. Mutual TLS with the controller's private CA, end to end inside the relay tunnel, and each node only on its own relay port (FLEET-01, FLEET-02).
- Leases with fencing tokens give one valid writer per job; outward steps keep an intent and done record, and a job whose node vanished in between is never run again without a person (FLEET-03, FLEET-04).
- approved_by is written only from a person's approval record bound to the job hash (FLEET-06). Nodes hold their own gateway token, never the inference key (FLEET-07). Job secrets reach only the lease holder as 0600 files (FLEET-08).

## Audit

The audit log is append-only with a hash chain (`oma-bot audit verify`). Approval events, assignment events, bot versions and room messages are append-only tables too. No message content is written to the audit log.

## Known limits

- A process inside a host_exec bot could read its own environment and use its invite; hook rules, the firewall and agent-host's own deny list and sudo policy limit what it can do (decision B66).
- OMA's ACP listener inside a container is reachable from processes in that container (threat T3); the mitigations above apply until OMA offers an ACP token.
