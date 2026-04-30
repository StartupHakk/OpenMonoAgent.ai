---
name: graphify
description: Query and manage the graphify knowledge graph for this codebase
version: "1.0.0"
trigger: both
trigger-patterns:
  - "graphify *"
user-invocable: true
argument-hint: "<query|path|explain|build|report|visualize> [args]"

parameters:
  action:
    type: string
    required: true
    hint: "Action: query, path, explain, build, report, visualize"
    enum: [query, path, explain, build, report, visualize]
  args:
    type: string
    required: false
    default: ""
    hint: "For query/explain: concept or question. For path: 'NodeA NodeB'. Leave empty for report/visualize/build."

steps:
  - id: run
    inline-prompt: |
      Run the graphify action below. All commands run from the working directory.
      graphify-out/graph.json must exist — run `build` first if it does not.

      Action: {{params.action}}
      Args: {{params.args}}

      Commands:
        query     → graphify query "{{params.args}}"
                    Use --budget 100 if the default result is too shallow.
                    Example: graphify query "how does auth work?" --budget 100

        path      → graphify path "{{params.args}}"
                    Args must be two node names separated by a space.
                    Example: graphify path "UserController" "TokenStore"

        explain   → graphify explain "{{params.args}}"
                    Returns type, file location, relationships, and a plain-language description.

        build     → (cd to working directory) && graphify update .
                    Builds or incrementally refreshes the graph.
                    Outputs: graphify-out/graph.json, graph.html, GRAPH_REPORT.md

        report    → Read graphify-out/GRAPH_REPORT.md and summarise:
                    - top communities and what they represent
                    - most connected nodes (hubs)
                    - isolated nodes
                    - total node and edge count

        visualize → Tell the user: "Open graphify-out/graph.html in a browser."
                    Then read graphify-out/GRAPH_REPORT.md and list the top 5 communities
                    and most connected nodes so they know what to look for.

      Run the command, show the full output, and summarise the key findings.
      If the command fails because graphify is not installed, tell the user to run: openmono graphify
    allowed-tools: ["Bash", "FileRead"]
    gate: none
---

Query the graphify knowledge graph using CLI commands.
Run `build` first to create graphify-out/graph.json, then use query/path/explain to explore.
Use `report` for a summary of graph communities and hubs.
Use `visualize` to get instructions for the interactive HTML graph.
