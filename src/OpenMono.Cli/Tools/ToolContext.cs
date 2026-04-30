using OpenMono.History;
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
}
