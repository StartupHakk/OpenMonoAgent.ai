# Capacity guide

Capacity is measured, never guessed (plan 7.9). Every limit the gateway uses comes from what the inference server reports or from a recorded measurement.

## Three gates in series

1. Each bot's OMA agent sends at most 2 requests at a time.
2. The roster gateway holds exactly S requests in flight to a backend and queues the rest by priority: interactive (Spencer's own chat), assignment (handed-off work), routine, background (memory upkeep, dropped first when the queue is longer than S). Within a priority it goes round robin by bot, so one bot cannot starve the others.
3. The inference server serves S slots.

## Finding S

```bash
oma-bot gateway slots            # what the server says (Strata /v1/status, llama-server /slots)
oma-bot gateway bench --save     # throughput at 1, 2 and 4 parallel requests, saved to a node class
```

When the server does not report its slots and nothing is measured, the gateway uses 1 and raises a notice. `gateway.slots` in roster.json takes only `auto` or a measured number.

## Routines and the share of slots

Routine runs start only while routines use less than `routines.slot_share` (default half) of the gateway lanes, so Spencer's own work keeps moving. Quiet runs make no model call at all.

## Tool calls

`oma-bot gateway toolbench` scores 40 tool tasks at 1, 2 and 4 parallel requests, before and after the gateway's repair layer (docs/local-models.md). Run it after any model or server change.

## Reading the numbers

- `oma-bot gateway status`: lanes, waiting requests, peak in flight, and the last day's gateway events.
- `oma-bot usage --days 7`: calls and tokens per bot and routine, with queue waits.
- Load targets from the plan (to confirm on the real server): with 4 bots the 95th percentile queue wait stays under 20 seconds and no bot waits more than three times the median; with 8 bots and routines interactive work stays under 15 seconds.
