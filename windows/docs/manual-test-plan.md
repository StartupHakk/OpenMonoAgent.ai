# Manual test plan (real Windows hardware)

Run on four machine shapes: NVIDIA 24GB, NVIDIA 16GB, AMD or Intel (Vulkan),
and CPU only.

1. Fresh install: run OpenMonoSetup.exe per user, no elevation. App launches.
2. Wizard: hardware matches expectations, recommended tier is correct,
   download resumes after cancel, checksum failure deletes and retries once.
3. Inference: server reaches healthy within 180s, /props reports the model,
   tok/s visible, restart works, stale port held by another app falls back.
4. Chat: streams text, thinking collapses, tool cards render, permission
   dialog offers Allow once, Allow for session, Deny. Slash palette lists
   commands. Copy and paste work both ways.
5. Guardrails: `format E:` is denied, `reg delete` is denied, runas prompts,
   credential paths are protected.
6. Docker: with Docker Desktop running, gateway search works through Caddy.
   With Docker stopped, agent falls back to DuckDuckGo and direct fetch.
7. Updates: release check prompts, never restarts mid session.
8. Uninstall: processes stop, shortcuts removed, keep data is default,
   delete data wipes models and sessions.
9. Diagnostics: bundle contains redacted settings, log tail, hardware report.
