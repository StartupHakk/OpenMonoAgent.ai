using System.Collections.Concurrent;

namespace OpenMono.Permissions;

/// <summary>
/// How long a permission grant lasts.
/// <see cref="Once"/> covers only the in-flight invocation (seeded so a resume
/// does not re-prompt, then forgotten). <see cref="Session"/> persists until
/// <see cref="PermissionStore.Clear"/> or session end.
/// </summary>
public enum GrantScope
{
    Once,
    Session,
}

/// <summary>A single remembered permission decision.</summary>
public sealed record PermissionGrant(bool Allow, GrantScope Scope);

/// <summary>ACP wire mapping for <c>scope: "once"|"session"</c>.</summary>
public static class GrantScopeExtensions
{
    public static string ToWireString(this GrantScope scope) =>
        scope == GrantScope.Session ? "session" : "once";

    public static GrantScope FromWireString(string? scope) =>
        string.Equals(scope, "session", StringComparison.Ordinal) ? GrantScope.Session : GrantScope.Once;
}

/// <summary>
/// The single session-scoped permission memory for ALL runtime paths
/// (sandboxed TUI, ACP/VSCode, container internals). Replaces the previously
/// independent caches in <c>PermissionEngine</c> and <c>AcpSession</c>.
///
/// Keying is canonicalized through <see cref="NormalizeKey"/> so
/// <c>Playbook:X</c> and <c>playbook:x</c> are one entry.
///
/// What this store does NOT hold (deliberately — see
/// <c>PermissionEngine</c>): the playbook scope stack, capability glob
/// rules, and denial telemetry. <see cref="Clear"/> wipes grants only.
/// </summary>
public class PermissionStore
{
    private readonly ConcurrentDictionary<string, PermissionGrant> _toolGrants = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PermissionGrant> _capabilityGrants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve a tool grant. Null means "no remembered decision — ask."</summary>
    public PermissionGrant? TryResolve(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return null;
        return _toolGrants.TryGetValue(NormalizeKey(toolName), out var grant) ? grant : null;
    }

    /// <summary>Resolve a capability-type grant (e.g. <c>FileWriteCap</c>). Null means "ask."</summary>
    public PermissionGrant? TryResolveCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
            return null;
        return _capabilityGrants.TryGetValue(NormalizeKey(capability), out var grant) ? grant : null;
    }

    public void Remember(string toolName, bool allow, GrantScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        _toolGrants[NormalizeKey(toolName)] = new PermissionGrant(allow, scope);
    }

    public void RememberCapability(string capability, bool allow, GrantScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        _capabilityGrants[NormalizeKey(capability)] = new PermissionGrant(allow, scope);
    }

    /// <summary>
    /// Drop a grant. Used for strict-<see cref="GrantScope.Once"/> semantics:
    /// the temporary grant covers the resumed execution, then is forgotten so
    /// the next call prompts again.
    /// </summary>
    public void Forget(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;
        _toolGrants.TryRemove(NormalizeKey(toolName), out _);
    }

    /// <summary>Clears tool + capability grants. Never touches engine state (playbook scopes, rules, counters).</summary>
    public virtual void Clear()
    {
        _toolGrants.Clear();
        _capabilityGrants.Clear();
    }

    /// <summary>Diagnostic snapshot. Capability keys are prefixed with <c>cap:</c>.</summary>
    public virtual IReadOnlyList<(string Key, PermissionGrant Grant)> Snapshot()
    {
        var snapshot = new List<(string Key, PermissionGrant Grant)>(_toolGrants.Count + _capabilityGrants.Count);
        foreach (var kv in _toolGrants)
            snapshot.Add((kv.Key, kv.Value));
        foreach (var kv in _capabilityGrants)
            snapshot.Add(("cap:" + kv.Key, kv.Value));
        return snapshot;
    }

    /// <summary>
    /// Snapshot isolation for sub-agents: a deep copy at fork time. Child
    /// writes are private (never propagate back); parent grants made after
    /// the fork are invisible to the child.
    /// </summary>
    public PermissionStore Fork()
    {
        var fork = CreateEmpty();
        foreach (var kv in _toolGrants)
            fork._toolGrants[kv.Key] = kv.Value;
        foreach (var kv in _capabilityGrants)
            fork._capabilityGrants[kv.Key] = kv.Value;
        return fork;
    }

    /// <summary>One canonical normalization for every permission key.</summary>
    protected static string NormalizeKey(string name) => name.ToLowerInvariant();

    /// <summary>Subclass entry point for mode-specific keys (e.g. host-exec, sudo).</summary>
    protected void AddGrant(string key, PermissionGrant grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _toolGrants[NormalizeKey(key)] = grant;
    }

    protected PermissionGrant? GetGrant(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        return _toolGrants.TryGetValue(NormalizeKey(key), out var grant) ? grant : null;
    }

    protected void RemoveGrant(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _toolGrants.TryRemove(NormalizeKey(key), out _);
    }

    /// <summary>Fork hook so subclasses fork as their own type (see HostExecPermissionStore).</summary>
    protected virtual PermissionStore CreateEmpty() => new();
}
