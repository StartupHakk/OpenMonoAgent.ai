// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using Omabot.Common;

namespace Omabot.Web;

/// <summary>A signed-in web user. Id is the raw session id (only in the cookie); the store keeps its hash.</summary>
public sealed record WebSession(string Id, string UserId, string Name, string Role, string Csrf, DateTimeOffset ExpiresAt)
{
    public bool CanAct => Role is "owner" or "operator";
}

/// <summary>
/// Web sessions and one-time login links. A link is single use, expires in
/// minutes and only its hash is stored; a session id is 256 random bits in an
/// HttpOnly, SameSite=Strict cookie, with its own CSRF token for every change.
/// Sessions end at their absolute expiry, and in web modes also after
/// <paramref name="idle"/> without a request.
/// </summary>
public sealed class WebAuth(UsersDb db, IClock clock, TimeSpan sessionTtl, TimeSpan? idle = null)
{
    public const string Cookie = "omabot_session";

    public static string NewSecret(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>A one-time login link code (for `oma-bot web open` on this computer).</summary>
    public string CreateLoginCode(string userId, string name, string role, TimeSpan ttl)
    {
        var code = NewSecret("olc_");
        db.Exec("INSERT INTO login_codes(code_hash, user_id, name, role, expires_at) VALUES ($h,$u,$n,$r,$e);",
            ("$h", SecureFile.Sha256Hex(code)), ("$u", userId), ("$n", name), ("$r", role), ("$e", Iso(clock.UtcNow + ttl)));
        Redactor.Shared.AddKnownSecret(code);
        return code;
    }

    /// <summary>Spends a login code once and opens a session. Null when the code is unknown, used or expired.</summary>
    public WebSession? Redeem(string code, string? ip)
    {
        if (code.Length is < 20 or > 200) return null;
        var hash = SecureFile.Sha256Hex(code);
        var row = db.Query("SELECT user_id, name, role, expires_at FROM login_codes WHERE code_hash=$h AND used_at IS NULL;",
            r => (User: r.GetString(0), Name: r.GetString(1), Role: r.GetString(2), Expires: Parse(r.GetString(3))), ("$h", hash)).FirstOrDefault();
        if (row.User is null || row.Expires <= clock.UtcNow) return null;
        if (db.Exec("UPDATE login_codes SET used_at=$t WHERE code_hash=$h AND used_at IS NULL;", ("$t", Iso(clock.UtcNow)), ("$h", hash)) != 1) return null;
        return Open(row.User, row.Name, row.Role, ip);
    }

    public WebSession Open(string userId, string name, string role, string? ip)
    {
        var id = NewSecret("");
        var csrf = NewSecret("");
        var expires = clock.UtcNow + sessionTtl;
        db.Exec("INSERT INTO sessions(id_hash, user_id, name, role, csrf, created_at, expires_at, last_seen_at, ip) VALUES ($h,$u,$n,$r,$c,$t,$e,$t,$ip);",
            ("$h", SecureFile.Sha256Hex(id)), ("$u", userId), ("$n", name), ("$r", role), ("$c", csrf), ("$t", Iso(clock.UtcNow)), ("$e", Iso(expires)), ("$ip", ip));
        Redactor.Shared.AddKnownSecret(id);
        return new WebSession(id, userId, name, role, csrf, expires);
    }

    public WebSession? Validate(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 64) return null;
        var hash = SecureFile.Sha256Hex(id);
        var row = db.Query("SELECT user_id, name, role, csrf, expires_at, last_seen_at FROM sessions WHERE id_hash=$h;",
            r => (Session: new WebSession(id, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4))), Seen: Parse(r.GetString(5))), ("$h", hash)).FirstOrDefault();
        var s = row.Session;
        if (s is null) return null;
        if (s.ExpiresAt <= clock.UtcNow || (idle is { } i && row.Seen + i <= clock.UtcNow))
        {
            db.Exec("DELETE FROM sessions WHERE id_hash=$h;", ("$h", hash));
            return null;
        }
        db.Exec("UPDATE sessions SET last_seen_at=$t WHERE id_hash=$h;", ("$t", Iso(clock.UtcNow)), ("$h", hash));
        return s;
    }

    public void Revoke(string id) => db.Exec("DELETE FROM sessions WHERE id_hash=$h;", ("$h", SecureFile.Sha256Hex(id)));

    public int RevokeUser(string userId) => db.Exec("DELETE FROM sessions WHERE user_id=$u;", ("$u", userId));

    public int RevokeAll() => db.Exec("DELETE FROM sessions;");

    public int Purge() => db.Exec("DELETE FROM sessions WHERE expires_at <= $t;", ("$t", Iso(clock.UtcNow))) + db.Exec("DELETE FROM login_codes WHERE expires_at <= $t;", ("$t", Iso(clock.UtcNow)));
}
