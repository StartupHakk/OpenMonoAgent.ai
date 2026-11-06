namespace OpenMono.Win.AgentHost;

// ACP reachability on Windows. The ACP server itself runs inside the agent
// container (same Kestrel server, same /api/v1/... routes, same SSE turns as
// Mac/Linux); the Windows side only needs the published address and the lock
// file location the VS Code extension discovers. No server code is duplicated
// or reconfigured here.
public static class WindowsAcpWiring
{
    public const int DefaultHostPort = 7475;

    public const int ContainerPort = 7475;

    public static string PortPublishArg(int hostPort) =>
        $"-p 127.0.0.1:{hostPort}:{ContainerPort}";

    public static string AgentUrl(int hostPort) =>
        $"http://127.0.0.1:{hostPort}";

    public static string DiscoveryUrl(int hostPort) =>
        AgentUrl(hostPort) + "/api/v1/discovery";

    // Where the extension looks for agent.lock, next to the open workspace.
    // Written by the in-container server; the host only reads it.
    public static string AgentLockPath(string windowsWorkspace) =>
        Path.Combine(windowsWorkspace, ".openmono", "agent.lock");
}
