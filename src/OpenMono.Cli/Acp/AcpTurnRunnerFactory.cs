using OpenMono.Memory;
using OpenMono.Playbooks;

namespace OpenMono.Acp;



public sealed class AcpTurnRunnerFactory
{
    private readonly ConversationLoopFactory _loopFactory;
    private readonly AcpServerSettings _settings;
    private readonly PlaybookRegistry? _playbookRegistry;
    private readonly MemoryStore? _memoryStore;

    public AcpTurnRunnerFactory(
        ConversationLoopFactory loopFactory,
        AcpServerSettings settings,
        PlaybookRegistry? playbookRegistry = null,
        MemoryStore? memoryStore = null)
    {
        _loopFactory = loopFactory;
        _settings = settings;
        _playbookRegistry = playbookRegistry;
        _memoryStore = memoryStore;
    }

    public AcpTurnRunner Create(AcpSession session, SseWriter writer)
        => new AcpTurnRunner(session, writer, _loopFactory, _settings, _playbookRegistry, _memoryStore);
}
