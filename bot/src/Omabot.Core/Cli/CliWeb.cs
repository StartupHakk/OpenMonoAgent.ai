// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Web;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    private WebAuth WebAuthFor(Config.RosterConfig config)
        => new(new UsersDb(env.Paths.UsersDb), env.Clock, TimeSpan.FromHours(config.Web.SessionHours), config.Mode == "local" ? null : TimeSpan.FromMinutes(config.Web.IdleMinutes));

    private async Task<int> WebCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("web open|enable|disable|status|admin-secret");
        var config = LoadConfig();
        switch (sub)
        {
            case "open":
                {
                    a.NoMore();
                    if (config.Mode != "local") throw new ArgsException("oma-bot web open is for local mode; web mode signs in with a password and a code.");
                    var who = PersonAtCli;
                    var code = WebAuthFor(config).CreateLoginCode("local:" + who.Id, who.Id, "owner", TimeSpan.FromMinutes(5));
                    var (_, audit, _, _) = OpenDb();
                    audit.Write(who.ToString(), "web.link", "local", "one-time sign-in link made");
                    env.Out.WriteLine("Open this link in a browser on this computer within 5 minutes (it works once):");
                    env.Out.WriteLine($"http://127.0.0.1:{config.Api.Port}/login?code={code}");
                    return Exit.Ok;
                }
            case "enable":
                {
                    var origin = a.Option("--public-origin");
                    a.NoMore();
                    if (origin is not null) config.Web.PublicOrigin = origin.TrimEnd('/');
                    config.Web.BehindHttpsFront = true;
                    var wasMode = config.Mode;
                    config.Mode = "web-single";
                    var problems = WebMode.Problems(config, env.Paths);
                    if (problems.Count > 0)
                    {
                        foreach (var p in problems) env.Err.WriteLine("Refused: " + p);
                        env.Err.WriteLine("Web mode stays off.");
                        return Exit.Failed;
                    }
                    var nonce = WebMode.NewNonce();
                    SecureFile.WriteAllTextAtomic(env.Paths.WebCheckNonce, nonce);
                    string? front;
                    try { front = await WebMode.FrontProblemAsync(config, nonce, env.HttpsHandler(), ct).ConfigureAwait(false); }
                    finally { File.Delete(env.Paths.WebCheckNonce); }
                    if (front is not null)
                    {
                        env.Err.WriteLine("Refused: " + front);
                        env.Err.WriteLine("Start oma-bot serve and the HTTPS front (docker/web-front), then run this again. Web mode stays off.");
                        return Exit.Failed;
                    }
                    config.Save(env.Paths);
                    var (_, audit, _, _) = OpenDb();
                    audit.Write(PersonAtCli.ToString(), "web.enabled", config.Web.PublicOrigin!, $"from {wasMode}");
                    env.Out.WriteLine($"Web mode is on for {config.Web.PublicOrigin}. Restart oma-bot serve to apply it.");
                    return Exit.Ok;
                }
            case "disable":
                {
                    a.NoMore();
                    var was = config.Mode;
                    config.Mode = "local";
                    config.Save(env.Paths);
                    var ended = File.Exists(env.Paths.UsersDb) ? WebAuthFor(config).RevokeAll() : 0;
                    var (_, audit, _, _) = OpenDb();
                    audit.Write(PersonAtCli.ToString(), "web.disabled", was, $"{ended} session(s) ended");
                    env.Out.WriteLine($"Web mode is off ({ended} session(s) ended). Restart oma-bot serve to apply it.");
                    return Exit.Ok;
                }
            case "status":
                {
                    a.NoMore();
                    env.Out.WriteLine($"Mode: {config.Mode}");
                    env.Out.WriteLine($"Public origin: {config.Web.PublicOrigin ?? "(not set)"}");
                    env.Out.WriteLine($"Listens on: http://{config.Api.BindAddress}:{config.Api.Port}/");
                    if (File.Exists(env.Paths.UsersDb))
                    {
                        var accounts = new WebAccounts(new UsersDb(env.Paths.UsersDb), env.Clock, WebAuthFor(config), config).List();
                        env.Out.WriteLine($"Accounts: {accounts.Count(x => x.State == "active")} active, {accounts.Count(x => x.State == "pending")} pending, {accounts.Count(x => x.State == "disabled")} disabled");
                    }
                    var check = config.Mode == "local" ? WithMode(config, "web-single") : config;
                    var problems = WebMode.Problems(check, env.Paths);
                    env.Out.WriteLine(problems.Count == 0 ? "Web-single checks: all pass (the HTTPS front is checked by web enable and doctor --online)." : "Web-single checks:");
                    foreach (var p in problems) env.Out.WriteLine("  " + p);
                    return Exit.Ok;
                }
            case "admin-secret":
                {
                    var rotate = a.Flag("--rotate");
                    a.NoMore();
                    var path = WebSecrets.PathOf(config, env.Paths);
                    var (_, audit, _, _) = OpenDb();
                    if (!rotate)
                    {
                        if (!WebSecrets.Create(path))
                        {
                            env.Out.WriteLine($"{path} already exists. --rotate replaces it and re-seals every account's second factor.");
                            return Exit.Ok;
                        }
                        audit.Write(PersonAtCli.ToString(), "web.admin_secret", path, "created");
                        env.Out.WriteLine($"Wrote {path} (0600). It is never printed; keep a copy offline with the host's backups.");
                        return Exit.Ok;
                    }
                    if (!File.Exists(path)) throw new ArgsException($"{path} does not exist. Run oma-bot web admin-secret first.");
                    var oldKey = WebSecrets.SeedKeyFrom(File.ReadAllText(path).Trim());
                    var secret = WebSecrets.NewSecret();
                    var newKey = WebSecrets.SeedKeyFrom(secret);
                    // The new secret is on disk before any seed moves to it, so a crash leaves both values to recover from.
                    SecureFile.WriteAllTextAtomic(path + ".new", secret + "\n");
                    var broken = File.Exists(env.Paths.UsersDb) ? new WebAccounts(new UsersDb(env.Paths.UsersDb), env.Clock, WebAuthFor(config), config).Reseal(oldKey, newKey) : 0;
                    File.Move(path + ".new", path, overwrite: true);
                    audit.Write(PersonAtCli.ToString(), "web.admin_secret", path, $"rotated; {broken} seed(s) could not be opened");
                    env.Out.WriteLine($"Rotated {path}." + (broken > 0 ? $" {broken} account(s) need a new second factor (users disable, then add again)." : ""));
                    return broken > 0 ? Exit.Failed : Exit.Ok;
                }
            default:
                throw new ArgsException("Use: oma-bot web open|enable|disable|status|admin-secret");
        }
    }

    private async Task<int> ImagesCmd(Args a, CancellationToken ct)
    {
        var sub = a.NextOrNull() ?? "status";
        var config = LoadConfig();
        switch (sub)
        {
            case "status":
                {
                    a.NoMore();
                    var current = env.Supervisor() is { } s ? await s.ImageIdAsync(config.Bots.Image, ct).ConfigureAwait(false) : null;
                    env.Out.WriteLine($"Image: {config.Bots.Image}");
                    env.Out.WriteLine($"On this host: {current ?? "(unknown; the supervisor is not running or the image is missing)"}");
                    env.Out.WriteLine($"Accepted: {config.Images.AcceptedDigest ?? "(none)"}");
                    var problem = Supervision.BotLauncher.ImageProblem(config, current);
                    env.Out.WriteLine(problem ?? "Bots may start: the image on this host is the accepted one.");
                    return problem is null ? Exit.Ok : Exit.Failed;
                }
            case "accept":
                {
                    var digest = a.Option("--digest");
                    a.NoMore();
                    var current = await RequireSupervisor().ImageIdAsync(config.Bots.Image, ct).ConfigureAwait(false)
                        ?? throw new ArgsException($"The agent image {config.Bots.Image} is not on this host.");
                    if (digest is not null && !string.Equals(digest, current, StringComparison.Ordinal))
                    {
                        env.Err.WriteLine($"Refused: the image on this host is {current}, not {digest}.");
                        return Exit.Failed;
                    }
                    var before = config.Images.AcceptedDigest;
                    config.Images.AcceptedDigest = current;
                    config.Save(env.Paths);
                    var (_, audit, _, _) = OpenDb();
                    audit.Write(PersonAtCli.ToString(), "images.accept", config.Bots.Image, $"{before ?? "none"} to {current}");
                    env.Out.WriteLine($"Accepted {config.Bots.Image} at {current}.");
                    return Exit.Ok;
                }
            default:
                throw new ArgsException("Use: oma-bot images [status] | accept [--digest sha256:...]");
        }
    }

    private Task<int> ReleaseCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("release check");
        if (sub != "check") throw new ArgsException("Use: oma-bot release check [--repo DIR] [--advisories FILE | --online] [--multi]");
        var repo = Path.GetFullPath(a.Option("--repo") ?? Directory.GetCurrentDirectory());
        var file = a.Option("--advisories");
        var online = a.Flag("--online");
        var multi = a.Flag("--multi");
        a.NoMore();
        if (!File.Exists(Path.Combine(repo, "Omabot.sln")) && File.Exists(Path.Combine(repo, "bot", "Omabot.sln"))) repo = Path.Combine(repo, "bot");
        if (!File.Exists(Path.Combine(repo, "Omabot.sln"))) throw new ArgsException($"{repo} is not the bot folder (no Omabot.sln). Give --repo.");
        IReadOnlyList<Release.Advisory>? advisories = null;
        string? problem = null;
        if (file is not null) advisories = Release.ReleaseGate.ReadAdvisories(file);
        if (online)
        {
            var (list, why) = Release.ReleaseGate.OnlineAdvisories(repo);
            if (list is null) { advisories = null; problem = why; }
            else advisories = [.. advisories ?? [], .. list];
        }
        var lines = Release.ReleaseGate.Check(repo, advisories, problem, multi);
        foreach (var l in lines) env.Out.WriteLine($"[{(l.Ok ? "pass" : "FAIL")}] {l.Check}: {l.Message}");
        var ok = lines.All(l => l.Ok);
        env.Out.WriteLine(ok ? "The automatic gate passes. The release still needs the tests in 11.8 green and Spencer's sign-off." : "Release blocked.");
        return Task.FromResult(ok ? Exit.Ok : Exit.Failed);
    }

    private static Config.RosterConfig WithMode(Config.RosterConfig c, string mode)
    {
        var copy = c.Clone();
        copy.Mode = mode;
        copy.Web.BehindHttpsFront = true;
        return copy;
    }

    private string FirstLine(string what)
    {
        var text = env.ReadStdin() ?? throw new ArgsException($"Give the {what} on standard input, never as an argument.");
        var line = text.Split('\n')[0].TrimEnd('\r');
        if (line.Length == 0) throw new ArgsException($"The {what} on standard input is empty.");
        return line;
    }

    private Task<int> UsersCmd(Args a, CancellationToken ct)
    {
        var sub = a.NextOrNull() ?? "list";
        var config = LoadConfig();
        var (_, audit, _, _) = OpenDb();
        var accounts = new WebAccounts(new UsersDb(env.Paths.UsersDb), env.Clock, WebAuthFor(config), config, audit);
        byte[] SeedKey() => WebSecrets.Problem(WebSecrets.PathOf(config, env.Paths)) is { } p
            ? throw new ArgsException(p)
            : WebSecrets.SeedKey(WebSecrets.PathOf(config, env.Paths));
        int Report((bool Ok, string Message) r)
        {
            (r.Ok ? env.Out : env.Err).WriteLine(r.Message);
            return r.Ok ? Exit.Ok : Exit.Failed;
        }
        switch (sub)
        {
            case "list":
                a.NoMore();
                if (accounts.List().Count == 0) env.Out.WriteLine("No web accounts. Add one with: oma-bot users add NAME --role owner < password-file");
                foreach (var x in accounts.List())
                {
                    var locked = x.LockedUntil is { } lu && lu > env.Clock.UtcNow ? $" locked until {lu:yyyy-MM-dd HH:mm}Z" : "";
                    var home = x.Home is null ? "" : $" roster {x.Home}";
                    env.Out.WriteLine($"{x.Name,-20} {x.Role,-9} {x.State,-9} last sign-in {x.LastLoginAt ?? "never"}{home}{locked}");
                }
                return Task.FromResult(Exit.Ok);
            case "homes":
                {
                    a.NoMore();
                    var homes = Tenants.All(new UsersDb(env.Paths.UsersDb), env.Paths);
                    if (homes.Count == 0) env.Out.WriteLine("No user rosters. In web-multi, oma-bot users add NAME --role owner makes one.");
                    foreach (var h in homes)
                    {
                        var block = Tenants.PortBlock(h.Index);
                        env.Out.WriteLine($"{h.Name,-20} {h.Id}  OMABOT_HOME={h.Paths.Home}  ports {block} to {block + 199}  {Tenants.Problem(h) ?? "ok"}");
                    }
                    return Task.FromResult(Exit.Ok);
                }
            case "add":
                {
                    var name = a.Next("users add NAME --role ROLE [--home ROSTER]");
                    var role = a.Option("--role") ?? throw new ArgsException("Give --role owner, operator or viewer.");
                    var home = a.Option("--home");
                    a.NoMore();
                    var key = SeedKey();
                    if (config.Mode != "web-multi" && home is not null) throw new ArgsException("--home is for web-multi, where each user has a roster of their own.");
                    if (config.Mode == "web-multi")
                    {
                        home ??= role == "owner" ? name : throw new ArgsException("An operator or viewer works in someone's roster: give --home ROSTER.");
                        var usersDb = new UsersDb(env.Paths.UsersDb);
                        if (role == "owner" && home == name) Tenants.Ensure(usersDb, config, env.Paths, home, env.Clock);
                        else if (Tenants.Find(usersDb, env.Paths, home) is null) throw new ArgsException($"No user roster {home}. Add its owner first.");
                    }
                    var (ok, message, uri) = accounts.Add(name, role, FirstLine("password"), key, home);
                    if (!ok) { env.Err.WriteLine(message); return Task.FromResult(Exit.Failed); }
                    SecureFile.EnsureDirNoClobber(env.Paths.WebEnroll);
                    var file = Path.Combine(env.Paths.WebEnroll, name + ".txt");
                    Redactor.Shared.AddKnownSecret(uri);
                    SecureFile.WriteAllTextAtomic(file, $"Add this to an authenticator app (scan it as a QR code or paste it):\n{uri}\n");
                    env.Out.WriteLine(message);
                    env.Out.WriteLine($"The authenticator key is in {file} (0600); it is never printed.");
                    env.Out.WriteLine($"Add it to an authenticator app, then run: oma-bot users confirm {name} (the current 6-digit code on standard input).");
                    return Task.FromResult(Exit.Ok);
                }
            case "confirm":
                {
                    var name = a.Next("users confirm NAME");
                    a.NoMore();
                    var r = accounts.Confirm(name, FirstLine("code"), SeedKey());
                    if (r.Ok) File.Delete(Path.Combine(env.Paths.WebEnroll, name + ".txt"));
                    return Task.FromResult(Report(r));
                }
            case "role":
                {
                    var name = a.Next("users role NAME ROLE");
                    var role = a.Next("users role NAME ROLE");
                    a.NoMore();
                    return Task.FromResult(Report(accounts.SetRole(name, role)));
                }
            case "disable" or "enable" or "logout" or "password":
                {
                    var name = a.Next($"users {sub} NAME");
                    a.NoMore();
                    return Task.FromResult(sub switch
                    {
                        "disable" => Report(accounts.Disable(name)),
                        "enable" => Report(accounts.Enable(name)),
                        "password" => Report(accounts.SetPassword(name, FirstLine("password"))),
                        _ => accounts.Find(name) is null ? Report((false, $"No account {name}.")) : Report((true, $"{accounts.Logout(name)} session(s) of {name} ended.")),
                    });
                }
            default:
                throw new ArgsException("Use: oma-bot users add|confirm|list|homes|role|disable|enable|logout|password");
        }
    }
}
