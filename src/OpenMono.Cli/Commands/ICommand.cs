namespace OpenMono.Commands;

public enum CommandType
{
    Prompt,
    Local
}

public interface ICommand
{
    string Name { get; }
    string Description { get; }
    CommandType Type { get; }
    Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct);
}

public sealed class CommandContext
{
    public required Session.SessionState Session { get; init; }
    public required Tools.ToolRegistry ToolRegistry { get; init; }
    public required CommandRegistry CommandRegistry { get; init; }
    public required Config.AppConfig Config { get; init; }
    public required Rendering.IRenderer Renderer { get; init; }
    public required string WorkingDirectory { get; init; }
}
