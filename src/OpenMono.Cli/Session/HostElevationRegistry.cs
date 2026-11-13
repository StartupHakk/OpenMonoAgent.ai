using System.Collections.Concurrent;

namespace OpenMono.Session;

/// <summary>
/// One-shot handoff of the sudo credential between the surface that collected
/// it (the ACP permission-resume loop verifies it against the daemon) and the
/// HostExec tool that runs the real command with it. Keyed by session id;
/// <see cref="Take"/> consumes the entry so a verified password can never ride
/// a second command. A terminal elevation failure is attached instead of a
/// password, so the re-driven tool call reports the honest outcome to the
/// model loop. Strings cannot be zeroed in .NET — the gateway wipes its byte
/// copy on every return path; nothing here is ever logged or persisted.
/// </summary>
public sealed record ElevationGrant(string? Password, string? Failure)
{
    public static ElevationGrant Verified(string password) => new(password, null);
    public static ElevationGrant Failed(string modelMessage) => new(null, modelMessage);
}

public static class HostElevationRegistry
{
    private static readonly ConcurrentDictionary<string, ElevationGrant> _grants = new();

    public static void Attach(string sessionId, ElevationGrant grant) => _grants[sessionId] = grant;

    /// <summary>Consume this session's elevation grant (null = none). Removed
    /// before returning — one verified consent covers exactly one elevated
    /// exec.</summary>
    public static ElevationGrant? Take(string sessionId) =>
        _grants.TryRemove(sessionId, out var g) ? g : null;

    public static void Clear(string sessionId) => _grants.TryRemove(sessionId, out _);

    public static void ClearAll() => _grants.Clear();
}
