using OpenMono.History;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Permissions;
using OpenMono.Config;
using OpenMono.Acp;

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
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<string>>? AskUserWithOptions { get; init; }

    /// <summary>
    /// In-place masked secret prompt for the sudo elevation flow (TUI). Null
    /// chars = the user cancelled (Esc); empty = an empty submit, which still
    /// counts as an attempt. Null property = this surface has no password
    /// channel (the elevation flow then fails honestly instead of looping
    /// prompts a non-interactive terminal would answer with placeholders).
    /// </summary>
    public Func<string, CancellationToken, Task<char[]?>>? AskSecret { get; init; }
    public FileHistory? FileHistory { get; init; }
    public SessionDiffStager? DiffStager { get; init; }
    public CursorStore? Cursors { get; init; }

    public Action? BeginResponse { get; init; }
    public Action? EndResponse { get; init; }
    public Action<string>? StreamText { get; init; }
    public Action<string>? OnDebug { get; init; }

    public IOutputSink? Output { get; init; }

    public IAcpUserInteraction? Interaction { get; init; }

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
        AskUserWithOptions = this.AskUserWithOptions,
        AskSecret        = this.AskSecret,
        FileHistory      = this.FileHistory,
        DiffStager       = this.DiffStager,
        Cursors          = this.Cursors,
        BeginResponse    = this.BeginResponse,
        EndResponse      = this.EndResponse,
        StreamText       = this.StreamText,
        OnDebug          = this.OnDebug,
        Output           = this.Output,
        Interaction      = this.Interaction,
        AgentDepth       = newDepth,
    };
}
