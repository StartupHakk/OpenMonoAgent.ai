# Architecture

OpenMono is a .NET 10 CLI that runs a local agentic loop against a llama.cpp inference server (or any OpenAI-compatible endpoint). Everything runs in Docker — your project folder bind-mounts in as `/workspace`, the agent can't escape it.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/architecture-diagram-dark.png">
    <source media="(prefers-color-scheme: light)" srcset="assets/architecture-diagram-light.png">
    <img src="assets/architecture-diagram-dark.png" alt="OpenMono architecture diagram" width="900" />
  </picture>
</p>

---

## High-level topology

```
┌────────────────────────────────────────┐
│  openmono (CLI)                        │
│  src/OpenMono.Cli/                     │
│                                        │
│  ConversationLoop                      │
│    └── ILlmClient (streaming SSE)      │
│    └── ToolDispatcher                  │
│         └── 20 built-in tools          │
│         └── MCP tools (dynamic)        │
│         └── LSP tools                  │
│         └── RoslynTool                 │
│    └── PermissionEngine                │
│    └── SessionManager (JSONL)          │
│    └── Compactor / Checkpointer        │
│    └── HookRunner                      │
│    └── IRenderer (TUI | Classic)       │
└────────────────────────────────────────┘
           │ HTTP :7474 (OpenAI-compat)
           ▼
┌────────────────────┐
│  llama-server      │
│  (llama.cpp)       │
│  Qwen3.6 GGUF      │
└────────────────────┘
```

---

## Startup sequence (`Program.cs`)

1. Parse CLI flags (`--tui`, `--classic`, `--endpoint`, `--model`, `--verbose`, …)
2. Probe the LLM server — tries `/props` (llama.cpp), falls back to `/v1/models` — to detect the live model name and context size
3. Create `SessionState` (12-char UUID, timestamp, empty message list)
4. Wire DI: config → renderer → permissions → memory → hooks → LSP/MCP managers → playbook registry → tool registry → LLM client
5. Choose renderer: `UseTui ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected)` — TUI by default for interactive terminals
6. Launch `ConversationLoop.RunAsync()`

---

## Conversation loop (`ConversationLoop.cs`)

### Initialisation (once)
- Build system prompt: base instructions + project `OPENMONO.md` + cross-session memory + git branch/status
- Start LSP servers, MCP servers, load playbooks, register commands and hooks

### Per-turn flow

```
User input
  │
  ├─ /command  →  CommandRegistry.Execute()
  │
  └─ message
       │
       ├─ Checkpoint if >65% context used  (LLM-generated summary, preferred)
       ├─ Compact   if >80% context used   (fallback — summarise + keep last 4 turns)
       │
       └─ Stream LLM  (up to 25 iterations)
            │
            ├─ ThinkingDelta   → thinking panel (collapsed on first text)
            ├─ TextDelta       → streamed to output
            ├─ ToolCallDelta   → accumulated, dispatched after stream ends
            └─ Usage           → token counter
                 │
                 └─ Tool execution (see below)
                      │
                      └─ Results added to session → next LLM iteration
```

Turn ends when the LLM produces text with no tool calls. Session saved to JSONL.

**Doom loop detection**: 3 identical tool call sequences in a row → abort.

---

## Tool execution pipeline

Every tool call goes through this pipeline before touching anything:

```
1.  Parse JSON arguments
2.  Schema validation (required fields, types, enums)
3.  Sanity check (e.g. path outside workspace → reject)
4.  Plan mode guard (read-only tools only when in plan mode)
5.  Capability check → PermissionEngine
         ├─ Auto-allowed  (FileRead, Glob, Grep, …)
         ├─ Config rules  (allow/deny regex patterns per tool)
         └─ Interactive   (prompt user → once / session / deny)
6.  Result cache lookup (read-only tools)
7.  Pre-tool hook
8.  Execute
9.  Post-tool hook
10. Artifact store (>10 KB results → stored, reference returned to model)
11. Cache write
12. File cache invalidation (FileWrite/FileEdit/ApplyPatch)
```

**Concurrency**: read-only + concurrency-safe tools run in parallel (`Task.WhenAll`). Writable tools run serially. Read-only tasks can start while the LLM is still streaming.


## Session & context management

### Persistence (`SessionManager`)
- Format: JSONL — line 1 is a header record, subsequent lines are messages
- Path: `~/.openmono/sessions/{date}_{sessionId}.jsonl`
- Checkpoints stored alongside: `{sessionId}.checkpoints.json`

### Context window management

| Threshold | Action |
|-----------|--------|
| 65% | **Checkpoint** — LLM summarises messages up to N recent turns; summary stored with cutoff index; future context window starts from cutoff |
| 80% | **Compact** (fallback) — summarise all messages except last 4 turns; replace with summary message + recents |

The CLI reads the actual context size from `/props` at startup, so both thresholds track the real window.

---

## Sub-agents (`AgentTool`)

Spawns an isolated session with a restricted tool set and a dedicated system prompt. Parent session's permission engine is reused.

| Agent | Max turns | Allowed tools | Purpose |
|-------|-----------|---------------|---------|
| `general-purpose` | 25 | all | generic tasks |
| `Explore` | 15 | FileRead, Glob, Grep, MCP | read-only discovery |
| `Plan` | 10 | + TodoWrite (no writes) | architecture planning |
| `Coder` | 30 | FileRead/Write/Edit, Glob, Grep, Bash | implementation |
| `Verify` | 20 | FileRead, Glob, Grep, Bash, Roslyn, LSP, MCP | adversarial testing |

Tool allow-lists support wildcards (`*`, `mcp__*`).

---

## MCP client (`McpServerManager` + `McpClient`)

On startup, for each enabled MCP server in config:

1. Spawn subprocess (command + args + env)
2. JSON-RPC 2.0 handshake over stdin/stdout (`initialize` → `notifications/initialized`)
3. `tools/list` → get tool definitions
4. Register each tool as `mcp__{serverName}__{toolName}` in `ToolRegistry`

`McpClient` serialises requests, reads responses, and exposes `CallToolAsync`, `ListResourcesAsync`, `ReadResourceAsync`.

**Auto-detected servers**: `code-review-graph` (if in PATH + graph DB exists) and `graphify` (if in PATH + `graphify-out/graph.json` exists) are registered automatically without config.

---

## LSP client (`LspServerManager` + `LspClient`)

Language servers start lazily on first call. File extension → language mapping:

| Extension | Server |
|-----------|--------|
| `.cs` | OmniSharp |
| `.ts` / `.tsx` | typescript-language-server |
| `.py` | pylsp |
| `.go` | gopls |
| `.rs` | rust-analyzer |

`LspTool` exposes: `hover`, `definition`, `references`, `completion`, `diagnostic`.

---

## Roslyn tool (`RoslynTool`)

Loads all `.cs` files from the working directory into an in-memory `AdhocWorkspace` with .NET runtime metadata references. Compilation is cached for 5 minutes.

Actions:

| Action | What it returns |
|--------|-----------------|
| `overview` | Types and members in a file |
| `find-references` | Every usage of a symbol |
| `callers` | Methods that call a given method |
| `diagnostics` | Compiler errors and warnings |
| `search` | Symbols matching a name pattern |
| `type-hierarchy` | Base types, interfaces, derived types |
| `blast-radius` | Direct + transitive dependents |
| `get-symbol` | Kind, type, parameters, modifiers, location |

---

## Permissions (`PermissionEngine`)

**Capability system (primary)** — tools declare what they need:

| Capability | Example |
|------------|---------|
| `FileReadCap(path)` | read a file |
| `FileWriteCap(path, op)` | write / create / delete |
| `ProcessExecCap(binary, args)` | shell execution |
| `NetworkEgressCap(host, port)` | HTTP/HTTPS call |
| `VcsMutationCap(repo, op)` | git write |
| `AgentSpawnCap(type, task)` | spawn sub-agent |

Decision order: session deny-all → config deny patterns → session allow-all → config allow patterns → **interactive prompt** (allow once / session / deny once / session).

**Legacy system (fallback)** — tools declare `PermissionLevel`: `AutoAllow`, `Ask`, or `Deny`.

---

## Hooks (`HookRunner`)

Bash scripts triggered at three points. Conditions can filter by tool name or input substring.

```jsonc
// settings.json
{
  "hooks": {
    "preToolUse": [
      {
        "if": { "tool": "Bash", "inputContains": "rm" },
        "run": "echo '{{tool_name}}: {{tool_input}}' >> audit.log"
      }
    ]
  }
}
```

Hook types: `SessionStart`, `PreToolUse`, `PostToolUse`. Timeout: 30 s each.

---

## Rendering

`IRenderer` is a composite interface: `IOutputSink` (write markdown, tool events) + `IInputReader` (read input, show pickers) + `ILiveFeedback` (stream text, thinking panel, tok/s indicator).

| Implementation | Mode | When used |
|----------------|------|-----------|
| `AnsiTuiRenderer` | Full-screen (Spectre.Console) | Interactive terminal |
| `TerminalRenderer` | Scrolling REPL | Redirected I/O or `--classic` |

The TUI is powered by [Spectre.Console](https://spectreconsole.net) — a cross-platform .NET library for building rich terminal UIs with colors, tables, progress bars, and interactive components.

---

## Key types (reference)

```csharp
interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    bool IsConcurrencySafe { get; }
    bool IsReadOnly { get; }
    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct);
    PermissionLevel RequiredPermission(JsonElement input);
    IReadOnlyList<Capability> RequiredCapabilities(JsonElement input);
}

interface ILlmClient
{
    IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        CancellationToken ct);
}

// Messages
record Message(MessageRole Role, string? Content,
               List<ToolCall>? ToolCalls, string? ToolCallId, string? ToolName);

record ToolCall(string Id, string Name, string Arguments);  // Arguments = JSON string

// Streaming
record StreamChunk
{
    ThinkingDelta? Thinking;
    TextDelta? Text;
    ToolCallDelta? ToolCall;
    Usage? Usage;
    bool IsComplete;
}
```

---

## Project layout

```
src/OpenMono.Cli/
├── Program.cs              entry point, DI wiring, startup sequence
├── Session/                ConversationLoop, SessionManager, Compactor, Checkpointer, TokenTracker
├── Tools/                  20 built-in tools + ToolRegistry, ToolDispatcher, SchemaBuilder
├── Llm/                    ProviderRegistry, AnthropicClient, OpenAiCompatClient, OllamaClient
├── Permissions/            PermissionEngine, Capability, PermissionLevel
├── Agents/                 AgentDefinition, AgentTool (sub-agent runner)
├── Mcp/                    McpClient, McpServerManager, McpToolAdapter
├── Lsp/                    LspClient, LspServerManager, LspTool
├── Roslyn/                 RoslynTool (AdhocWorkspace, 8 actions)
├── Playbooks/              PlaybookExecutor, PlaybookLoader, TemplateEngine, ParameterValidator
├── Commands/               14 slash commands (/help, /status, /model, /compact, /undo, …)
├── Memory/                 Cross-session memory (YAML frontmatter files)
├── History/                File snapshots for /undo
├── Hooks/                  HookRunner, HookDefinition
├── Rendering/              AnsiTuiRenderer, TerminalRenderer, IRenderer
├── Config/                 AppConfig, multi-source loader
└── Utils/                  Git, process, path helpers
```
