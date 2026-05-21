using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMono.Acp;

/// <summary>
/// Writes <c>&lt;workspace&gt;/.openmono/agent.lock</c> after Kestrel binds so the VS Code
/// extension can discover the agent. The lock file records the <b>host-facing</b> port
/// (which differs from the container-internal port when Docker port-publishes), the
/// host workspace path (which the extension verifies against the VS Code workspace
/// folder it's connecting from), and the agent id (used by the extension's
/// label-ownership rule when deciding whether to stop a container it spawned).
///
/// The three pieces of host context come from env vars set by the extension's
/// <c>docker run</c> command — or by a user's docker-compose file in the manual mode.
/// </summary>
public sealed class AcpLockFileWriter
{
    private readonly string _path;
    private readonly LockPayload _payload;
    private bool _written;

    public AcpLockFileWriter(AcpServerSettings settings)
        : this(settings, workspaceMount: "/workspace")
    {
    }

    /// <summary>
    /// Test/seam constructor: lets the lock file be written under a temp dir instead
    /// of the production bind-mount path.
    /// </summary>
    public AcpLockFileWriter(AcpServerSettings settings, string workspaceMount)
    {
        ContainerWorkspace = workspaceMount;
        var dir = Path.Combine(workspaceMount, ".openmono");
        _path = Path.Combine(dir, "agent.lock");

        var hostPort = ParseIntOrDefault(Environment.GetEnvironmentVariable("HOST_ACP_PORT"), settings.Port);
        var hostWorkspace = Environment.GetEnvironmentVariable("HOST_WORKSPACE_PATH")
            ?? throw new InvalidOperationException(
                "HOST_WORKSPACE_PATH env var is required. The extension's DockerManager " +
                "always sets it; a user-managed docker-compose setup must declare it too.");
        var agentId = Environment.GetEnvironmentVariable("ACP_AGENT_ID") ?? GenerateAgentId();
        var containerId = Environment.GetEnvironmentVariable("HOSTNAME") ?? "unknown";

        _payload = new LockPayload(
            version: "1.0.0",
            agent_id: agentId,
            host_workspace: hostWorkspace,
            port: hostPort,
            container_id: containerId,
            started_at: DateTime.UtcNow.ToString("o"));
    }

    public string LockFilePath => _path;
    public string AgentId => _payload.agent_id;
    public string HostWorkspace => _payload.host_workspace;
    public int HostPort => _payload.port;
    public string ContainerId => _payload.container_id;

    /// <summary>
    /// The path the agent uses internally to read/write workspace files.
    /// In Docker this is "/workspace" (the bind-mount). For native runs this is the
    /// host working directory (same as HostWorkspace). Used by GET /api/v1/discovery
    /// so the extension can verify the agent's view of the workspace.
    /// </summary>
    public string ContainerWorkspace { get; }

    /// <summary>Write the lock file. Idempotent; safe to call once Kestrel is listening.</summary>
    public void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_payload, LockJsonOpts));
        _written = true;
    }

    /// <summary>
    /// Remove the lock file on graceful shutdown. No-op if Write was never called.
    /// Best-effort: errors are swallowed so this can run from a finally / IHostedService.StopAsync.
    /// </summary>
    public void TryRemove()
    {
        if (!_written) return;
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    private static readonly JsonSerializerOptions LockJsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed record LockPayload(
        string version,
        string agent_id,
        string host_workspace,
        int port,
        string container_id,
        string started_at);

    private static string GenerateAgentId()
        => "agt_" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant();

    private static int ParseIntOrDefault(string? s, int @default)
        => int.TryParse(s, out var v) ? v : @default;
}
