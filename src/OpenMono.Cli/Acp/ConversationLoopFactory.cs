using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Acp;


public sealed class ConversationLoopFactory
{
    private readonly ToolRegistry _tools;
    private readonly AppConfig _config;
    private readonly IOutputSink _output;
    private readonly IInputReader _input;
    private readonly ILiveFeedback? _liveFeedback;
    private readonly Playbooks.PlaybookRegistry? _playbookRegistry;
    private readonly Memory.MemoryStore? _memoryStore;

    public AppConfig Config => _config;

    public ConversationLoopFactory(
        ToolRegistry tools,
        AppConfig config,
        IOutputSink output,
        IInputReader input,
        ILiveFeedback? liveFeedback = null,
        Playbooks.PlaybookRegistry? playbookRegistry = null,
        Memory.MemoryStore? memoryStore = null)
    {
        _tools = tools;
        _config = config;
        _output = output;
        _input = input;
        _liveFeedback = liveFeedback;
        _playbookRegistry = playbookRegistry;
        _memoryStore = memoryStore;
    }


    public ConversationLoop Create(
        ILlmClient llm,
        SessionState session,
        IAcpEventSink sink,
        IAcpUserInteraction interaction,
        PermissionStore store)
    {
        var placeholderPermissions = new PermissionEngine(_config, _output, _input, store: store);
        return new ConversationLoop(
            llm,
            _tools,
            placeholderPermissions,
            _output,
            _input,
            _liveFeedback,
            _config,
            session,
            sink: sink,
            interaction: interaction,
            store: store);
    }
}
