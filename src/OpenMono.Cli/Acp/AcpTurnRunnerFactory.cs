using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Playbooks;
using OpenMono.Memory;

namespace OpenMono.Acp;

public sealed class AcpTurnRunnerFactory
{
    private readonly ConversationLoopFactory _loopFactory;
    private readonly AcpServerSettings _settings;
    private readonly PlaybookRegistry? _playbookRegistry;
    private readonly MemoryStore? _memoryStore;
    private readonly BackendRegistry _backendRegistry;
    private readonly LlmClientPool _clientPool;

    public AcpTurnRunnerFactory(
        ConversationLoopFactory loopFactory,
        AcpServerSettings settings,
        PlaybookRegistry? playbookRegistry = null,
        MemoryStore? memoryStore = null,
        BackendRegistry? backendRegistry = null,
        LlmClientPool? clientPool = null)
    {
        _loopFactory = loopFactory;
        _settings = settings;
        _playbookRegistry = playbookRegistry;
        _memoryStore = memoryStore;
        _backendRegistry = backendRegistry ?? new BackendRegistry();
        _clientPool = clientPool ?? new LlmClientPool();
    }

    public AcpTurnRunner Create(AcpSession session, SseWriter writer)
        => new AcpTurnRunner(
            session,
            writer,
            _loopFactory,
            _settings,
            _backendRegistry,
            _clientPool,
            _playbookRegistry,
            _memoryStore);
}
