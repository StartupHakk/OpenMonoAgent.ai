# Fleet mode (optional)

Fleet mode sends jobs to named OMA servers (plan 7.21, phase 12). It is off by default. Only Spencer turns it on, by name, for nodes he invites; the roster and the bots never add hosts, move containers or enroll nodes on their own, and fleet commands are not bot tools. Every fleet link goes through the FRPS and FRPC relay with mutual TLS inside the tunnel. No Tailscale.

## Turning it on

```bash
oma-bot fleet enable --by Spencer --confirm     # placement.mode fleet, fleet.enabled_by Spencer
# restart oma-bot serve: the controller listens on 127.0.0.1:7490 and up
oma-bot fleet status
oma-bot fleet pause | resume                    # kill switch for new leases
oma-bot fleet disable
```

Before inviting nodes, fill in docs/fleet-inventory.md (step F0) and sign it off.

## Enrolling a node

```bash
oma-bot fleet invite echo11 --labels role=scrape,project=shs     # one-time code, 15 minutes, bound to the name
# relay: publish controller port 7490 (enrollment) and 7490 + slot for this node only
# on echo11:
oma-bot-node enroll --controller https://oma-fleet-controller:7490 --relay RELAY_HOST --ca-fingerprint FP --node echo11 <<< CODE
# back on the roster host, compare the fingerprint the node printed:
oma-bot fleet approve echo11 --fingerprint NODE_FP
# on echo11, as a service:
oma-bot-node run --relay RELAY_HOST
```

- The node makes its own key; it never leaves the node. The controller's private CA (in `secrets/fleet`, 0600) signs a 30 day client certificate.
- The node trusts the controller only if the CA it fetches has the fingerprint from the invite; nothing secret is sent before that check.
- Until `fleet approve`, the node can do nothing. Approval also issues the node's gateway token, which only ever travels over the node's mutual TLS channel.
- Each node must call on its own port (7490 + slot), so each node reaches the controller only through its own relay port. A certificate on any other port is refused.
- `oma-bot fleet drain NODE` stops new work for a node; `oma-bot fleet revoke NODE` refuses its certificate, ends its leases and removes its gateway token at once.

## Jobs

```bash
oma-bot fleet job add check-disk.yaml
oma-bot fleet jobs
oma-bot fleet job show JOB_ID
oma-bot fleet job cancel JOB_ID
oma-bot fleet secret set sync-api-key < keyfile
```

```yaml
idempotency_key: shs/keepalive/2026-11-04
kind: readonly_query            # readonly_query, routine_check, run_playbook, deploy, render, backfill
selector: { labels: { role: scrape } }
never_nodes: [pandora]
target_node: echo11             # optional
inputs: { prompt: "Is the keepalive page answering?" }
steps:                          # default: one step called run
  - { id: check, outward: false }
secrets: [sync-api-key]         # names only; values stay on the roster host
limits: { max_attempts: 2 }
```

- Read-only kinds run under the standing policy (`approved_by: policy:read-only`). Every other kind waits for Spencer's card in `oma-bot approvals`, bound to the job's hash; `approved_by` is written from that record. An `approved_by` in a job file or a node's result is dropped and audited.
- Never-touch nodes take no fleet work. A job never lands on a node it names in `never_nodes` or a node missing its labels.
- Nodes pull work. A lease lasts 60 seconds and the node renews it; every write carries the lease's fencing token, and a stale one is refused.
- Outward steps keep an intent and done record. If a node vanishes after an outward step started and before it finished, the job is lost and Spencer is alarmed: nothing runs again until a person checks the outside system. Finished steps are skipped on a retry.
- Artifacts are stored only when their SHA-256 matches; job secrets reach only the node holding the lease, as 0600 files removed after the job, and never appear in logs, audit lines or results.

## What the node runs

The node agent runs read-only jobs through the node's own OMA agent over ACP on loopback (`--agent`, default `http://127.0.0.1:7475`). Outward steps are refused on nodes in this build.

## Not built yet

Signed rollout with canary and rollback (F7), a fleet page in the web view (F8; use `oma-bot fleet status`), scale and soak (F9), and automatic certificate renewal (a node enrolls again with a new invite before 30 days). The live runs over the relay need two nodes and Spencer's direction.
