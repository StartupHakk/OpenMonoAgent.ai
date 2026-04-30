# graphify

Semantic knowledge graph for your codebase. Builds a queryable graph from source code using AST parsing across 25+ languages, then lets the agent answer conceptual questions without reading raw files.

> Built by [@safishamsi](https://github.com/safishamsi/graphify) — integrated into OpenMono as a Bash-tool layer on top of the CLI.

---

## What it does

graphify parses your project into a graph of nodes (classes, functions, modules, files) and edges (calls, imports, inherits, depends-on). Once built, the agent can query that graph directly instead of reading files one by one — making cross-file questions ~71× more token-efficient.

Output lives in `graphify-out/` inside your project directory:

| File | Purpose |
|------|---------|
| `graphify-out/graph.json` | Machine-readable graph — what the agent queries |
| `graphify-out/graph.html` | Interactive visual explorer — open in a browser |
| `graphify-out/GRAPH_REPORT.md` | Human-readable summary of communities and key nodes |

---

## Setup

graphify is installed automatically by `install.sh` (Step 5 — agent and full roles). No manual install needed.

To build the graph for a project (one-time, per project):

```bash
openmono graphify           # installs if missing, builds graph, verifies

# Or directly from your project root:
graphify update .
```

---

## CLI commands

All commands must be run from inside the project directory (graphify doesn't accept a path argument).

```bash
# Build / refresh
graphify update .              # full build (or incremental if graph exists)

# Query the graph
graphify query "how does auth work?"
graphify query "what manages session state?" --budget 100   # limit traversal depth

# Find connection between two concepts
graphify path "UserController" "TokenStore"

# Explain a specific node
graphify explain "ConversationLoop"
```

---

## How OpenMono uses it

At startup, OpenMono checks for `graphify-out/graph.json` in the working directory. If found, it prints:

```
graphify graph detected — use: graphify query/path/explain via Bash.
```

The agent then uses Bash to run graphify commands when answering conceptual questions — before falling back to raw Grep. This is not an MCP server; graphify runs as a direct Bash subprocess.

### Playbook

A built-in playbook at `.openmono/playbooks/graphify/PLAYBOOK.md` exposes explicit graph actions:

```
# Inside the agent REPL:
graphify query "session state"
graphify path "ConversationLoop" "TokenTracker"
graphify explain "ToolRegistry"
graphify build
graphify update
```

---

## Sample questions to ask the agent

These are best answered by graphify rather than raw file reads:

```
What handles HTTP request routing?
What would break if I deleted TodoStore?
How does a request get from the controller to the database?
What calls the save method?
Which classes have the most dependents?
Explain the role of CliApp in this codebase.
```

---

## Visualization

Open `graphify-out/graph.html` in a browser. The graph is interactive:
- Click a node to inspect it (type, file, relationships)
- Search nodes by name
- Filter by community (clusters of related code)
- Click neighbor links to traverse the graph

The agent will remind you to open this file when you ask about visualization.

---

## Docker

graphify is baked into the agent image via `pip3 install graphifyy` in `docker/Dockerfile.agent`.

Build the graph on the host, then mount your project directory:

```bash
# Host — build the graph
cd ~/your-project
openmono graphify

# Start the agent — graph is accessible at /workspace/graphify-out/graph.json
WORKSPACE=~/your-project openmono agent
```

The graph is built on the host and accessed via the `${WORKSPACE}:/workspace` volume mount. No rebuild needed inside the container.

---

## Rebuilding

The graph does not auto-update. Rebuild after significant changes:

```bash
openmono graphify          # from any directory
# or
cd ~/your-project && graphify update .
```
