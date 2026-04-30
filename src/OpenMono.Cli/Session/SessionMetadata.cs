using OpenMono.History;

namespace OpenMono.Session;

public sealed class SessionMetadata
{
    public FileHistory? FileHistory { get; set; }
    public TokenTracker? TokenTracker { get; set; }
    public bool PlanMode { get; set; }

    public bool ThinkingEnabled { get; set; }
}
