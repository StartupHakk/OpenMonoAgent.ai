using OpenMono.History;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Permissions;
using OpenMono.Config;

namespace OpenMono.Tools;

public sealed class ToolContext
{
    public required ToolRegistry ToolRegistry { get; init; }
    public required SessionState Session { get; init; }
    public required PermissionEngine Permissions { get; init; }
    public required AppConfig Config { get; init; }
    public required string WorkingDirectory { get; init; }
    public required Action<string> WriteOutput { get; init; }
    public required Func<string, CancellationToken, Task<string>> AskUser { get; init; }
    public FileHistory? FileHistory { get; init; }
    public CursorStore? Cursors { get; init; }

    public Action? BeginResponse { get; init; }
    public Action? EndResponse { get; init; }
    public Action<string>? StreamText { get; init; }

    public IOutputSink? Output { get; init; }

    public int AgentDepth { get; init; } = 0;

    public ToolContext WithAgentDepth(int newDepth) => new()
    {
        ToolRegistry     = this.ToolRegistry,
        Session          = this.Session,
        Permissions      = this.Permissions,
        Config           = this.Config,
        WorkingDirectory = this.WorkingDirectory,
        WriteOutput      = this.WriteOutput,
        AskUser          = this.AskUser,
        FileHistory      = this.FileHistory,
        Cursors          = this.Cursors,
        BeginResponse    = this.BeginResponse,
        EndResponse      = this.EndResponse,
        StreamText       = this.StreamText,
        Output           = this.Output,
        AgentDepth       = newDepth,
    };
}
