# Capacity table (task C1)

Nothing here is measured yet. Fill it with `oma-bot gateway bench --record` on the real box through the relay. Until a row is measured the gateway uses only what the server reports (`concurrency.serving` or `/slots`). If the server reports nothing and no measured row exists, the gateway runs with one lane and doctor warns.

| Node class | GPU and VRAM | RAM | Backend | Model | Slots | Context per slot | Single stream tok/s | Prompt tok/s | f(2) | f(4) | Measured on |
|---|---|---|---|---|---|---|---|---|---|---|---|
| main-3090 | RTX 3090 24 GB | 64 GB | Strata | Qwen3.8-Flash-Next | not measured | not measured | not measured | not measured | not measured | not measured | |
