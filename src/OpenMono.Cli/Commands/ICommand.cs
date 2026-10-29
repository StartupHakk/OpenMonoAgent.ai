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
    bool SafeDuringActiveTurn => false;
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
    public required Llm.ILlmClient Llm { get; init; }

    /// <summary>In-memory backend definitions (see <c>BackendRegistry</c>). Null in tests
    /// where the command doesn't need it.</summary>
    public Llm.BackendRegistry? BackendRegistry { get; init; }

    /// <summary>Per-backend client cache (see <c>LlmClientPool</c>). Null in tests where
    /// the command doesn't need it.</summary>
    public Llm.LlmClientPool? LlmClientPool { get; init; }
}
