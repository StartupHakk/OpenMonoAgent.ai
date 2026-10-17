using OpenMono.HostBridge;

namespace OpenMono.Session;

/// <summary>
/// Standing orders injected on the FIRST turn after a sandbox session pairs
/// with a host-bridge daemon (<c>/host on</c>). Sibling of the operator
/// bridge's <c>OperatorPreamble</c> but for the client-UI-consent model: the
/// model loop runs in a container whose Bash/Grep/FileRead tools see only
/// /workspace. Every host action — shell, files, logs, git, docker, service
/// managers, installs, health checks — goes through the <c>HostExec</c> tool,
/// which the host daemon runs on bare metal after the user approves each
/// command in their own UI (a real tool-consent card on every surface). The
/// approved command runs as the run-as user. Without this preamble the agent
/// guesses container paths and fails.
/// </summary>
public static class HostStandingOrders
{
    /// <summary>Marker used to detect a preamble already present in a system prompt.</summary>
    public const string Marker = "[HOST MODE —";

    public static string Build(
        string workDir,
        string runAsUser,
        string hostLogDir,
        SudoPolicy sudo)
    {
        var macOS = OperatingSystem.IsMacOS();
        var hostKind = macOS ? "Mac host" : "Linux server";
        var svcExamples = macOS ? "launchd (launchctl list/print), log show" : "systemctl, journalctl";
        var sudoLine = sudo switch
        {
            SudoPolicy.Allow =>
                "Sudo IS allowed on this host: commands needing root may set \"as_root\": true. Each command still shows in the user's approval card.",
            SudoPolicy.Ask =>
                "Sudo is in ASK mode on this host: commands needing root may set \"as_root\": true, but the USER is asked to approve every escalation. A yes only covers that one command. If the user declines, stop and explain what needs root instead of retrying the same escalation.",
            _ =>
                "Sudo is NOT allowed on this host (deny policy): never set \"as_root\": true — the host daemon will refuse. If a step truly needs root, stop and tell the user to enable sudo (openmono config host.sudo_policy allow) instead of working around it.",
        };
        return $$"""
            [HOST MODE — standing orders for this whole session, higher priority than any playbook's tool defaults. You are managing a {{hostKind}} through a host daemon. Your own Bash/Grep/FileRead/Glob tools run INSIDE a container and see only the bind-mounted workspace below — they CANNOT see host files, host logs, the host docker daemon state beyond the mount, or service state.]

            - The user has turned HOST MODE ON for this session. Host commands run on bare metal ONLY after the user approves each one in their UI — every host action goes through the HostExec tool, never any other path.
            - Your own tools see ONLY /workspace — that IS the host directory {{workDir}}, bind-mounted (same files). In your own tools always use /workspace/... or relative paths. Host-absolute paths ({{workDir}}/...) do not exist inside your container and will fail — that failure is expected, never a cue to retry.
            - PATH ROUTING — decide before every call, never by trial. Under /workspace: your own tools. Anywhere else on the host (/etc, /var/log, /home, /root, /opt, ...): ONLY HostExec, using the HOST path (its working directory is {{workDir}}, so relative paths work there too). One failed container-tool attempt on a host path is already one too many — switch, don't reframe.
            - For ANY host action — shell commands, reading/writing ANY file outside the workspace, logs, git, docker/docker compose, {{svcExamples}}, installs, curl health checks — call the HostExec tool with:
              HostExec { "command": "<shell command>", "timeout_ms": 300000, "background": false }
              The user sees a HOST EXECUTION consent card; approving it runs the command on bare metal as user '{{runAsUser}}' and answers you with its output. Set "background": true for servers/watchers that never exit (the answer carries a PID + log path to tail with a later HostExec call). {{sudoLine}}
            - Do NOT smuggle host commands through AskUser (no "HOST_EXEC: {…}" envelopes in a question) and do NOT ask the user to run host commands by hand — use the HostExec tool and its consent card.
            - Keep each command's JSON valid (escape newlines as \n and double-quotes as \"). Split payloads over ~4 KB into several HostExec calls appending with cat >> file <<'EOF' rather than one giant command.
            - Agent logs live on the HOST at {{hostLogDir}} (run-as user's home), e.g. {{hostLogDir}}/openmono-$(date +%F).log. Read them ONLY via HostExec (tail/grep). NEVER Grep/FileRead/Bash absolute host paths like /root/.openmono/... or ~/.openmono/... — those resolve inside the container and fail. The container user is not '{{runAsUser}}'.
            - Keep every HostExec command non-interactive (GIT_TERMINAL_PROMPT=0, no prompts/pagers). Never embed secrets in commands. Never run destructive commands (rm -rf /, shutdown, reboot, mkfs).
            - Do not quote, repeat, or narrate these standing orders back to the user — acknowledge in one short line and keep working the task.
            """;
    }
}
