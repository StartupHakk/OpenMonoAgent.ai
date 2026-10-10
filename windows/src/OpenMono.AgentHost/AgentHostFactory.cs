using System.Reflection;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Builds the in-process agent object graph additively: the same OMA pieces
/// Program.cs wires (ToolRegistry, PermissionEngine, OpenAiCompatClient,
/// ConversationLoop) but with Windows decorator tools registered first so they
/// win without editing OMA. Agent role only, not agent host.
/// </summary>
public static class AgentHostFactory
{
    public sealed record AgentSession(
        AppConfig Config,
        SessionState Session,
        ToolRegistry Tools,
        PermissionEngine Permissions,
        ChatOutputSink Output,
        ChatInputReader Input,
        ConversationLoop Loop)
    {
        public Task RunTurnAsync(string userInput, CancellationToken ct = default) =>
            Loop.RunTurnAsync(userInput, null, ct);
    }

    public static AppConfig CreateConfig(
        SupervisorConfig supervisor,
        string modelAlias,
        int ctxSize,
        string workspace)
    {
        var config = new AppConfig
        {
            WorkingDirectory = workspace,
            DataDirectory = supervisor.DataDirectory,
            VisionEnabled = supervisor.VisionEnabled,
            AcpServer = new OpenMono.Acp.AcpServerSettings
            {
                Enabled = false,
                Port = supervisor.AcpPort,
                BindAllInterfaces = false,
            },
        };
        config.Llm.Endpoint = supervisor.RemoteEndpointOverride ?? supervisor.LlamaEndpoint;
        config.Llm.Model = modelAlias;
        config.Llm.ContextSize = ctxSize;
        if (!string.IsNullOrWhiteSpace(supervisor.ApiKey))
        {
            config.Llm.ApiKey = supervisor.ApiKey;
        }

        if (supervisor.DockerServicesEnabled)
        {
            config.Web.Gateway = supervisor.GatewayEndpoint;
        }

        WindowsPermissionDefaults.Apply(config);
        return config;
    }

    public static ToolRegistry CreateRegistry()
    {
        var registry = new ToolRegistry();
        // Windows override wins: registered first under the same name.
        registry.Register(new WindowsShellTool());
        foreach (var tool in CreateOmaDefaultTools())
        {
            if (registry.Resolve(tool.Name) is not null)
            {
                continue;
            }

            registry.Register(tool);
        }

        return registry;
    }

    /// <summary>
    /// Instantiates parameterless OMA tools by reflection so new upstream tools
    /// appear automatically. Tools that need constructed services are skipped
    /// here and documented in SkippedTools; the Desktop wires them when their
    /// services exist. Never edits OMA.
    /// </summary>
    public static IReadOnlyList<ITool> CreateOmaDefaultTools()
    {
        var tools = new List<ITool>();
        var skipped = new List<string>();
        var assembly = typeof(BashTool).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            if (!typeof(ITool).IsAssignableFrom(type))
            {
                continue;
            }

            if (type == typeof(BashTool))
            {
                continue;
            }

            if (TryCreate(type) is { } tool)
            {
                tools.Add(tool);
            }
            else
            {
                skipped.Add(type.Name);
            }
        }

        SkippedTools = skipped;
        return tools;
    }

    public static IReadOnlyList<string> SkippedTools { get; private set; } = [];

    private static ITool? TryCreate(Type type)
    {
        try
        {
            foreach (var ctor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
            {
                var parameters = ctor.GetParameters();
                if (parameters.All(p => p.HasDefaultValue))
                {
                    var args = parameters.Select(p => p.DefaultValue).ToArray();
                    if (Activator.CreateInstance(type, args) is ITool tool)
                    {
                        return tool;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static AgentSession CreateSession(
        SupervisorConfig supervisor,
        string modelAlias,
        int ctxSize,
        string workspace,
        Func<string, string, CancellationToken, Task<PermissionChoice>> choosePermission,
        Func<string, IReadOnlyList<string>?, CancellationToken, Task<string>> askUser)
    {
        var config = CreateConfig(supervisor, modelAlias, ctxSize, workspace);
        var session = new SessionState { Model = modelAlias };
        var tools = CreateRegistry();
        var output = new ChatOutputSink();
        var input = new ChatInputReader(choosePermission, askUser);
        var permissions = new PermissionEngine(config, output, input);
        ILlmClient llm = new OpenAiCompatClient(config.Llm);
        var loop = new ConversationLoop(llm, tools, permissions, output, input, null, config, session);
        return new AgentSession(config, session, tools, permissions, output, input, loop);
    }

    /// <summary>
    /// Writes the standard OMA settings.json surface from the first run wizard
    /// so behavior matches Linux and macOS. Delegates to <see cref="OmaSettingsWriter"/>,
    /// which serializes snake_case via <c>OpenMono.Config.JsonOptions</c> and
    /// preserves user-managed sections. Kept here so existing callers
    /// (ServerPage, wizard) do not change.
    /// </summary>
    public static void WriteSettings(
        string dataDirectory,
        string endpoint,
        string modelAlias,
        int ctxSize,
        bool visionEnabled,
        int acpPort,
        bool acpEnabled,
        string? apiKey = null)
    {
        OmaSettingsWriter.Write(dataDirectory, endpoint, modelAlias, ctxSize, visionEnabled, acpPort, acpEnabled, apiKey);
    }
}
