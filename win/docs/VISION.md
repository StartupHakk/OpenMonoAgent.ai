# Vision on Windows (Foundry Local)

Images are never silently dropped. OMA already attaches images as `ImagePart`
data URLs and sends them as `image_url` parts on `/v1/chat/completions`
(`OpenMono.Llm.OpenAiCompatClient`, `OpenMono.Session.ImagePart`). Foundry
Local's HTTP service accepts image inputs on that same OpenAI-compatible
route, so the agent in its container needs no change. The Windows host only
decides which model serves the session and what `inference.vision_enabled`
resolves to.

## How it works

1. `openmono start` (and every `openmono agent`) runs `FoundryVisionProbe`
   against the served model: a 1x1 PNG posted as an `image_url` part to
   `/v1/chat/completions`. A 2xx means the model takes images; anything else
   is a routing signal, not a crash. The result is cached per served model id
   in `~/.openmono/win/inference.json`.
2. The result feeds the same flag the rest of OMA uses (`WindowsVisionConfig`):
   an explicit `inference.vision_enabled` always wins, an accepted probe turns
   vision on, a refused probe turns it off. The launcher exports the outcome
   as `OPENMONO_VISION_ENABLED` for the container, as compose already expects.
3. If the coding model is text-only, the agent keeps working on it and the
   upstream `ConversationLoop` warning fires when an image is attached (the
   image is not sent, and the user is told). The launcher also prints how to
   get images through: `openmono agent --vision`.
4. `openmono agent --vision` loads the Foundry vision alias, probes it, and
   starts the session on it with vision on. If that model also refuses
   images, the launch fails with "The images were not sent." instead of
   starting a session that would drop them.

The agent sends every turn of a session to one model, so switching to the
vision model only for image turns needs an upstream change (below).
`FoundryVisionRouter` and `WindowsImagePolicy` already hold that per-turn
decision as pure, tested logic for when it lands. Probe traffic stays on
127.0.0.1; nothing leaves the machine.

## Upstream change for automatic per-turn routing (for Spencer)

Add an optional `inference.vision_model` setting (env `OPENMONO_VISION_MODEL`)
to OMA: when a turn carries `ImagePart`s and the setting is present,
`OpenAiCompatClient` sends that turn to the vision model id on the same
endpoint; text turns stay on `llm.model`. Purely additive, no effect on
Mac/Linux when unset. With it, the Windows launcher would load both aliases
and pass the vision model's served id, and `--vision` becomes unnecessary.

## Alias used

Vision fallback alias: **`qwen2.5-vl-7b`** (`FoundryConstants.DefaultVisionFallbackAlias`).
Provisional: it is always resolved against the live Foundry catalog
(`FoundryCatalogClient` + `FoundryAliasResolver`) before use, so if the
installed Foundry release spells it differently (or does not carry it), the
host reports "not in catalog" instead of downloading the wrong thing. If you
confirm a better catalog spelling on your machine, tell us and we will update
the constant.

## What Spencer runs to verify (Windows, NVIDIA, Foundry 0.13.0+)

```powershell
.\win\openmono.ps1 start     # prints "Server accepted an image_url part" or "Vision off: ..."
.\win\openmono.ps1 agent     # attach an image in the TUI or the VS Code panel
```

- If `start` reports the image probe accepted, Qwen3.8-27B is serving as a
  vision-language model on this build and image turns go straight to it.
- If it reports vision off, attach an image in a normal session to see the
  upstream warning, then run `.\win\openmono.ps1 agent --vision` and attach
  it again: the vision alias carries it.

## If the local build only takes images over /v1/responses

Foundry SDK samples also post base64 `input_image` content to `/v1/responses`.
OMA's client speaks `/v1/chat/completions` with `image_url` parts. If the
Windows probe shows chat-completions refusing images while `/v1/responses`
accepts them on the same model, that is a client-shape gap, not a model gap:
the fix is a `/v1/responses` sender in OMA's client that reuses the same
`ImagePart` data URLs (the agent's HTTP client lives in the container, so it
cannot be patched from `win/`). Report the probe output; until then the
`--vision` session above carries images.

## CI

The probe and router are covered by `OpenMono.Win.Tests` with a fake HTTP
handler. No GPU, no Foundry install, and no network needed for tests.
