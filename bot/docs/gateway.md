# Roster gateway

Bots talk only to the gateway (`http://<bridge>:7478/v1`) with their own token. The gateway checks the token, picks a backend, waits for a lane, swaps the model nickname for the upstream model, adds the real key and streams the answer back unchanged.

- **Lanes.** Each backend has S lanes, where S comes from (in order) a measured node class, a measured `gateway.slots`, the server itself (`GET /v1/status` `concurrency.serving` on Strata, `GET /slots` on llama-server), or one lane marked unknown (B21). The service rereads the server every `gateway.slot_refresh_seconds`.
- **Priorities.** interactive (Spencer's `oma-bot run`), assignment, routine, background. Round robin by bot inside a priority; at most `gateway.per_bot_in_flight` (2) per bot. Background is dropped with 429 when the queue would be longer than S (B23).
- **Sticky slots.** A bot gets its last lane back when free. For llama-server backends with `slot_affinity: true` the lane is sent as `id_slot`, so the server reuses the bot's cached prompt.
- **Label routing.** A model nickname carries labels (team file `models:`). A request only goes to a backend whose labels include all of them; `class=main-3090` never lands on a small node. Each backend may map a nickname to its own model name (`models:` in roster.json backends).
- **Usage.** Every call is recorded in roster.db `usage` (bot, routine, priority, backend, model, tokens, queue wait). `oma-bot usage` shows a summary.
- **State.** The service writes `~/.omabot/run/gateway-state.json` (slots, queue lengths, oldest wait) for `oma-bot gateway status` and the health bot.

Measuring (task C1, LOAD-06):

```bash
oma-bot gateway slots                     # what the server reports and what the gateway uses
oma-bot gateway bench --levels 1,2,4      # report in ~/.omabot/reports/
oma-bot gateway bench --levels 1,2,4 --save   # also writes the node class in roster.json
```

The bench measures L (round trip and first byte), pp (prompt tokens per second), r1 (single stream tokens per second) and f(S) at each level. No slot number goes into config unless it was reported by the server or measured.
