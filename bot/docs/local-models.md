# Local models: tool-call repair and request shaping

This page describes what the roster gateway does so local models make reliable tool calls (plan 7.8, phase 8). OMA is not changed: the gateway sits in front of the inference server and sees every request and reply.

## Tool-call repair

Repair runs only when the request offered tools (`gateway.repair_tool_calls`, on by default). Such a reply is read whole, repaired, and sent on as one JSON body or a rebuilt event stream; requests without tools stream through unchanged.

1. **Broken arguments** in a native call are repaired, in this order, and never by guessing a value: cut the text after the brace that closes the object; remove a comma just before a closing brace or bracket; close an unclosed string and then open braces and brackets, only at the very end. A key or a colon with no value is never closed.
2. **Unreadable calls** are not dropped. The gateway sends the reply back to the model once, with the broken call and a note asking for valid JSON. If the call is still unreadable, it becomes plain text in the reply ("the call to X was not run because its arguments are not valid JSON") and is logged as `tool_args_unreadable`.
3. **Calls written as text** are recovered: `<tool_call>{json}</tool_call>` (Hermes and Qwen), `<function=name><parameter=key>value</parameter></function>` (Qwen coder), and a reply that is only one JSON call. Only when the calls end the reply, only for offered tools, and never from reasoning (anything up to the last `</think>`, or `reasoning_content`).
4. **Types**: a string value becomes a number, integer or boolean when the tool's schema says so and the text reads cleanly ("3" becomes 3 for an integer).
5. At most 32 calls per reply.
6. A repaired or recovered call reaches OMA as a normal tool call, so OMA's own schema check and permission check still apply.

## Request shaping

- **Tool count limit**: tools beyond `gateway.tool_limit` (64; hard ceiling 128, because many servers refuse more) are left out and logged. Team files should keep a worker bot at 12 tools or fewer; `team apply` warns above that.
- **Output cap**: `max_tokens` is capped at a quarter of the model's `context_size` from the team file.
- **Spill**: a tool result longer than `gateway.spill_bytes` (16384) is written once, redacted and 0600, to `spill/` in the bot's data folder. The context keeps a head, a pointer to the file (readable inside the container under `/home/agent/.openmono/spill/`) and a tail.
- **Repeat note**: when the last three tool calls in the conversation were the same call with the same result, the gateway adds "You called X with the same arguments three times and got the same result. Try something different or stop." to the last message. Nothing is blocked; OMA's own doom-loop tiers still apply, and the roster's stall guards end work that makes no progress.

Gateway events go to `logs/gateway-events.log` (no message content); `oma-bot gateway status` shows the last day's counts.

## Sampling presets

Presets live in roster.json under `bots.presets`; a bot names one in the team file:

```yaml
bots:
  - name: Builder
    preset: tools
```

The defaults are starting points to tune with the task set: `chat` (temperature 0.7, top_p 0.8, top_k 20, presence penalty 1.5, OMA's Qwen values) and `tools` (temperature 0.4, presence penalty 1.0). The container gets `OPENMONO_MODEL_PRESET`. Because OMA copies an active preset's endpoint and model over its own settings, and a preset without an endpoint points at a local default, every preset the roster writes (including OMA's built-in `qwen` and `qwen38` names) carries the gateway endpoint, the bot's model, the context size and the output cap.

## Measuring

The fixed task set has 40 tasks: reading files, searching, listing, editing, running commands, handing out work, consulting, and typed arguments. A call is valid when it names the expected tool, its arguments are a JSON object with the required fields in the schema's types, and it does what the task asked. It is scored on the raw reply (before repair) and on the repaired reply.

```bash
oma-bot gateway toolbench --levels 1,2,4      # real requests to the inference server
```

The report (written to `reports/`) gives the valid rate before and after repair, the unreadable rate and the no-call rate at each level. The success target is for Spencer to set after the first measurement (the plan's placeholder is 95 percent after repair).
