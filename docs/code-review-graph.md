# code-review-graph

Persistent incremental knowledge graph for structural code analysis. Runs as an MCP server and adds ~22 tools to the agent covering call graphs, blast-radius analysis, dead code detection, architecture overviews, and semantic search across 19+ languages.

> Built by [@tirth8205](https://github.com/tirth8205/code-review-graph) — integrated into OpenMono as an auto-detected MCP server.

---

## What it does

code-review-graph builds a structural call graph from your codebase and exposes it via MCP stdio transport. Unlike graphify (which builds a conceptual semantic graph), code-review-graph focuses on precise structural relationships: who calls what, what would break if X changed, which code is never called.

When running, its tools appear natively in the agent's tool list prefixed `mcp__code-graph__*`.

---

## Setup

code-review-graph is installed automatically by `install.sh` (Step 5 — agent and full roles). No manual install needed.

`install.sh` also builds the graph from `ref/` automatically if that directory has content. To build or rebuild manually:

```bash
openmono graph              # installs if missing, builds from ref/, verifies MCP server

# Or directly:
code-review-graph build     # full build from current directory
code-review-graph update    # incremental (only changed files)
```

Add your own projects under `ref/` before running for richer graph data.

---

## CLI commands

### Graph management

```bash
code-review-graph build              # full graph build — re-parses all files
code-review-graph update             # incremental update — only changed files
code-review-graph postprocess        # re-run post-processing (flows, communities, FTS)
code-review-graph watch              # auto-update graph as files change
code-review-graph status             # show graph statistics (nodes, edges, languages)
```

### Visualization and docs

```bash
code-review-graph visualize          # generate interactive HTML graph visualization
code-review-graph wiki               # generate markdown wiki from community structure
```

### Multi-repo support

```bash
code-review-graph register           # register a repository in the multi-repo registry
code-review-graph unregister         # remove a repository from the registry
code-review-graph repos              # list all registered repositories
```

### Change impact

```bash
code-review-graph detect-changes     # analyze change impact (what does this diff affect?)
```

### MCP server

```bash
code-review-graph serve              # start MCP server on stdio — used by OpenMono
```

---

## How OpenMono uses it

OpenMono auto-detects code-review-graph at startup:

1. Checks if `code-review-graph` is in PATH
2. Checks if a graph database exists (`~/.code-review-graph/`)
3. If both: registers `code-review-graph serve` as an MCP server
4. Its tools appear in the agent's tool list as `mcp__code-graph__*`

You'll see at startup:
```
code-review-graph detected — registering as MCP server.
```

### Manual MCP configuration

Override auto-detection in `.openmono/settings.json`:

```json
{
  "mcp_servers": {
    "code-graph": {
      "command": "code-review-graph",
      "args": ["serve"],
      "enabled": true
    }
  }
}
```

---

## Agent tool names

Once registered via MCP, the tools are visible to the agent. To see the exact list during a session:

```bash
# Run inside agent REPL or via Bash:
echo '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' \
  | code-review-graph serve 2>/dev/null
```

Known tools from the agent system prompt:
- `graph_search` — semantic search across the graph
- `graph_callers` — find all callers of a method/function
- `graph_query` — structured graph query

---

## Sample questions the agent can answer with it

```
What calls the ConversationLoop constructor?
What would break if I changed the ILlmClient interface?
Find all callers of RecordUsage.
Which methods are never called (dead code)?
What is the blast radius of changing SessionState?
Show me the call chain from Program.Main to StreamChatAsync.
```

---

## Docker

code-review-graph is pre-installed in the agent Docker image. The graph database is mounted from your host:

```yaml
volumes:
  - ${HOME}/.code-review-graph:/root/.code-review-graph
```

Build the graph on the host once, and the container picks it up on every run.

---

## Difference from graphify

| | code-review-graph | graphify |
|---|---|---|
| Focus | Structural (call graphs, callers, blast radius) | Semantic (concepts, relationships, communities) |
| Transport | MCP stdio — tools appear natively in agent | Bash subprocess — agent runs CLI commands |
| Graph location | `~/.code-review-graph/` (global) | `graphify-out/` (per project) |
| Incremental updates | Yes (`update`, `watch`) | Yes (`update .`) |
| Visualization | `visualize` command (HTML) | `graphify-out/graph.html` (auto-generated) |
| Wiki export | Yes (`wiki` command) | No |
| Multi-repo | Yes (`register`, `repos`) | No |
| Languages | 19+ | 25+ |

Use both together for full coverage: code-review-graph for precise structural queries, graphify for conceptual exploration.
