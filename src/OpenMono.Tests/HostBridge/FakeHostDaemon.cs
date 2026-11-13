using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenMono.Tests.HostBridge;

/// <summary>
/// Canned host-bridge daemon for tests that must not touch real sudo: answers
/// /ping, /pair, /status, /keepalive, /revoke and /exec. /exec is keyed on the
/// request body — as_root without a password ⇒ the daemon's fail-closed policy
/// denial, wrong password ⇒ password_failure, correct password ⇒ success
/// output. Every exec body is recorded (ExecPasswords) so tests can assert
/// exactly what crossed the wire. Real sudo+password is not deterministically
/// testable in CI; this stands in for the daemon half of the contract.
/// </summary>
internal sealed class FakeHostDaemon : IDisposable
{
    private readonly HttpListener _listener;

    public string Secret { get; } = "fake-daemon-secret-0f1e";
    public string BaseUrl { get; }
    public int ExecCount { get; private set; }
    public string? LastExecPassword { get; private set; }
    public List<string?> ExecPasswords { get; } = new();
    public int VerifyCount { get; private set; }
    public List<string?> VerifyPasswords { get; } = new();
    public string SudoPolicy { get; }

    private readonly string _correctPassword;

    public FakeHostDaemon(string correctPassword, string sudoPolicy = "ask")
    {
        _correctPassword = correctPassword;
        SudoPolicy = sudoPolicy;
        var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;
        BaseUrl = $"http://127.0.0.1:{port}";
        _ = Task.Run(ServeAsync);
    }

    public static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            try { Handle(ctx); } catch { /* connection dropped — keep serving */ }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        switch (path)
        {
            case "/ping":
                Respond(ctx, new { ok = true, daemon = true, version = "test-fake" });
                return;
            case "/status":
                Respond(ctx, new { ok = true, daemon = true, port = 0, version = "test-fake", paired_sessions = 1, idle_timeout_seconds = -1 });
                return;
            case "/pair":
                Respond(ctx, new { ok = true, token = "tok_fake", run_as = "test-user", sudo = SudoPolicy, log_dir = "/tmp" });
                return;
            case "/keepalive":
            case "/revoke":
                Respond(ctx, new { ok = true });
                return;
            case "/exec":
                HandleExec(ctx);
                return;
            case "/sudo/verify":
                HandleSudoVerify(ctx);
                return;
            default:
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
        }
    }

    private void HandleExec(HttpListenerContext ctx)
    {
        var raw = new StreamReader(ctx.Request.InputStream).ReadToEnd();
        using var doc = JsonDocument.Parse(raw);
        var req = doc.RootElement;
        ExecCount++;

        // The wire-contract assertion surface: the password field is exactly
        // what the gateway put on the request (or absent).
        var password = req.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        LastExecPassword = password;
        ExecPasswords.Add(password);

        var r = req.GetProperty("request");
        var asRoot = r.TryGetProperty("as_root", out var a) && a.ValueKind == JsonValueKind.True;
        if (asRoot)
        {
            if (string.IsNullOrEmpty(password))
            {
                Respond(ctx, new
                {
                    ok = false,
                    output = "Host execution denied by host-bridge policy (sudo needs a per-exec password from the client consent card, or allow_sudo=allow for unattended host mode).",
                    denied = true,
                });
            }
            else if (password != _correctPassword)
            {
                Respond(ctx, new
                {
                    ok = false,
                    output = "sudo authentication failed — the password was wiped; the user may retry.",
                    denied = true,
                    error = "password_failure",
                });
            }
            else
            {
                Respond(ctx, new { ok = true, output = "root-output\n$ echo as-root-ok\nok" });
            }
            return;
        }

        var command = r.GetProperty("command").GetString();
        Respond(ctx, new { ok = true, output = command });
    }

    /// <summary>
    /// Mirrors the real daemon's /sudo/verify code matrix: unpaired token ⇒
    /// unpaired_or_bad_token (terminal), sudo=deny ⇒ sudo_denied (terminal),
    /// empty password ⇒ password_failure without burning a sudo attempt,
    /// wrong password ⇒ password_failure (the client's attempt loop owns the
    /// count), correct password ⇒ ok. Every submitted password is recorded
    /// (VerifyPasswords) so tests can assert exactly what crossed the wire.
    /// </summary>
    private void HandleSudoVerify(HttpListenerContext ctx)
    {
        var raw = new StreamReader(ctx.Request.InputStream).ReadToEnd();
        using var doc = JsonDocument.Parse(raw);
        var req = doc.RootElement;
        VerifyCount++;

        var password = req.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        VerifyPasswords.Add(password);

        var token = req.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (token != "tok_fake")
        {
            Respond(ctx, new { ok = false, denied = true, error = "unpaired_or_bad_token", output = "no live session token" });
            return;
        }

        if (SudoPolicy == "deny")
        {
            Respond(ctx, new { ok = false, denied = true, error = "sudo_denied", output = "sudo is denied on this host (allow_sudo=deny)." });
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            Respond(ctx, new { ok = false, denied = true, error = "password_failure", output = "sudo authentication failed: empty password submitted (no sudo attempt was made)." });
            return;
        }

        if (password != _correctPassword)
        {
            Respond(ctx, new { ok = false, denied = true, error = "password_failure", output = "sudo authentication failed: sudo rejected the password." });
            return;
        }

        Respond(ctx, new { ok = true });
    }

    private static void Respond(HttpListenerContext ctx, object body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}
