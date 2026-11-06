namespace OpenMono.Win.AgentHost;

// Graph-tool visibility from the Windows side of the mounted workspace.
// code-review-graph and graphify run inside the Linux agent container exactly
// as on Mac/Linux (auto-detect registers the code-graph MCP server when its
// database exists; graphify is invoked through the in-container Bash tool).
// The Windows host only checks that the workspace artifacts those tools
// consume are present, using the same paths upstream checks, evaluated on
// the host copy of the same mounted tree. All I/O is injected so tests run
// without a workspace.
public sealed record WindowsGraphInput(
    string WindowsWorkspace,
    string DataDirectory,
    Func<string, bool> DirectoryHasEntries,
    Func<string, bool> FileExists);

public sealed record WindowsGraphStatus(
    bool CodeGraphDbPresent,
    bool GraphifyGraphPresent,
    string Detail);

public static class WindowsGraphArtifacts
{
    public static WindowsGraphStatus Check(WindowsGraphInput input)
    {
        var dbDirs = new[]
        {
            Path.Combine(input.DataDirectory, "graph-db"),
            Path.Combine(input.WindowsWorkspace, ".code-review-graph"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".code-review-graph"),
        };

        var hasDb = dbDirs.Any(input.DirectoryHasEntries);
        var graphJson = Path.Combine(input.WindowsWorkspace, "graphify-out", "graph.json");
        var graphReport = Path.Combine(input.WindowsWorkspace, "graphify-out", "GRAPH_REPORT.md");
        var hasGraphify = input.FileExists(graphJson) || input.FileExists(graphReport);

        var detail = $"code-graph DB: {(hasDb ? "present" : "absent")}; "
            + $"graphify graph: {(hasGraphify ? "present" : "absent")}. "
            + "Both tools run in-container; build the databases with code-review-graph build "
            + "or graphify from inside the agent if absent.";

        return new WindowsGraphStatus(hasDb, hasGraphify, detail);
    }
}
