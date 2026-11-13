namespace OpenMono.Session;

/// <summary>
/// The sudo elevation attempt loop, shared by every surface that collects a
/// password in-place (TUI elevation card flow; the ACP surface drives the
/// same gateway calls from its permission-resume branch). Runs entirely
/// inside one tool call / one pause, so the conversation loop NEVER resumes
/// between attempts — it resumes only with the terminal outcome.
///
/// Semantics (agreed contract):
///  - an allow click on the elevated card opens the password prompt;
///  - every submit — INCLUDING empty — is verified against the daemon
///    (/sudo/verify → `sudo -v`, no payload runs) and counts as an attempt;
///  - at most <see cref="MaxAttempts"/> total attempts; each failure
///    re-prompts in place with an "attempt N/3" banner;
///  - a cancel (Esc / dismissed modal) ends the flow immediately;
///  - success hands back the VERIFIED password for the single following
///    real exec — one-shot, never cached, the char copy is wiped here.
/// </summary>
public sealed record SudoAttemptFlow(bool Verified, string? Password, string? ModelMessage)
{
    public const int MaxAttempts = 3;

    /// <summary>
    /// Drive the attempt loop. <paramref name="askSecret"/> prompts the user
    /// in place and returns their typed chars, an empty array for an empty
    /// submit, or null when they cancelled; null <paramref name="askSecret"/>
    /// means the surface has no password channel at all. Terminal failures
    /// carry a <see cref="ModelMessage"/> — the honest text the model loop
    /// receives so it knows WHY the command never ran and must not retry.
    /// </summary>
    public static async Task<SudoAttemptFlow> RunAsync(
        Func<string, CancellationToken, Task<char[]?>>? askSecret,
        HostGateway gateway,
        CancellationToken ct)
    {
        if (askSecret is null)
            return new(false, null,
                "Sudo elevation needs an interactive password prompt and this session has none — " +
                "the command did NOT run. Do NOT retry. Ask the user to run it themselves, or to set " +
                "host.sudo_policy allow for unattended host mode.");

        var banner = "";
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var chars = await askSecret($"sudo password — attempt {attempt}/{MaxAttempts}{banner}", ct);
            if (chars is null)
                return new(false, null,
                    "The user cancelled the sudo elevation at the password prompt (no password was submitted). " +
                    "The command did NOT run. Do NOT retry this call — ask the user how to proceed, or re-send " +
                    "without \"as_root\": true.");

            var password = new string(chars);
            try
            {
                // Empty submits go to the daemon too: it short-circuits them
                // to a password_failure without burning a real sudo attempt,
                // and the user still sees "attempt N/3 — previous was empty".
                var verdict = await gateway.VerifySudoPasswordAsync(asRoot: true, password, ct);
                if (verdict.Ok)
                    return new(true, password, null);

                if (verdict.Error == "password_failure")
                {
                    banner = password.Length == 0
                        ? " — previous attempt was empty"
                        : " — previous password was rejected";
                    continue;
                }

                // Terminal for the attempt loop: unpaired, daemon unreachable,
                // sudo denied by policy, or sudo itself unusable. Re-typing
                // cannot fix any of these.
                return new(false, null,
                    $"Sudo elevation is unavailable on this host: {verdict.Output} " +
                    "The command did NOT run. Do NOT retry — tell the user, or re-send without \"as_root\": true.");
            }
            finally
            {
                Array.Clear(chars, 0, chars.Length);
            }
        }

        return new(false, null,
            $"Sudo authentication failed after {MaxAttempts} attempts — the command did NOT run and any " +
            "password material was wiped. Do NOT retry this call. Ask the user to check their sudo setup, " +
            "or re-send without \"as_root\": true.");
    }
}
