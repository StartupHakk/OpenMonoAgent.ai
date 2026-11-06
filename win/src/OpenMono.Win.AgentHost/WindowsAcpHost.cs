using OpenMono.Acp;

namespace OpenMono.Win.AgentHost;

// Starts ACP on Windows by reusing the existing ACP server, not rewriting it.
// The ACP HTTP API (Kestrel on localhost:7475, /api/v1/... routes, SSE turns)
// is platform-neutral; what is Docker-specific upstream is only the launch
// context: HOST_WORKSPACE_PATH (the extension's DockerManager normally sets
// it) and the "/workspace" container mount baked into the lock file writer.
// This host sets both from the real Windows workspace before serving, so the
// VS Code extension talks to Windows over the exact same API it uses on
// Mac/Linux. No upstream file is edited; if HOST_WORKSPACE_PATH is already
// set (extension-managed launch), it is left alone.
public static class WindowsAcpHost
{
    public const string HostWorkspaceEnvVar = "HOST_WORKSPACE_PATH";

    public const string HostPortEnvVar = "HOST_ACP_PORT";

    public const int DefaultPort = 7475;

    public sealed record AcpPreparation(
        AcpServerSettings Settings,
        AcpLockFileWriter LockFile,
        string Workspace,
        bool WorkspaceEnvWasAlreadySet);

    public static AcpPreparation Prepare(string windowsWorkspace, int port = DefaultPort)
    {
        var alreadySet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostWorkspaceEnvVar));
        if (!alreadySet)
        {
            Environment.SetEnvironmentVariable(HostWorkspaceEnvVar, windowsWorkspace);
        }

        var settings = new AcpServerSettings
        {
            Enabled = true,
            Port = ResolvePort(port),
            BindAllInterfaces = false,
        };

        // The lock writer's workspaceMount parameter replaces the "/workspace"
        // container default with the native Windows workspace, so agent.lock
        // lands next to the code the extension already has open.
        var lockFile = new AcpLockFileWriter(settings, workspaceMount: windowsWorkspace);

        var workspace = Environment.GetEnvironmentVariable(HostWorkspaceEnvVar) ?? windowsWorkspace;
        return new AcpPreparation(settings, lockFile, workspace, alreadySet);
    }

    private static int ResolvePort(int preferred)
    {
        var fromEnv = Environment.GetEnvironmentVariable(HostPortEnvVar);
        return int.TryParse(fromEnv, out var parsed) && parsed > 0 ? parsed : preferred;
    }
}
