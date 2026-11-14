using System.Text.Json;
using OpenMono.HostBridge;
using OpenMono.Permissions;
using OpenMono.Session;

namespace OpenMono.Tools;

/// <summary>
/// Runs a shell command on the HOST machine through the paired host-bridge
/// daemon. This is the first-class host-action surface — it replaces the old
/// "HOST_EXEC envelope smuggled through AskUser" path, which rendered as a
/// free-text question box instead of a tool call.
///
/// The command is gated by the STANDARD permission pipeline (PermissionLevel.Ask),
/// so the human approves it in their own UI as a consent card on every surface
/// (TUI choice card / ACP extension permission_request), and the command row
/// renders as a real tool call. Once consented, the command runs on bare metal
/// via the daemon, which re-enforces the deny-list + sudo policy and audits it.
///
/// The tool is only OFFERED while the session is paired with a host daemon
/// (HostEnabled). When host is off it is filtered out of the tool definitions
/// and hard-blocked at execution — so the model literally cannot host-exec.
/// </summary>
public sealed class HostExecTool : ToolBase
{
    public override string Name => "HostExec";

    public override string Description =>
        "Run a shell command on the HOST machine (bare metal), not inside the container. Available only while " +
        "Host mode is ON. Your Bash/FileRead/Grep/Glob tools see ONLY the container workspace and cannot touch " +
        "host paths (/etc, /var, /home, service managers, the host docker daemon, host logs). Use HostExec for " +
        "any host-side action: host files outside the workspace, host logs, git/docker/system commands, service " +
        "managers (systemctl/journalctl or launchctl), installs, curl health checks. Each command is approved by " +
        "the user in their UI before it runs on bare metal as the host run-as user. Set \"background\": true for " +
        "servers/watchers that never exit (the answer carries a PID + log path to tail later). Set \"as_root\": " +
        "true only when a step truly needs root, and only if the host sudo policy allows/asks for it.";

    public override bool IsReadOnly => false;
    public override PermissionLevel DefaultPermission => PermissionLevel.Ask;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("command", "The shell command to run on the host")
        .AddInteger("timeout_ms", "Timeout in milliseconds (default 300000, max 600000); ignored when background is true")
        .AddBoolean("background", "If true, launch detached and return immediately with a PID + log path (for servers/watchers)")
        .AddBoolean("as_root", "If true, run with sudo — only honored when the host sudo policy allows/asks for it")
        .Require("command");

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var gateway = HostGatewayRegistry.Get(context.Session.Id);
        if (gateway is null)
            return ToolResult.InvalidInput(
                "Host mode is OFF for this session — the HostExec tool is unavailable and no host command ran.",
                "Do NOT retry this call or fake it with Bash. If a host action is needed, tell the user to turn " +
                "Host on (the Host toggle), then call HostExec again.");

        var request = FromInput(input);

        // Root escalation when the host policy is not "allow": consent alone
        // is not enough — the user must also prove they hold the sudo
        // credential. The password loop runs INSIDE this tool call (after
        // the permission card above), so the conversation loop cannot resume
        // between attempts; it resumes only with the terminal outcome. Each
        // submit is verified on the host with `sudo -v` (no payload runs);
        // wrong/empty passwords re-prompt in place up to MaxAttempts total;
        // a cancel ends the flow immediately. On success the VERIFIED
        // password rides the single following real exec, one-shot.
        string? verifiedPassword = null;
        if (request.AsRoot && gateway.Sudo != SudoPolicy.Allow)
        {
            if (context.Interaction is not null)
            {
                // ACP surface (VS Code / extension): the permission pause
                // carried the password channel — the resume loop already
                // verified each attempt against the daemon (/sudo/verify) and
                // attached the terminal outcome here before re-driving us.
                var grant = HostElevationRegistry.Take(context.Session.Id);
                if (grant is null)
                    return ToolResult.PermissionDenied(
                        "Host escalation (sudo) needs the user's password, and the client supplied none on the " +
                        "permission response. The command did NOT run. Do NOT retry this call unchanged — re-send " +
                        "it so the user can approve the elevated card and enter the password, or ask them.");
                if (grant.Failure is not null)
                    return ToolResult.PermissionDenied(grant.Failure);
                verifiedPassword = grant.Password;
            }
            else
            {
                // TUI / operator bridge: card consent already happened; run the
                // attempt loop in place against the same gateway.
                var flow = await SudoAttemptFlow.RunAsync(context.AskSecret, gateway, ct);
                if (!flow.Verified)
                    return ToolResult.PermissionDenied(flow.ModelMessage!);
                context.Output?.WriteInfo("\u2713 sudo password verified \u2014 running this command as root (one-shot).");
                verifiedPassword = flow.Password;
            }
        }

        // Command consent was already captured by this tool's permission gate:
        // the card shows the command (and a "sudo" tag + dangerous styling when
        // as_root is set), so the gateway must not re-prompt on top of that
        // card (and a mid-body nested pause would loop the ACP resume replay).
        // The one case that must NOT be auto-allowed is a blanket session
        // allow-all silently covering a root escalation — the documented rule
        // is "allow-all never covers sudo". We can't safely show a second card
        // mid-tool here, so we refuse and tell the model to get per-call consent.
        var outcome = await gateway.RunAsync(request, (_, isRoot, _) =>
        {
            if (isRoot && gateway.SessionAllowsAll)
                return Task.FromResult(HostConsentDecision.DenyOnce);
            return Task.FromResult(HostConsentDecision.AllowOnce);
        }, ct, verifiedPassword);

        if (request.AsRoot && gateway.SessionAllowsAll && outcome.ErrorCode == HostExecOutcome.ErrUserDenied)
            return ToolResult.PermissionDenied(
                $"Host escalation (sudo) refused: a blanket host allow-all never covers root. " +
                "Re-send this HostExec call with as_root and get the user to approve THIS command explicitly.");

        // Honest tool status: anything that did not run renders ✗.
        return outcome.ErrorCode switch
        {
            null => ToolResult.Success(outcome.Answer),
            HostExecOutcome.ErrUserDenied => ToolResult.PermissionDenied(outcome.Answer),
            // Defensive: this surface has no password modal; a password
            // failure here means the escalation ran without the client's
            // consent card. Tell the model to route the user back to the
            // host-execution consent flow rather than retrying.
            HostExecOutcome.ErrPasswordFailure => ToolResult.Failure(
                outcome.Answer, HostExecOutcome.ErrPasswordFailure,
                "The sudo password was not accepted. Do NOT retry this call — ask the user to approve the command again via the host-execution consent card so they can enter their sudo password."),
            HostExecOutcome.ErrStale => ToolResult.Failure(
                outcome.Answer, HostExecOutcome.ErrStale,
                "Do NOT retry. Ask the user to re-pair (the Host toggle, or /host on), then call HostExec again."),
            HostExecOutcome.ErrUnreachable => ToolResult.Failure(
                outcome.Answer, HostExecOutcome.ErrUnreachable,
                "Do NOT retry or fake it with Bash. Ask the user to start the host daemon (openmono host daemon), then re-pair."),
            HostExecOutcome.ErrPolicy => ToolResult.Failure(
                outcome.Answer, HostExecOutcome.ErrPolicy,
                "The host daemon's policy refused this command — do NOT retry it unchanged or try to route around the policy."),
            _ => ToolResult.Failure(outcome.Answer, outcome.ErrorCode ?? HostExecOutcome.ErrExecFailed),
        };
    }

    private static HostExecRequest FromInput(JsonElement input)
    {
        var command = input.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
        int? timeout = input.TryGetProperty("timeout_ms", out var t) && t.TryGetInt32(out var tv) ? tv : null;
        var background = input.TryGetProperty("background", out var b) && b.ValueKind == JsonValueKind.True;
        var asRoot = input.TryGetProperty("as_root", out var r) && r.ValueKind == JsonValueKind.True;
        return new HostExecRequest(command, timeout, background, asRoot);
    }
}
