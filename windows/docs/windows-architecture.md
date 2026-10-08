# Windows architecture (OpenMono for Windows)

Single box default: WinUI 3 desktop app hosts the OMA agent in process and
supervises native `llama-server.exe` on 127.0.0.1:7474. Caddy, SearXNG, and
Scrapling run in Docker Desktop (WSL2 backend) via the existing `docker/`
compose definitions plus `windows/docker/docker-compose.windows.yml`.

```text
Chat page (WinUI 3)
  -> AgentHostFactory.CreateSession
     -> ConversationLoop (OMA, unmodified)
     -> ToolRegistry with WindowsShellTool registered as "Bash" first
     -> PermissionEngine with Windows defaults merged into AppConfig
     -> OpenAiCompatClient -> http://127.0.0.1:7474 (native llama-server)
     -> WebSearch/WebFetch -> gateway when Docker is up, else built in fallback
  -> LlamaServerSupervisor owns llama-server.exe (health gated, 180s patience)
  -> DockerComposeManager owns caddy/searxng/scrapling (by reference)
  -> ACP loopback toggle on 127.0.0.1:7475 for future editor extensions
```

Reuse is project references plus wrappers in `windows/`. No file outside
`windows/` is edited. The five deferred upstream changes (IShellLauncher,
LSP file URIs, MCP .cmd handling, OS aware prompt, Windows sanity patterns)
are worked around additively and listed in the final report.

No telemetry by default. No embedded terminal. Agent role only.
