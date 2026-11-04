// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Tests.Support;
using Omabot.Web;
using Xunit;

namespace Omabot.Tests;

public sealed class WebAccountTests
{
    private static CliEnv Env(TempRoster r, string? stdin = null) => new()
    {
        Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter(), ReadStdin = () => stdin,
    };

    [Fact(DisplayName = "TOTP: RFC 6238 test vector, one step of drift, base32 round trip, and sealed seeds bound to account and key")]
    public void TotpBasics()
    {
        var seed = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(seed, 59 / 30));
        Assert.Equal("081804", Totp.Code(seed, 1111111109 / 30));
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        Assert.Equal(1111111109 / 30, Totp.Verify(seed, "081804", now, 0));
        Assert.Equal(1111111109 / 30, Totp.Verify(seed, "081804", now.AddSeconds(30), 0));
        Assert.Null(Totp.Verify(seed, "081804", now.AddSeconds(90), 0));
        Assert.Null(Totp.Verify(seed, "081804", now, 1111111109 / 30));
        Assert.Null(Totp.Verify(seed, "08180x", now, 0));
        Assert.Equal(seed, Totp.FromBase32(Totp.Base32(seed)));
        Assert.StartsWith("otpauth://totp/oma-bot:spencer?secret=", Totp.ProvisioningUri("oma-bot", "spencer", seed));

        var key = WebSecrets.SeedKeyFrom(WebSecrets.NewSecret());
        var sealedSeed = Totp.Seal(seed, key, "spencer");
        Assert.DoesNotContain(Totp.Base32(seed), sealedSeed);
        Assert.Equal(seed, Totp.Open(sealedSeed, key, "spencer"));
        Assert.Null(Totp.Open(sealedSeed, key, "alex"));
        Assert.Null(Totp.Open(sealedSeed, WebSecrets.SeedKeyFrom(WebSecrets.NewSecret()), "spencer"));
        Assert.Null(Totp.Open("v1." + sealedSeed[3..^4] + "AAAA", key, "spencer"));
    }

    [Fact(DisplayName = "Passwords: PBKDF2 with its own salt and stored iterations, fixed-time check, and the strength rule")]
    public void PasswordRules()
    {
        var a = Passwords.Hash("Tall pines 7 quiet river", 1_000);
        var b = Passwords.Hash("Tall pines 7 quiet river", 1_000);
        Assert.NotEqual(a, b);
        Assert.StartsWith("pbkdf2-sha256$1000$", a);
        Assert.True(Passwords.Verify("Tall pines 7 quiet river", a));
        Assert.False(Passwords.Verify("Tall pines 7 quiet rivers", a));
        Assert.False(Passwords.Verify("x", "md5$1$abc$def"));
        Assert.False(Passwords.Verify(new string('x', 200), a));
        Assert.Equal(Passwords.DefaultIterations.ToString(System.Globalization.CultureInfo.InvariantCulture), Passwords.Hash("Tall pines 7 quiet river").Split('$')[1]);

        Assert.NotNull(Passwords.Problem("short1!", "spencer"));
        Assert.NotNull(Passwords.Problem("spencer-is-great-2026", "spencer"));
        Assert.NotNull(Passwords.Problem("password12345!", "alex"));
        Assert.NotNull(Passwords.Problem("abcabcabcabcabc", "alex"));
        Assert.NotNull(Passwords.Problem("aaaaaaaaaaaaaaaa", "alex"));
        Assert.Null(Passwords.Problem("Tall pines 7 quiet river", "spencer"));
    }

    [Fact(DisplayName = "users: add needs the admin secret and a strong password from stdin; the key goes to a 0600 file, never the screen; confirm activates; the last owner stays")]
    public async Task UsersCli()
    {
        using var r = new TempRoster();
        var noSecret = Env(r, "Tall pines 7 quiet river\n");
        Assert.Equal(Exit.Invalid, await new CliApp(noSecret).RunAsync(["users", "add", "spencer", "--role", "owner"]));
        Assert.Contains("web admin secret", noSecret.Err.ToString());
        var secretRun = Env(r);
        Assert.Equal(0, await new CliApp(secretRun).RunAsync(["web", "admin-secret"]));
        Assert.True(SecureFile.IsPrivate(r.Paths.WebAdminSecret));
        Assert.DoesNotContain(File.ReadAllText(r.Paths.WebAdminSecret).Trim(), secretRun.Out.ToString());
        var again = Env(r);
        Assert.Equal(0, await new CliApp(again).RunAsync(["web", "admin-secret"]));
        Assert.Contains("already exists", again.Out.ToString());

        Assert.Equal(Exit.Invalid, await new CliApp(Env(r)).RunAsync(["users", "add", "spencer", "--role", "owner"]));
        var weak = Env(r, "password12345!\n");
        Assert.Equal(1, await new CliApp(weak).RunAsync(["users", "add", "spencer", "--role", "owner"]));
        Assert.Contains("too common", weak.Err.ToString());
        Assert.Equal(1, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "Spencer!", "--role", "owner"]));
        Assert.Equal(1, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "spencer", "--role", "admin"]));

        var add = Env(r, "Tall pines 7 quiet river\n");
        Assert.Equal(0, await new CliApp(add).RunAsync(["users", "add", "spencer", "--role", "owner"]));
        var enroll = Path.Combine(r.Paths.WebEnroll, "spencer.txt");
        Assert.True(SecureFile.IsPrivate(enroll));
        var uri = File.ReadAllLines(enroll)[1];
        var seed = WebRig.SeedOf(uri);
        Assert.DoesNotContain("otpauth", add.Out.ToString());
        Assert.DoesNotContain(Totp.Base32(seed), add.Out.ToString());
        Assert.DoesNotContain("Tall pines", File.ReadAllText(r.Paths.UsersDb));
        Assert.DoesNotContain(Totp.Base32(seed), File.ReadAllText(r.Paths.UsersDb));

        var code = Totp.Code(seed, Totp.StepAt(r.Clock.UtcNow));
        var wrong = code == "111111" ? "222222" : "111111";
        Assert.Equal(1, await new CliApp(Env(r, wrong)).RunAsync(["users", "confirm", "spencer"]));
        Assert.True(File.Exists(enroll));
        Assert.Equal(0, await new CliApp(Env(r, code + "\n")).RunAsync(["users", "confirm", "spencer"]));
        Assert.False(File.Exists(enroll));

        var list = Env(r);
        Assert.Equal(0, await new CliApp(list).RunAsync(["users", "list"]));
        Assert.Contains("spencer", list.Out.ToString());
        Assert.Contains("active", list.Out.ToString());
        Assert.Equal(1, await new CliApp(Env(r)).RunAsync(["users", "role", "spencer", "viewer"]));
        Assert.Equal(1, await new CliApp(Env(r)).RunAsync(["users", "disable", "spencer"]));
        Assert.Equal(1, await new CliApp(Env(r, "short\n")).RunAsync(["users", "password", "spencer"]));
        Assert.Equal(0, await new CliApp(Env(r, "Another long pass 42 here\n")).RunAsync(["users", "password", "spencer"]));
        Assert.Equal(0, await new CliApp(Env(r)).RunAsync(["users", "logout", "spencer"]));
        var audit = string.Join("\n", r.Db().Query("SELECT action || ' ' || detail FROM audit WHERE action LIKE 'web.%';", x => x.GetString(0)));
        Assert.Contains("web.account_added", audit);
        Assert.Contains("web.account_confirmed", audit);
        Assert.DoesNotContain("Tall pines", audit);
    }

    [Fact(DisplayName = "web admin-secret --rotate re-seals every second factor under the new secret")]
    public async Task RotateAdminSecret()
    {
        await using var rig = await WebRig.StartAsync();
        var before = File.ReadAllText(rig.R.Paths.WebAdminSecret);
        var oldKey = rig.Key;
        var env = Env(rig.R);
        Assert.Equal(0, await new CliApp(env).RunAsync(["web", "admin-secret", "--rotate"]));
        Assert.NotEqual(before, File.ReadAllText(rig.R.Paths.WebAdminSecret));
        Assert.False(File.Exists(rig.R.Paths.WebAdminSecret + ".new"));
        var newKey = WebSecrets.SeedKey(rig.R.Paths.WebAdminSecret);
        var users = new UsersDb(rig.R.Paths.UsersDb);
        var sealedSeed = (string)users.Scalar("SELECT totp_seed FROM accounts WHERE name='spencer';")!;
        Assert.Null(Totp.Open(sealedSeed, oldKey, "spencer"));
        Assert.Equal(rig.Seeds["spencer"], Totp.Open(sealedSeed, newKey, "spencer"));
        rig.R.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(rig.Accounts.Login("spencer", WebRig.Password, rig.Code("spencer"), "127.0.0.1", newKey).Ok);
    }

    [Fact(DisplayName = "Web config: loopback API in every mode, ranges for sessions and lockouts, https source link; web status lists what web-single still needs")]
    public async Task WebConfig()
    {
        using var r = new TempRoster();
        void Bad(Action<RosterConfig> change)
        {
            var c = r.Config.Clone();
            change(c);
            Assert.Throws<InvalidDataException>(c.Validate);
        }
        Bad(c => { c.Mode = "web-single"; c.Api.BindAddress = "0.0.0.0"; });
        Bad(c => c.Web.IdleMinutes = 2);
        Bad(c => { c.Web.SessionHours = 1; c.Web.IdleMinutes = 90; });
        Bad(c => c.Web.MaxFailedLogins = 1);
        Bad(c => c.Web.AddressFailures = 1);
        Bad(c => c.Web.LoginMethod = "magic_link");
        Bad(c => c.Web.SourceUrl = "http://example.com/src");
        var options = WebMode.Options(new RosterConfig { Mode = "web-single", Web = { PublicOrigin = "https://roster.example.com:8443", BehindHttpsFront = true } });
        Assert.Equal(new[] { "roster.example.com:8443" }, options.Hosts);
        Assert.Equal(new[] { "https://roster.example.com:8443" }, options.Origins);
        Assert.Equal(WebMode.SecureCookie, options.Cookie);
        Assert.True(options.SecureCookie && options.BehindFront);
        Assert.False(WebMode.Options(r.Config).BehindFront);

        var status = Env(r);
        Assert.Equal(0, await new CliApp(status).RunAsync(["web", "status"]));
        Assert.Contains("Mode: local", status.Out.ToString());
        Assert.Contains("web.public_origin is not set", status.Out.ToString());
        Assert.Contains("admin secret", status.Out.ToString());
    }

    [Fact(DisplayName = "HTTPS front recipe: Caddy pinned by digest, admin API off, forwards to 127.0.0.1:7477 with fresh forwarded headers")]
    public void FrontRecipe()
    {
        var dir = Path.Combine(Applied.RepoRoot(), "bot", "docker", "web-front");
        var caddy = File.ReadAllText(Path.Combine(dir, "Caddyfile"));
        Assert.Contains("admin off", caddy);
        Assert.Contains("reverse_proxy 127.0.0.1:7477", caddy);
        Assert.Contains("header_up X-Forwarded-Proto https", caddy);
        Assert.Contains("header_up X-Forwarded-For {remote_host}", caddy);
        Assert.DoesNotContain("http://", caddy);
        var compose = File.ReadAllText(Path.Combine(dir, "compose.yaml"));
        Assert.Contains("caddy:2@${CADDY_DIGEST:?", compose);
        Assert.Contains("cap_drop: [ALL]", compose);
        Assert.Contains("no-new-privileges:true", compose);
        Assert.Contains("read_only: true", compose);
        Assert.DoesNotContain("privileged", compose);
        Assert.DoesNotContain("docker.sock", compose);
    }

    [Fact(DisplayName = "users in web-multi: an owner gets an isolated roster of their own; operators and viewers name one; web-single takes no --home")]
    public async Task UsersMulti()
    {
        using var r = new TempRoster();
        Assert.Equal(0, await new CliApp(Env(r)).RunAsync(["web", "admin-secret"]));
        Assert.Equal(Exit.Invalid, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "alice", "--role", "owner", "--home", "alice"]));
        r.Config.Mode = "web-multi";
        r.Config.Save(r.Paths);
        Assert.Equal(0, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "alice", "--role", "owner"]));
        Assert.Equal(0, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "bob", "--role", "owner"]));
        Assert.Equal(Exit.Invalid, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "carol", "--role", "viewer"]));
        Assert.Equal(Exit.Invalid, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "carol", "--role", "viewer", "--home", "nobody"]));
        Assert.Equal(0, await new CliApp(Env(r, "Tall pines 7 quiet river\n")).RunAsync(["users", "add", "carol", "--role", "viewer", "--home", "alice"]));
        var users = new UsersDb(r.Paths.UsersDb);
        var alice = Tenants.Find(users, r.Paths, "alice")!;
        var bob = Tenants.Find(users, r.Paths, "bob")!;
        Assert.Equal(1, alice.Index);
        Assert.Equal(2, bob.Index);
        Assert.Null(Tenants.Problem(alice));
        var config = RosterConfig.Load(alice.Paths);
        Assert.Equal("u01", config.Bots.Tenant);
        Assert.Equal("local", config.Mode);
        Assert.Equal(Path.Combine(r.Paths.Home, "secrets", "inference.key"), config.Gateway.UpstreamKeyFile);
        Assert.True(SecureFile.IsPrivate(alice.Paths.Home));
        Assert.Equal("alice", new WebAccounts(users, r.Clock, new WebAuth(users, r.Clock, TimeSpan.FromHours(1)), r.Config).Find("carol")!.Home);
        var homes = Env(r);
        Assert.Equal(0, await new CliApp(homes).RunAsync(["users", "homes"]));
        Assert.Contains("alice", homes.Out.ToString());
        Assert.Contains("u02", homes.Out.ToString());
        Assert.Contains("ok", homes.Out.ToString());
        config.Bots.Tenant = null;
        config.Save(alice.Paths);
        Assert.NotNull(Tenants.Problem(alice));
    }
}
