// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;

namespace Omabot.Web;

public sealed record WebAccount(string Name, string Role, string State, DateTimeOffset? LockedUntil, int Lockouts, string CreatedAt, string? LastLoginAt, string? Home = null)
{
    public string UserId => WebAccounts.UserId(Name);
}

/// <summary>Status 200 on success, 401 for a refused sign-in, 429 while the address is locked out.</summary>
public sealed record LoginOutcome(bool Ok, WebSession? Session, int Status, string Message);

/// <summary>
/// Web accounts for web-single (plan 7.19, 7.20 items 2 to 4): a strong
/// password plus a time-based code, roles owner, operator and viewer.
/// Accounts start pending and only sign in after the code is confirmed on
/// the host. Failed sign-ins lock the account after
/// <c>web.max_failed_logins</c> tries and the address after
/// <c>web.address_failures</c>, each lockout twice as long as the one before
/// (at most a day), and every failure is audited. Every refusal reads the
/// same, so the page never tells which part was wrong or whether a name exists.
/// </summary>
public sealed partial class WebAccounts(UsersDb db, IClock clock, WebAuth auth, RosterConfig config, AuditLog? audit = null, int iterations = Passwords.DefaultIterations)
{
    public static readonly string[] Roles = ["owner", "operator", "viewer"];
    public const string Refused = "Sign-in failed. Check the name, password and code. After several failed tries, wait before trying again.";
    public const string AddressLocked = "Too many failed sign-ins from this address. Try again later.";
    private static readonly TimeSpan MaxLockout = TimeSpan.FromHours(24);
    private readonly Lazy<string> _dummy = new(() => iterations == Passwords.DefaultIterations ? Passwords.DummyHash : Passwords.Hash(WebSecrets.NewSecret(), iterations));

    [GeneratedRegex("^[a-z][a-z0-9._-]{1,31}$")]
    private static partial Regex NamePattern();

    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static string UserId(string name) => "web:" + name;

    private string Now => Iso(clock.UtcNow);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public IReadOnlyList<WebAccount> List() => db.Query(
        "SELECT name, role, state, locked_until, lockouts, created_at, last_login_at, home FROM accounts ORDER BY name;", Map);

    public WebAccount? Find(string name) => db.Query(
        "SELECT name, role, state, locked_until, lockouts, created_at, last_login_at, home FROM accounts WHERE name=$n;", Map, ("$n", name)).FirstOrDefault();

    private static WebAccount Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : Parse(r.GetString(3)), r.GetInt32(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7));

    /// <summary>The web-multi user roster this account works in, read fresh on every request; null in web-single.</summary>
    public string? HomeOf(string name) => db.Scalar("SELECT home FROM accounts WHERE name=$n AND state='active';", ("$n", name)) as string;

    public int ActiveOwners() => Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM accounts WHERE role='owner' AND state='active';"), CultureInfo.InvariantCulture);

    /// <summary>Creates a pending account and returns the provisioning address for an authenticator app.</summary>
    public (bool Ok, string Message, string? ProvisioningUri) Add(string name, string role, string password, byte[] seedKey, string? home = null)
    {
        if (!ValidName(name)) return (false, "Account names are 2 to 32 lower case letters, digits, dots, dashes or underscores, starting with a letter.", null);
        if (!Roles.Contains(role)) return (false, "The role must be owner, operator or viewer.", null);
        if (Find(name) is not null) return (false, $"Account {name} already exists.", null);
        if (Passwords.Problem(password, name) is { } p) return (false, "Password refused: " + p, null);
        var seed = Totp.NewSeed();
        Redactor.Shared.AddKnownSecret(Totp.Base32(seed));
        db.Exec("INSERT INTO accounts(name, role, state, password_hash, totp_seed, created_at, updated_at, home) VALUES ($n,$r,'pending',$h,$s,$t,$t,$home);",
            ("$n", name), ("$r", role), ("$h", Passwords.Hash(password, iterations)), ("$s", Totp.Seal(seed, seedKey, name)), ("$t", Now), ("$home", home));
        audit?.Write("host", "web.account_added", name, $"role {role}{(home is null ? "" : ", roster " + home)}, pending until the code is confirmed");
        return (true, $"Account {name} ({role}) added. Confirm the code to activate it.", Totp.ProvisioningUri("oma-bot", name, seed));
    }

    /// <summary>Activates a pending account once a code from its authenticator checks out.</summary>
    public (bool Ok, string Message) Confirm(string name, string code, byte[] seedKey)
    {
        var row = Secrets(name);
        if (row is null) return (false, $"No account {name}.");
        if (row.Value.State != "pending") return (false, $"Account {name} is {row.Value.State}, not waiting for a code.");
        var seed = Totp.Open(row.Value.Seed, seedKey, name);
        if (seed is null) return (false, "The two-factor seed cannot be opened; the web admin secret changed. Remove and add the account again.");
        var step = Totp.Verify(seed, code, clock.UtcNow, row.Value.LastStep);
        if (step is null) return (false, "That code does not match. Check the authenticator's clock and try the next code.");
        db.Exec("UPDATE accounts SET state='active', totp_last_step=$s, updated_at=$t WHERE name=$n;", ("$s", step.Value), ("$t", Now), ("$n", name));
        audit?.Write("host", "web.account_confirmed", name, "second factor confirmed");
        return (true, $"Account {name} is active.");
    }

    public (bool Ok, string Message) SetPassword(string name, string password)
    {
        if (Find(name) is null) return (false, $"No account {name}.");
        if (Passwords.Problem(password, name) is { } p) return (false, "Password refused: " + p);
        db.Exec("UPDATE accounts SET password_hash=$h, failed=0, updated_at=$t WHERE name=$n;", ("$h", Passwords.Hash(password, iterations)), ("$t", Now), ("$n", name));
        auth.RevokeUser(UserId(name));
        audit?.Write("host", "web.password_set", name, "password replaced; sessions ended");
        return (true, $"Password for {name} replaced. Its sessions were ended.");
    }

    public (bool Ok, string Message) Disable(string name)
    {
        var a = Find(name);
        if (a is null) return (false, $"No account {name}.");
        if (a.Role == "owner" && a.State == "active" && ActiveOwners() <= 1) return (false, "Refused: that is the last active owner.");
        db.Exec("UPDATE accounts SET state='disabled', updated_at=$t WHERE name=$n;", ("$t", Now), ("$n", name));
        auth.RevokeUser(UserId(name));
        audit?.Write("host", "web.account_disabled", name, "sessions ended");
        return (true, $"Account {name} disabled. Its sessions were ended.");
    }

    public (bool Ok, string Message) Enable(string name)
    {
        var a = Find(name);
        if (a is null) return (false, $"No account {name}.");
        if (a.State != "disabled") return (false, $"Account {name} is {a.State}.");
        db.Exec("UPDATE accounts SET state='active', failed=0, locked_until=NULL, lockouts=0, updated_at=$t WHERE name=$n;", ("$t", Now), ("$n", name));
        audit?.Write("host", "web.account_enabled", name, "");
        return (true, $"Account {name} enabled.");
    }

    public (bool Ok, string Message) SetRole(string name, string role)
    {
        var a = Find(name);
        if (a is null) return (false, $"No account {name}.");
        if (!Roles.Contains(role)) return (false, "The role must be owner, operator or viewer.");
        if (a.Role == "owner" && role != "owner" && a.State == "active" && ActiveOwners() <= 1) return (false, "Refused: that is the last active owner.");
        db.Exec("UPDATE accounts SET role=$r, updated_at=$t WHERE name=$n;", ("$r", role), ("$t", Now), ("$n", name));
        auth.RevokeUser(UserId(name));
        audit?.Write("host", "web.role_set", name, $"{a.Role} to {role}; sessions ended");
        return (true, $"Account {name} is now {role}. Its sessions were ended.");
    }

    /// <summary>Signs the account out everywhere.</summary>
    public int Logout(string name)
    {
        var n = auth.RevokeUser(UserId(name));
        audit?.Write("host", "web.logout_everywhere", name, $"{n} session(s) ended");
        return n;
    }

    /// <summary>Re-seals every seed under a new key (admin secret rotation). Returns how many could not be opened.</summary>
    public int Reseal(byte[] oldKey, byte[] newKey)
    {
        var broken = 0;
        foreach (var (name, sealedSeed) in db.Query("SELECT name, totp_seed FROM accounts;", r => (r.GetString(0), r.GetString(1))))
        {
            var seed = Totp.Open(sealedSeed, oldKey, name);
            if (seed is null) { broken++; continue; }
            db.Exec("UPDATE accounts SET totp_seed=$s, updated_at=$t WHERE name=$n;", ("$s", Totp.Seal(seed, newKey, name)), ("$t", Now), ("$n", name));
        }
        return broken;
    }

    private (string State, string Role, string Hash, string Seed, long LastStep, int Failed, DateTimeOffset? LockedUntil, int Lockouts)? Secrets(string name)
    {
        var rows = db.Query("SELECT state, role, password_hash, totp_seed, totp_last_step, failed, locked_until, lockouts FROM accounts WHERE name=$n;",
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt32(5), r.IsDBNull(6) ? (DateTimeOffset?)null : Parse(r.GetString(6)), r.GetInt32(7)), ("$n", name));
        return rows.Count == 0 ? null : rows[0];
    }

    public bool AddressLockedOut(string ip)
    {
        var until = db.Scalar("SELECT locked_until FROM address_locks WHERE ip=$i;", ("$i", ip)) as string;
        return until is not null && Parse(until) > clock.UtcNow;
    }

    /// <summary>One sign-in attempt. The password is always checked, against a dummy hash when the name is unknown.</summary>
    public LoginOutcome Login(string? name, string? password, string? code, string ip, byte[] seedKey)
    {
        var shown = ValidName(name) ? name! : "(invalid name)";
        if (AddressLockedOut(ip))
        {
            audit?.Write("web", "web.login_refused", shown, $"from {ip}: address locked out");
            return new(false, null, 429, AddressLocked);
        }
        var row = ValidName(name) ? Secrets(name!) : null;
        var passwordOk = Passwords.Verify(password ?? "", row?.Hash ?? _dummy.Value) && row is not null;
        string? why = row is null ? "unknown account"
            : row.Value.LockedUntil is { } lu && lu > clock.UtcNow ? "account locked out"
            : row.Value.State != "active" ? "account " + row.Value.State
            : !passwordOk ? "wrong password"
            : null;
        long? step = null;
        if (why is null)
        {
            var seed = Totp.Open(row!.Value.Seed, seedKey, name!);
            step = seed is null ? null : Totp.Verify(seed, code, clock.UtcNow, row.Value.LastStep);
            if (seed is null) why = "two-factor seed cannot be opened";
            else if (step is null) why = "wrong or reused code";
        }
        if (why is not null)
        {
            Fail(row is null ? null : name, shown, ip, why, row?.Failed ?? 0, row?.Lockouts ?? 0, row?.LockedUntil);
            return new(false, null, 401, Refused);
        }
        db.Exec("UPDATE accounts SET failed=0, lockouts=0, locked_until=NULL, totp_last_step=$s, last_login_at=$t WHERE name=$n;", ("$s", step!.Value), ("$t", Now), ("$n", name));
        var session = auth.Open(UserId(name!), name!, row!.Value.Role, ip);
        audit?.Write("web:" + name, "web.login", UserId(name!), $"from {ip}");
        return new(true, session, 200, "Signed in.");
    }

    private void Fail(string? account, string shown, string ip, string why, int failed, int lockouts, DateTimeOffset? lockedUntil)
    {
        var now = clock.UtcNow;
        db.Exec("INSERT INTO login_failures(name, ip, at) VALUES ($n,$i,$t);", ("$n", shown), ("$i", ip), ("$t", Iso(now)));
        var detail = $"from {ip}: {why}";
        if (account is not null && why != "account locked out")
        {
            failed++;
            if (failed >= config.Web.MaxFailedLogins)
            {
                var until = now + Backoff(lockouts);
                db.Exec("UPDATE accounts SET failed=0, lockouts=lockouts+1, locked_until=$u WHERE name=$n;", ("$u", Iso(until)), ("$n", account));
                detail += $"; account locked until {Iso(until)}";
            }
            else db.Exec("UPDATE accounts SET failed=$f WHERE name=$n;", ("$f", failed), ("$n", account));
        }
        var since = Iso(now - TimeSpan.FromMinutes(config.Web.AddressWindowMinutes));
        var recent = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM login_failures WHERE ip=$i AND at > $s;", ("$i", ip), ("$s", since)), CultureInfo.InvariantCulture);
        if (recent >= config.Web.AddressFailures)
        {
            var prior = Convert.ToInt32(db.Scalar("SELECT COALESCE(MAX(lockouts), 0) FROM address_locks WHERE ip=$i;", ("$i", ip)), CultureInfo.InvariantCulture);
            var until = now + Backoff(prior);
            db.Exec("""
                INSERT INTO address_locks(ip, locked_until, lockouts) VALUES ($i,$u,1)
                ON CONFLICT(ip) DO UPDATE SET locked_until=$u, lockouts=lockouts+1;
                """, ("$i", ip), ("$u", Iso(until)));
            db.Exec("DELETE FROM login_failures WHERE ip=$i;", ("$i", ip));
            detail += $"; address locked until {Iso(until)}";
        }
        audit?.Write("web", "web.login_failed", shown, detail);
    }

    private TimeSpan Backoff(int lockouts)
    {
        var minutes = config.Web.LockoutMinutes * Math.Pow(2, Math.Min(lockouts, 10));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxLockout.TotalMinutes));
    }

    /// <summary>Forgets failures older than a day.</summary>
    public int Purge() => db.Exec("DELETE FROM login_failures WHERE at <= $t;", ("$t", Iso(clock.UtcNow - TimeSpan.FromDays(1))))
        + db.Exec("DELETE FROM address_locks WHERE locked_until <= $t;", ("$t", Iso(clock.UtcNow - TimeSpan.FromDays(1))));
}
