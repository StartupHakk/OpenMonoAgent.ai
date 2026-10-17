using OpenMono.HostBridge;

namespace OpenMono.Session;

/// <summary>
/// Registry of live host-daemon gateways, keyed by session id. The gateway is
/// runtime state (HTTP client + session token) — never persisted; on restart
/// the launcher re-pairs via env and <c>openmono agent --host</c> re-pairs via
/// <c>/host on</c>.
/// </summary>
public static class HostGatewayRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HostGateway> Gateways = new();

    public static void Register(string sessionId, HostGateway gateway) => Gateways[sessionId] = gateway;

    public static HostGateway? Get(string sessionId) =>
        Gateways.TryGetValue(sessionId, out var g) ? g : null;

    public static bool Unregister(string sessionId) => Gateways.TryRemove(sessionId, out _);
}
