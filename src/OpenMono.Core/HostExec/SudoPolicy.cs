namespace OpenMono.HostBridge;

/// <summary>
/// Privilege-escalation policy for host commands (config key: allow_sudo).
/// Mirrors host_exec.default. Lives in the shared kernel so the daemon, the
/// bridge, and the sandbox agent's status reporting all speak one enum.
/// </summary>
public enum SudoPolicy
{
    /// <summary>Every elevation asks the operator per command (y/N). A "yes"
    /// is in-memory only for that command — the config stays ask until the
    /// operator explicitly changes it. Non-interactive runs fail closed (deny).</summary>
    Ask,
    /// <summary>Elevations run without prompting (password still prompted
    /// once in memory when needed).</summary>
    Allow,
    /// <summary>Hard refusal of every elevation.</summary>
    Deny,
}
