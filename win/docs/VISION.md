# Vision on Windows (llama.cpp)

Images are never silently dropped. OMA attaches images as `ImagePart` data
URLs and sends them as `image_url` parts on `/v1/chat/completions`
(`OpenMono.Llm.OpenAiCompatClient`, `OpenMono.Session.ImagePart`), the same
request llama-server takes on Mac/Linux, so the agent in its container needs
no change. The Windows host only decides whether llama-server is started with
the model's vision projector and what `inference.vision_enabled` resolves to.
One model serves each machine: OpenMono never loads a second model for images.

## How it works

1. Every rung of the Windows ladder in `scripts/models.json` is a Qwen
   vision-language GGUF with an `mmproj` projector file next to it on Hugging
   Face. `openmono start` downloads both into the models folder and starts
   llama-server with `--mmproj <file> --image-min-tokens 1024
   --image-max-tokens 1280`, the flags `scripts/install.sh` uses on Linux.
   With the projector loaded the context is the rung's `ctx_vision` (on
   24 GB, 172032 instead of 196608, as `CTX_VISION` in `install.sh`), which
   leaves room for the vision encoder.
2. The outcome feeds the same flag the rest of OMA uses (`WindowsVisionConfig`):
   an explicit `inference.vision_enabled` always wins, otherwise vision is on
   exactly when llama-server was started with the projector. The launcher
   exports the result as `OPENMONO_VISION_ENABLED` for the container, as
   compose already expects. `inference.vision_enabled: false` in
   `settings.json` also skips the projector download.
3. If the projector download fails, the text model still starts, the
   launcher says `mmproj download failed ...; vision will be unavailable.`,
   and vision is off. Attaching an image then fires the upstream
   `ConversationLoop` warning (the image is not sent, and the user is told).
4. `openmono agent --vision` serves the same model and starts the session
   with vision on. If the projector is not loaded, the launch fails with
   "The images were not sent." instead of starting a session that would drop
   them. It never switches to another model.

Image traffic stays on 127.0.0.1; nothing leaves the machine.

## What Spencer runs to verify (Windows)

```powershell
.\win\openmono.ps1 start     # prints "llama-server loaded the vision projector for <model>; vision enabled."
.\win\openmono.ps1 agent     # attach an image in the TUI or the VS Code panel
```

## CI

The projector handling, the vision flag and the context choice are covered by
`OpenMono.Win.Tests` with fakes (no GPU, no download, no network), and the
smoke test checks that `agent` passes `OPENMONO_VISION_ENABLED=1` and the
vision context to the container.
