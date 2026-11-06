# Vision on Windows (Foundry Local)

Images are never silently dropped. OMA already attaches images as `ImagePart`
data URLs and sends them as `image_url` parts on `/v1/chat/completions`
(`OpenMono.Llm.OpenAiCompatClient`, `OpenMono.Session.ImagePart`). Foundry
Local's HTTP service accepts image inputs on that same OpenAI-compatible
route, so the agent in its container needs no change. The Windows host only
decides which model serves the session and what `inference.vision_enabled`
resolves to. One model serves each machine: OpenMono never loads a second
model for images.

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
3. If the served model is text-only, the agent keeps working on it and the
   upstream `ConversationLoop` warning fires when an image is attached (the
   image is not sent, and the user is told). The launcher says the same and
   that it does not load a second model.
4. `openmono agent --vision` serves the same model, probes it, and starts the
   session with vision on. If the model refuses images, the launch fails with
   "The images were not sent." instead of starting a session that would drop
   them. It never switches to another alias.

`FoundryVisionRouter` and `WindowsImagePolicy` hold the per-turn decision as
pure, tested logic: an image turn goes to the served model when its probe
accepted images, otherwise it is refused out loud. Probe traffic stays on
127.0.0.1; nothing leaves the machine.

## Which model takes images

The model ladder is in the README. Below the 24 GB floor every rung is a
Qwen3.5 vision-language model (`qwen3.5-9b`, `qwen3.5-4b`, `qwen3.5-2b`,
`qwen3.5-0.8b`; catalog task `vision-language-chat`, input `text,image`), so
the served model already handles images on 12, 8, and 4 GB cards, and on a
CPU-only machine (`qwen3.5-2b`, generic CPU variant).

A 24 GB+ card always serves `qwen3.8-27b`. The Foundry catalog lists that
build as task `coding` with text input (checked 2026-11-05 at
https://foundrylocal.ai/api/models). OpenMono still serves it, probes it, and
reports whether images were accepted; it does not quietly load a different
model. `.\win\openmono.ps1 catalog check` prints each rung's task and input
modalities from the live catalog.

## What Spencer runs to verify (Windows, NVIDIA, Foundry 0.13.0+)

```powershell
.\win\openmono.ps1 start     # prints "Server accepted an image_url part" or "Vision off: ..."
.\win\openmono.ps1 agent     # attach an image in the TUI or the VS Code panel
```

- If `start` reports the image probe accepted, image turns go straight to the
  served model.
- If it reports vision off (expected for `qwen3.8-27b` while its catalog entry
  is text-only), attaching an image shows the upstream warning and the image
  is not sent. `.\win\openmono.ps1 agent --vision` fails with the same
  explanation instead of switching models.

## If the local build only takes images over /v1/responses

Foundry SDK samples also post base64 `input_image` content to `/v1/responses`.
OMA's client speaks `/v1/chat/completions` with `image_url` parts. If the
Windows probe shows chat-completions refusing images while `/v1/responses`
accepts them on the same model, that is a client-shape gap, not a model gap:
the fix is a `/v1/responses` sender in OMA's client that reuses the same
`ImagePart` data URLs (the agent's HTTP client lives in the container, so it
cannot be patched from `win/`). Report the probe output.

## CI

The probe and router are covered by `OpenMono.Win.Tests` with a fake HTTP
handler. No GPU, no Foundry install, and no network needed for tests.
