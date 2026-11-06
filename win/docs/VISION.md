# Vision on Windows (Foundry Local)

Images are never silently dropped. OMA already attaches images as `ImagePart`
data URLs and sends them as `image_url` parts on `/v1/chat/completions`
(`OpenMono.Llm.OpenAiCompatClient`, `OpenMono.Session.ImagePart`). The Windows
host reuses that exact contract and only decides *which model* serves an image
turn.

## How routing works

1. At startup the host runs `FoundryVisionProbe` against the adopted endpoint:
   a tiny 1x1 PNG posted as an `image_url` part to `/v1/chat/completions`.
   Accept (2xx) means the serving model takes images; anything else is a
   routing signal, not a crash.
2. `FoundryVisionRouter.SelectModelForTurn` picks the model per turn:
   - text-only turn: always the target model (`qwen3.8-27b`).
   - image turn and the target probe accepted: target model, images included.
   - image turn and the target refused (or was never probed): the resolved
     vision fallback model, with the reason recorded.
   - image turn and nothing vision-capable is available: no model, and
     `WindowsImagePolicy.RequireVisionModel` throws a user-facing error that
     names the recovery command. The text is never sent without its images.

The probe result feeds the same `inference.vision_enabled` flag the rest of
OMA uses (`WindowsVisionConfig`): an explicit user setting always wins, an
accepted probe enables vision, a refused probe disables it. The
`ConversationLoop` image warning and `OPENMONO_VISION_ENABLED=1` env var keep
working exactly as documented. Probe traffic stays on 127.0.0.1; nothing
leaves the machine, per the local-first principle.

## Alias used

Vision fallback alias: **`qwen2.5-vl-7b`** (`FoundryConstants.DefaultVisionFallbackAlias`).
Provisional: it is always resolved against the live Foundry catalog
(`FoundryCatalogClient` + `FoundryAliasResolver`) before use, so if the
installed Foundry release spells it differently (or does not carry it), the
host reports "not in catalog" instead of downloading the wrong thing. If you
confirm a better catalog spelling on your machine, tell us and we will update
the constant.

## What Spencer runs to verify (Windows, NVIDIA, Foundry 0.13.0+)

```
foundry model load qwen3.8-27b
foundry server status        # note the URL, normally http://127.0.0.1:7474
```

Then start the Windows host and attach an image in chat:

- If the turn stays on `qwen3.8-27b`, the local build accepts `image_url`
  parts and Qwen3.8-27B is serving as a native vision-language model.
- If the turn routes to the VL fallback, that build is text-only and the
  fallback carried the images. The reason string in the log says which path
  was taken.

## If the local build only takes images over /v1/responses

Foundry SDK samples also post base64 `input_image` content to `/v1/responses`.
OMA's client speaks `/v1/chat/completions` with `image_url` parts. If the
Windows probe shows chat-completions refusing images while `/v1/responses`
accepts them on the same model, that is a client-shape gap, not a model gap:
the fix is a small `/v1/responses` sender in `win/` that reuses the same
`ImagePart` data URLs. Report the probe output and we will add it; until then
the VL fallback path above carries image turns.

## CI

The probe and router are covered by `OpenMono.Win.Tests` with a fake HTTP
handler. No GPU, no Foundry install, and no network needed for tests.
