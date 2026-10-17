using System.Diagnostics;
using System.Text.Json;

namespace OpenMono.HostBridge;

/// <summary>
/// Single owner of the bare-metal HOST_EXEC decision and execution path:
/// allow→deny→ask policy, sudo escalation (ask/allow/deny), masked password
/// prompt + retry + wipe, and the audit trail. Both the interactive <see cref="Bridge"/>
/// (TUI/line) and the headless ACP serve relay call this, so host policy,
/// sudo, and the paper trail can never diverge between surfaces.
///
/// It is deliberately transport-agnostic: it only needs an <see cref="IBridgeUi"/>
/// to ask the operator and an <see cref="IHostExecPrompter"/>-shaped prompter
/// (itself) for stdin secrets. The turn pump and SSE framing stay in the caller.
/// </summary>
public sealed class HostExecOrchestrator : IHostExecPrompter
{
    private readonly BridgeConfig _config;
    private readonly HostExecutor _executor;
    private readonly HostIdentity _identity;
    private readonly IBridgeUi _ui;
    private readonly bool _nonInteractive;
    private readonly HostExecPermissionStore _store;
    private readonly string _workDir;

    public HostExecOrchestrator(
        BridgeConfig config,
        HostExecutor executor,
        HostIdentity identity,
        IBridgeUi ui,
        HostExecPermissionStore store,
        string workDir,
        bool nonInteractive)
    {
        _config = config;
        _executor = executor;
        _identity = identity;
        _ui = ui;
        _store = store;
        _workDir = workDir;
        _nonInteractive = nonInteractive;
    }

    public HostIdentity Identity => _identity;

    /// <summary>
    /// The turn-resuming answer for a HOST_EXEC envelope that claims the
    /// prefix but does not parse. Tells the agent exactly how to re-send
    /// (valid JSON with \n escapes, or raw shell, or smaller chunks) instead
    /// of parking the operator in front of a raw JSON blob forever.
    /// </summary>
    public static string MalformedEnvelopeAnswer(string question) =>
        $"HOST_EXEC envelope rejected ({question.Length} chars): it starts with HOST_EXEC: but is not " +
        "a raw shell command and not valid envelope JSON, so nothing ran. Re-send ONE of these shapes: " +
        "(1) HOST_EXEC: {\"command\": \"<shell>\", \"timeout_ms\": 30000} with real JSON inside — escape every " +
        "newline as \\n and every quote as \\\"; " +
        "(2) HOST_EXEC: <raw shell command> with no JSON at all (heredocs welcome); " +
        "(3) split payloads over ~4 KB into several HOST_EXEC calls appending with cat >>. " +
        "Never ask the operator to hand-edit JSON.";

    /// <summary>
    /// True when this operator surface can ask a human (interactive TTY). The
    /// relay uses this to decide whether an ask-policy command can be resolved
    /// or must fail closed.
    /// </summary>
    public bool CanAskHuman => !_nonInteractive && !Console.IsInputRedirected;

    /// <summary>
    /// Run one HOST_EXEC request through policy + sudo + audit and return the
    /// text answer to feed back to the model loop. Never throws on policy
    /// denials — those come back as a textual answer, mirroring the old bridge.
    /// </summary>
    public async Task<string> RunAsync(HostExecRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var decision = _executor.CheckPolicy(request.Command);
        var targetUser = request.AsRoot ? "root" : _identity.RunAsUser;

        // Sudo escalation check (allow_sudo policy). The executor's sudo gate
        // refuses Deny; Allow passes; Ask confirms here, per command, through
        // the same 4-option permission menu the regular agent uses
        // ([y] Allow / [a] Allow all / [n] Deny / [!] Deny all). A once-answer
        // is in-memory only for THIS command — config stays ask until the
        // operator explicitly changes it (openmono config host.sudo_policy).
        if (_executor.RequiresElevation(request) && _identity.Sudo == SudoPolicy.Ask)
        {
            if (_store.TryResolveSudo() is { Allow: false })
            {
                AuditLog.WriteCommand(targetUser, request.AsRoot, request.Command,
                    -1, stopwatch.ElapsedMilliseconds, false,
                    "sudo ask policy denied for session (deny all)");
                return $"Host execution denied by operator (sudo denied for session): {request.Command}";
            }
            if (_store.TryResolveSudo() is not { Allow: true })
            {
                if (!CanAskHuman)
                {
                    // Non-interactive fail closed: an ask policy can never
                    // escalate without a human at the keyboard.
                    AuditLog.WriteCommand(targetUser, request.AsRoot, request.Command,
                        -1, stopwatch.ElapsedMilliseconds, false,
                        "sudo ask policy in a non-interactive bridge (fail closed)");
                    return "Host execution denied by bridge policy (sudo ask policy, non-interactive): " + request.Command;
                }
                var (allow, scope) = await _ui.PermissionAsync(
                    "sudo", $"Run '{request.Command}' as {targetUser} with sudo?", ct);
                if (scope == "session")
                    _store.RememberSudo(allow, OpenMono.Permissions.GrantScope.Session);
                if (!allow)
                {
                    AuditLog.WriteCommand(targetUser, request.AsRoot, request.Command,
                        -1, stopwatch.ElapsedMilliseconds, false, "operator declined sudo ask");
                    return $"Host execution denied by operator (sudo ask policy): {request.Command}";
                }
            }
        }

        if (decision == HostPolicyDecision.Ask && !_nonInteractive)
        {
            if (_store.TryResolveHostExec() is { Allow: false })
            {
                decision = HostPolicyDecision.Deny;
            }
            else if (_store.TryResolveHostExec() is { Allow: true })
            {
                decision = HostPolicyDecision.Allow;
            }
            else
            {
                var (allow, scope) = await _ui.PermissionAsync("host-exec", request.Command, ct);
                if (scope == "session")
                    _store.RememberHostExec(allow, OpenMono.Permissions.GrantScope.Session);
                decision = allow ? HostPolicyDecision.Allow : HostPolicyDecision.Deny;
            }
        }
        if (decision != HostPolicyDecision.Allow)
        {
            var denial = decision == HostPolicyDecision.Ask
                ? "non-interactive bridge denied an ask-policy command"
                : "denied by bridge policy";
            AuditLog.WriteCommand(targetUser, request.AsRoot, request.Command,
                -1, stopwatch.ElapsedMilliseconds, false, denial);
            return $"Host execution denied by bridge policy{(decision == HostPolicyDecision.Ask ? " (non-interactive)" : "")}: {request.Command}";
        }

        // Lazy password: only when this exact escalation needs one we do not
        // have. Masked prompt, memory-only, never logged. Up to 3 entries:
        // a wrong password is zeroed + dropped immediately so the next
        // attempt re-prompts instead of reusing a known-bad secret.
        const int maxPasswordAttempts = 3;
        var passwordAttempts = 0;
        if (_executor.NeedsPassword(request))
        {
            if (!CanAskHuman)
                return $"ERROR: elevation to '{targetUser}' needs a sudo password and none is in memory. " +
                    "Restart interactively to provide it, or set OPENMONO_HOST_PASSWORD_FILE.";
            _ui.Info($"[host] elevation to '{targetUser}' needs the sudo password.");
            var first = await _ui.PasswordAsync($"Sudo password for '{targetUser}' (type it — nothing is logged): ", ct);
            if (first.Length == 0)
            {
                Array.Clear(first);
                _identity.ForgetPassword();
                return $"ERROR: sudo password entry cancelled for '{targetUser}': {request.Command}";
            }
            _identity.SetPassword(first);
            passwordAttempts = 1;
        }

        // Pre-exec hint only (never blocking): warn about TTY-direct secrets
        // up front so the operator is not surprised by the guidance error.
        switch (HostExecInteraction.Classify(request.Command))
        {
            case InteractiveHint.StdinSecret:
            case InteractiveHint.StdinConfirm:
                _ui.Info("[host] this command may ask for input — you'll be prompted if it does.");
                break;
            case InteractiveHint.TtySecret:
                _ui.Info("[host] note: ssh-style password prompts need a TTY — prefer key auth.");
                break;
            default:
                break;
        }

        _ui.HostStart(request.Command);
        var output = await _executor.ExecuteAsync(request, _config.TimeoutMs, ct, this);

        // Wrong-password retry: wipe the bad secret, re-prompt in the same
        // surface (TUI chat lane or line-mode masked prompt), re-run.
        // Non-interactive and background runs fail fast — no one to ask.
        var canReprompt = CanAskHuman && !request.Background
            && _executor.RequiresElevation(request) && _identity.Sudo != SudoPolicy.Deny;
        while (HostExecutor.IsPasswordFailure(output) && canReprompt && passwordAttempts < maxPasswordAttempts)
        {
            _identity.ForgetPassword();
            HostExecutor.NoteAuthenticationFailure(targetUser);
            _ui.Error($"[host] sudo authentication failed for '{targetUser}' (attempt {passwordAttempts}/{maxPasswordAttempts}). Try again — the old password was wiped from memory.");
            char[] retry;
            try
            {
                retry = await _ui.PasswordAsync($"Sudo password for '{targetUser}' (attempt {passwordAttempts + 1}/{maxPasswordAttempts}, nothing is logged): ", ct);
            }
            catch (OperationCanceledException)
            {
                return $"[host-exec on {_workDir}]\n$ {request.Command}\nERROR: sudo password entry cancelled for '{targetUser}'.";
            }
            if (retry.Length == 0)
            {
                Array.Clear(retry);
                _identity.ForgetPassword();
                return $"[host-exec on {_workDir}]\n$ {request.Command}\nERROR: sudo password entry cancelled for '{targetUser}'.";
            }
            _identity.SetPassword(retry);
            passwordAttempts++;
            _ui.HostStart(request.Command);
            output = await _executor.ExecuteAsync(request, _config.TimeoutMs, ct, this);
        }
        if (HostExecutor.IsPasswordFailure(output) && passwordAttempts >= maxPasswordAttempts)
        {
            _identity.ForgetPassword();
            output = $"ERROR: sudo authentication failed for '{targetUser}' after {maxPasswordAttempts} attempts. The wrong password was wiped from memory — re-run the command to try again.";
        }

        _ui.HostDone(request.Command, output);
        return $"[host-exec on {_workDir}]\n$ {request.Command}\n{output}";
    }

    // Answer for a malformed envelope: audit it and return the re-send shapes,
    // so the turn resumes instead of stalling on PENDING_RESPONSE.
    public string HandleMalformedEnvelope(string question)
    {
        var answer = MalformedEnvelopeAnswer(question);
        AuditLog.WriteCommand(_identity.RunAsUser, false, Truncate(question, 500),
            -1, 0, false, "HOST_EXEC envelope unparseable; auto-replied, operator not asked");
        _ui.Info("[bridge] HOST_EXEC envelope unparseable — asked the agent to re-send it.");
        return answer;
    }

    // IHostExecPrompter: commands that block on stdin ask the operator through
    // the existing UI. Answers are per-command, never session grants, and
    // secrets are never logged (only the prompt excerpt + channel are audited).
    bool IHostExecPrompter.CanPrompt => CanAskHuman;

    async Task<char[]?> IHostExecPrompter.AskSecretAsync(string promptExcerpt, CancellationToken ct)
    {
        AuditPrompt(promptExcerpt, "masked");
        try
        {
            return await _ui.PasswordAsync($"[host] input needed ({promptExcerpt}): ", ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    async Task<string?> IHostExecPrompter.AskTextAsync(string promptExcerpt, CancellationToken ct)
    {
        AuditPrompt(promptExcerpt, "echoed");
        try
        {
            return await _ui.InputAsync($"[host] input needed ({promptExcerpt}) ", ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    async Task<bool?> IHostExecPrompter.AskConfirmAsync(string promptExcerpt, CancellationToken ct)
    {
        AuditPrompt(promptExcerpt, "confirm");
        try
        {
            return await _ui.ConfirmAsync($"[host] {promptExcerpt} ", ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void AuditPrompt(string excerpt, string channel) =>
        AuditLog.WriteCommand(_identity.RunAsUser, false,
            excerpt.Length > 200 ? excerpt[..200] + "..." : excerpt,
            -1, 0, false, $"host stdin prompt shown ({channel}); value never logged");

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max] + "...";
}
