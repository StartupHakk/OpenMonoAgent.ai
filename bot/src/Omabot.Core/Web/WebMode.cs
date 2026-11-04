// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using Omabot.Common;
using Omabot.Config;

namespace Omabot.Web;

/// <summary>
/// Hosting modes for the web view (plan 7.19, 7.20 item 1). Web mode never
/// starts without HTTPS, a login method that is built, an admin secret and
/// an active owner: <see cref="Problems"/> lists what is missing, and
/// <see cref="FrontProblemAsync"/> proves the public HTTPS address has a valid
/// certificate and reaches this roster.
/// </summary>
public static class WebMode
{
    public const string SecureCookie = "__Host-omabot_session";
    public const string HealthPath = "/healthz";

    public static WebOptions Options(RosterConfig c)
    {
        if (c.Mode == "local") return WebServer.Local(c);
        var origin = new Uri(c.Web.PublicOrigin!);
        var host = origin.IsDefaultPort ? origin.IdnHost : $"{origin.IdnHost}:{origin.Port.ToString(CultureInfo.InvariantCulture)}";
        return new WebOptions(c.Mode, [host], [origin.GetLeftPart(UriPartial.Authority)], SecureCookie: true, Cookie: SecureCookie, BehindFront: true);
    }

    /// <summary>Why the public origin is not usable, or null. Only https and a DNS name the certificate covers.</summary>
    public static string? OriginProblem(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return "web.public_origin is not set (for example https://roster.example.com).";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return "web.public_origin must be an https address.";
        if (u.UserInfo.Length > 0 || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0 || origin.TrimEnd('/') != u.GetLeftPart(UriPartial.Authority))
            return "web.public_origin is only the scheme and host, like https://roster.example.com.";
        if (u.HostNameType != UriHostNameType.Dns || !u.Host.Contains('.') || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "web.public_origin must use a DNS name its certificate covers, not an address or localhost.";
        return null;
    }

    /// <summary>What stops web mode from starting. Empty in local mode and when everything is in place.</summary>
    public static IReadOnlyList<string> Problems(RosterConfig c, RosterPaths p)
    {
        var list = new List<string>();
        if (c.Mode == "local") return list;
        if (c.Mode == "web-multi" && SignoffProblem(p) is { } signoff) list.Add(signoff);
        if (OriginProblem(c.Web.PublicOrigin) is { } o) list.Add(o);
        if (!c.Web.BehindHttpsFront) list.Add("web.behind_https_front must be true: the HTTPS front (docker/web-front) terminates TLS and forwards to 127.0.0.1.");
        if (c.Web.TlsCertFile is not null || c.Web.TlsKeyFile is not null) list.Add("The roster does not terminate TLS itself. Leave web.tls_cert_file and web.tls_key_file empty and use the HTTPS front.");
        if (!(IPAddress.TryParse(c.Api.BindAddress, out var ip) && IPAddress.IsLoopback(ip))) list.Add("api.bind_address must be a loopback address; only the HTTPS front reaches it.");
        if (c.Web.LoginMethod != "password_totp") list.Add("web.login_method passkey is not built yet. Use password_totp (a strong password plus a time-based code).");
        if (WebSecrets.Problem(WebSecrets.PathOf(c, p)) is { } s) list.Add(s);
        if (!File.Exists(p.UsersDb)) list.Add("No web accounts. Add an owner: oma-bot users add NAME --role owner, then oma-bot users confirm NAME.");
        else
        {
            if (!SecureFile.IsPrivate(p.Web)) list.Add($"{p.Web} is readable by others. Run chmod 700 {p.Web}.");
            if (!SecureFile.IsPrivate(p.UsersDb)) list.Add($"{p.UsersDb} is readable by others. Run chmod 600 {p.UsersDb}.");
            var owners = Convert.ToInt32(new UsersDb(p.UsersDb).Scalar("SELECT COUNT(*) FROM accounts WHERE role='owner' AND state='active';"), CultureInfo.InvariantCulture);
            if (owners == 0) list.Add("No active owner account. Add one with oma-bot users add NAME --role owner and confirm its code with oma-bot users confirm NAME.");
        }
        return list;
    }

    /// <summary>
    /// web-multi starts only after the security release gate passed and an outside review was done (plan 7.20 item 11,
    /// 11.8): a person records both in web/release-signoff.md.
    /// </summary>
    public static string? SignoffProblem(RosterPaths p)
    {
        var file = Path.Combine(p.Web, "release-signoff.md");
        const string how = "web-multi needs the security release gate and an outside review first; record them in web/release-signoff.md (docs/multi-user.md).";
        if (!File.Exists(file) || new FileInfo(file).LinkTarget is not null) return how;
        if (!SecureFile.IsPrivate(file)) return $"{file} is readable by others. Run chmod 600 {file}.";
        var fields = File.ReadAllLines(file).Select(l => l.Split(':', 2)).Where(x => x.Length == 2)
            .GroupBy(x => x[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.Ordinal);
        string Field(string k) => fields.TryGetValue(k, out var v) ? v : "";
        if (Field("security_gate") != "passed" || Field("outside_review") != "done" || Field("signed_by").Length == 0
            || !DateOnly.TryParseExact(Field("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return how + " It needs security_gate: passed, outside_review: done, signed_by: NAME and date: YYYY-MM-DD.";
        return null;
    }

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Accepts the service's own check value, or the one `oma-bot web enable`
    /// wrote to web/check.nonce in the last ten minutes.
    /// </summary>
    public static Func<string, bool> NonceChecker(RosterPaths p, string own) => n =>
    {
        if (n.Length != 32) return false;
        if (SecureFile.FixedTimeEquals(n, own)) return true;
        var f = new FileInfo(p.WebCheckNonce);
        // File times are system times, so the age is measured on the system clock.
        if (!f.Exists || f.LinkTarget is not null || DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromMinutes(10)) return false;
        return SecureFile.FixedTimeEquals(n, File.ReadAllText(f.FullName).Trim());
    };

    /// <summary>
    /// Asks the public HTTPS address for this roster's check value. Null when
    /// the certificate is valid (system trust unless a handler says otherwise),
    /// the front forwards to this roster and it marks the request as https.
    /// </summary>
    public static async Task<string?> FrontProblemAsync(RosterConfig c, string nonce, HttpMessageHandler? handler, CancellationToken ct)
    {
        if (OriginProblem(c.Web.PublicOrigin) is { } o) return o;
        var own = handler is null;
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), UseProxy = false };
        using var http = new HttpClient(handler, disposeHandler: own) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var res = await http.GetAsync(new Uri(new Uri(c.Web.PublicOrigin!), $"{HealthPath}?n={nonce}"), ct).ConfigureAwait(false);
            if (res.StatusCode != HttpStatusCode.OK) return $"The HTTPS front answered {(int)res.StatusCode} to the roster check, so it does not forward to this roster.";
            var body = (await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            return body switch
            {
                "ok oma-bot https" => null,
                "ok oma-bot http" => "The HTTPS front does not send X-Forwarded-Proto: https.",
                _ => "The answer to the roster check did not come from this roster.",
            };
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException ae)
        {
            return "The HTTPS front's certificate is not valid: " + ae.Message;
        }
        catch (HttpRequestException ex)
        {
            return "The HTTPS front cannot be reached: " + ex.Message;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The HTTPS front did not answer in time.";
        }
    }
}
