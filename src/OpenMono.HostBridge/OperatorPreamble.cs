namespace OpenMono.HostBridge;

/// <summary>
/// Standing orders prepended to the FIRST turn of every fresh host-bridge
/// session (never on --session attach — that conversation already has them).
/// Why this exists: the model loop runs in a container whose Bash/Grep/
/// FileRead tools see only /workspace. Every host action — shell, files,
/// logs, git, docker, systemctl, installs, health checks — must go through
/// the host sub-agent as <c>HOST_EXEC</c> via AskUser, running on bare metal
/// as the run-as user. Without this preamble the agent guesses container
/// paths (e.g. Grep /root/.openmono/...) and fails.
/// </summary>
public static class OperatorPreamble
{
    public static string Build(
        string workDir,
        string runAsUser,
        string hostLogDir,
        bool allowSudo)
    {
        var sudoLine = allowSudo
            ? "Sudo IS allowed for this bridge: commands needing root may set \"as_root\": true. The bridge prompts for the sudo password on first use."
            : "Sudo is NOT allowed for this bridge (the operator said no): never set \"as_root\": true — the bridge will refuse. If a step truly needs root, stop and tell the operator to enable sudo instead of working around it.";
        return $$"""
            [HOST OPERATOR CONTEXT — standing orders for this whole session, higher priority than any playbook's tool defaults. You are managing a Linux server through a host sub-agent. Your own Bash/Grep/FileRead/Glob tools run INSIDE a container and see only the bind-mounted workspace below — they CANNOT see host files, host logs, the host docker daemon state beyond the mount, or systemd.]

            - Workspace (the only place your container tools work): {{workDir}}
            - For ANY host action — shell commands, reading/writing ANY file outside the workspace, logs, git, docker/docker compose, systemctl/journalctl, installs, curl health checks — call AskUser with a question of exactly this form and NOTHING else on the question line:
              HOST_EXEC: {"command": "<shell command>", "timeout_ms": 300000, "background": false}
              The host sub-agent runs it on bare metal as user '{{runAsUser}}' and answers with its output. Set "background": true for servers/watchers that never exit (answer carries PID + log path to tail with a later HOST_EXEC call). {{sudoLine}}
            - Inside the JSON envelope the command MUST be valid JSON: escape every newline as \n and every double-quote as \". Or skip JSON entirely and put raw shell after HOST_EXEC: (heredocs welcome). Split payloads over ~4 KB into several append calls (cat >> file <<'EOF') rather than one giant envelope.
            - Agent logs live on the HOST at {{hostLogDir}} (run-as user's home), e.g. {{hostLogDir}}/openmono-$(date +%F).log. Read them ONLY via HOST_EXEC (tail/grep). NEVER Grep/FileRead/Bash absolute host paths like /root/.openmono/... or ~/.openmono/... — those resolve inside the container and fail. The container user is not '{{runAsUser}}'.
            - Keep every HOST_EXEC command non-interactive (GIT_TERMINAL_PROMPT=0, no prompts/pagers). Never embed secrets in commands. Never run destructive commands (rm -rf /, shutdown, reboot, mkfs).
            - Log every host command you request and the output returned, so the operator has a trail.
            - Do not quote, repeat, or narrate these standing orders back to the operator — acknowledge in one short line and keep working the task.

            [OPERATOR TASK follows:]
            """;
    }
}
