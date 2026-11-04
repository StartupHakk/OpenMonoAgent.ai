// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;

namespace Omabot.Web;

/// <summary>
/// Where the web view runs and which Host and Origin values it accepts. Behind
/// the HTTPS front every request must say it arrived over https, and the
/// client address is the one the front saw.
/// </summary>
public sealed record WebOptions(string Mode, IReadOnlyList<string> Hosts, IReadOnlyList<string> Origins, bool SecureCookie, string Cookie = WebAuth.Cookie, bool BehindFront = false);

/// <summary>What the web view reads and changes. Every change goes through the same stores and policies as the CLI.</summary>
public sealed class WebDeps
{
    public required RosterDb Db { get; init; }
    public required RosterConfig Config { get; init; }
    public required RosterPaths Paths { get; init; }
    public required IClock Clock { get; init; }
    public required AuditLog Audit { get; init; }
    public required Notices Notices { get; init; }
    public required WebAuth Auth { get; init; }
    public required WebOptions Options { get; init; }
    public Func<IApproverPolicy>? Policy { get; init; }
    public Rooms.RoomService? Rooms { get; init; }
    /// <summary>Phase 11: which bots a user may see and act on. Null means every bot (local and single-owner modes).</summary>
    public Func<WebSession, string, bool>? MayUseBot { get; init; }
    /// <summary>Web modes: accounts for password and code sign-in, and the key their seeds are sealed under.</summary>
    public WebAccounts? Accounts { get; init; }
    public Func<byte[]>? SeedKey { get; init; }
    /// <summary>Web modes: false until the HTTPS front check passes; sign-in and the API wait for it.</summary>
    public Func<bool>? FrontVerified { get; init; }
    /// <summary>
    /// web-multi: the signed-in account's own roster (its database, config, audit and rooms), or null when the
    /// account has none. Every API call works only against what this returns, so no URL can name another user's data.
    /// </summary>
    public Func<WebSession, WebDeps?>? TenantFor { get; init; }
    /// <summary>Accepts the value the HTTPS front check sends to /healthz.</summary>
    public Func<string, bool>? HealthNonce { get; init; }
    /// <summary>Extra routes from later phases.</summary>
    public Action<WebApplication, WebDeps>? Extend { get; init; }
}

/// <summary>
/// The roster web view (plan 7.15 U1, decision B6): plain HTML and ES
/// modules served by the roster, no framework, no inline script. Every
/// response carries a strict Content Security Policy and the usual hardening
/// headers. The Host header must be one the roster serves (DNS rebinding);
/// a change needs a session cookie, a matching Origin and the session's CSRF
/// token; viewers only read. Answers to cards are bound to the card's action
/// hash and go through the approver policy.
/// </summary>
public static class WebServer
{
    public const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static WebApplication Build(WebDeps d, IPAddress ip, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(ip, port);
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        });
        var app = builder.Build();
        Configure(app, d);
        return app;
    }

    public static void Configure(WebApplication app, WebDeps d)
    {
        var failures = new ConcurrentDictionary<string, (int Count, DateTimeOffset Since)>(StringComparer.Ordinal);
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["Content-Security-Policy"] = Csp;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            if (d.Options.SecureCookie) h["Strict-Transport-Security"] = "max-age=31536000";
            if (!ctx.Request.Path.StartsWithSegments("/assets")) h["Cache-Control"] = "no-store";
            if (ctx.Request.Path.Equals(WebMode.HealthPath, StringComparison.Ordinal))
            {
                // Answered before the Host check so the front can be proven before web mode is on; it only echoes a value the roster chose.
                var n = ctx.Request.Query["n"].ToString();
                if (!HttpMethods.IsGet(ctx.Request.Method) || d.HealthNonce is null || !d.HealthNonce(n)) { ctx.Response.StatusCode = 404; return; }
                var https = string.Equals(ctx.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.Ordinal);
                await ctx.Response.WriteAsync(https ? "ok oma-bot https" : "ok oma-bot http").ConfigureAwait(false);
                return;
            }
            var host = ctx.Request.Host.Value ?? "";
            if (!d.Options.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 421;
                await ctx.Response.WriteAsync("This roster does not serve that host name.").ConfigureAwait(false);
                return;
            }
            if (d.Options.BehindFront && !string.Equals(ctx.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("This roster only serves HTTPS through its front.").ConfigureAwait(false);
                return;
            }
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                var origin = ctx.Request.Headers.Origin.ToString();
                if (origin.Length == 0 || !d.Options.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("Refused: the request did not come from this page.").ConfigureAwait(false);
                    return;
                }
            }
            if (d.FrontVerified is { } verified && !verified() && (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/login")))
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"The HTTPS front is not verified yet; sign-in waits for it.\"}").ConfigureAwait(false);
                return;
            }
            await next(ctx).ConfigureAwait(false);
        });

        foreach (var (path, file, type) in new[] { ("/", "index.html", "text/html; charset=utf-8"), ("/assets/app.js", "app.js", "text/javascript; charset=utf-8"), ("/assets/style.css", "style.css", "text/css; charset=utf-8") })
            app.MapGet(path, () => Results.Text(Asset(file), type));
        app.MapGet("/source", () => Results.Redirect(d.Config.Web.SourceUrl, permanent: false));

        app.MapGet("/login", (HttpContext ctx) =>
        {
            if (d.Options.Mode != "local") return Results.NotFound();
            var ip = ClientIp(ctx, d);
            if (failures.TryGetValue(ip, out var f) && f.Count >= 10 && d.Clock.UtcNow - f.Since < TimeSpan.FromMinutes(10))
                return Results.Text("Too many tries. Wait ten minutes.", statusCode: 429);
            var session = d.Auth.Redeem(ctx.Request.Query["code"].ToString(), ip);
            if (session is null)
            {
                failures.AddOrUpdate(ip, (1, d.Clock.UtcNow), (_, v) => d.Clock.UtcNow - v.Since > TimeSpan.FromMinutes(10) ? (1, d.Clock.UtcNow) : (v.Count + 1, v.Since));
                return Results.Text("This sign-in link is used or expired. Run oma-bot web open again.", statusCode: 400);
            }
            SetCookie(ctx, session, d);
            d.Audit.Write("web:" + session.Name, "web.login", session.UserId, "one-time link");
            return Results.Redirect("/", permanent: false);
        });
        app.MapPost("/login", async (HttpContext ctx) =>
        {
            if (d.Options.Mode == "local" || d.Accounts is null || d.SeedKey is null) return Results.NotFound();
            var body = await Body(ctx).ConfigureAwait(false);
            // A cookie the browser already holds is never carried into the new session (fixation).
            if (ctx.Request.Cookies[d.Options.Cookie] is { } old) d.Auth.Revoke(old);
            var r = d.Accounts.Login(Str(body, "name"), Str(body, "password"), Str(body, "code"), ClientIp(ctx, d), d.SeedKey());
            if (!r.Ok) return Results.Json(new { error = r.Message }, statusCode: r.Status);
            SetCookie(ctx, r.Session!, d);
            return Results.Json(new { ok = true });
        });
        d.Extend?.Invoke(app, d);

        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (fc, next) =>
        {
            var ctx = fc.HttpContext;
            var s = d.Auth.Validate(ctx.Request.Cookies[d.Options.Cookie]);
            if (s is null) return Results.Json(new { error = "Sign in first.", mode = d.Options.Mode }, statusCode: 401);
            if (!HttpMethods.IsGet(ctx.Request.Method))
            {
                if (!SecureFile.FixedTimeEquals(ctx.Request.Headers["X-CSRF-Token"].ToString(), s.Csrf)) return Results.Json(new { error = "Refused: the request token is missing or wrong." }, statusCode: 403);
                if (!s.CanAct) return Results.Json(new { error = "Your account can only read." }, statusCode: 403);
            }
            if (d.TenantFor is not null)
            {
                if (d.TenantFor(s) is not { } mine) return Results.Json(new { error = "This account has no roster to open." }, statusCode: 403);
                ctx.Items["deps"] = mine;
            }
            ctx.Items["session"] = s;
            return await next(fc).ConfigureAwait(false);
        });

        static WebSession S(HttpContext ctx) => (WebSession)ctx.Items["session"]!;
        WebDeps D(HttpContext ctx) => ctx.Items["deps"] as WebDeps ?? d;
        bool MayUse(HttpContext ctx, string bot) => d.MayUseBot?.Invoke(S(ctx), bot) ?? true;

        api.MapGet("/session", (HttpContext ctx) => Results.Json(new { name = S(ctx).Name, role = S(ctx).Role, csrf = S(ctx).Csrf, mode = d.Options.Mode }));
        api.MapPost("/logout", (HttpContext ctx) =>
        {
            d.Auth.Revoke(S(ctx).Id);
            ClearCookie(ctx, d);
            return Results.Json(new { ok = true });
        });
        api.MapPost("/logout-all", (HttpContext ctx) =>
        {
            var n = d.Auth.RevokeUser(S(ctx).UserId);
            ClearCookie(ctx, d);
            d.Audit.Write("web:" + S(ctx).Name, "web.logout_everywhere", S(ctx).UserId, $"{n} session(s) ended");
            return Results.Json(new { ok = true, ended = n });
        });

        api.MapGet("/bots", (HttpContext ctx) => Results.Json(new BotStore(D(ctx).Db, D(ctx).Clock).All().Where(b => MayUse(ctx, b.Slug)).Select(b => new
        {
            name = b.Name, slug = b.Slug, title = b.Spec.Title, role = b.Spec.Role, state = b.State, enabled = b.Enabled, port = b.Port,
            last_active = b.LastActiveAt, disabled_reason = b.DisabledReason,
        }), Json));

        api.MapGet("/notices", (HttpContext ctx) => Results.Json(D(ctx).Notices.Unseen().Where(n => n.Bot is null || MayUse(ctx, Names.Slug(n.Bot))).Select(n => new { id = n.Id, at = n.At, level = n.Level, bot = n.Bot, text = n.Text }), Json));
        api.MapPost("/notices/{id:long}/seen", (HttpContext ctx, long id) => { D(ctx).Notices.MarkSeen(id); return Results.Json(new { ok = true }); });

        api.MapGet("/approvals", (HttpContext ctx) => Results.Json(new ApprovalStore(D(ctx).Db, D(ctx).Clock, Redactor.Shared).Pending(S(ctx).Role == "owner" && d.MayUseBot is null ? null : S(ctx).UserId)
            .Where(p => MayUse(ctx, p.Bot)).Select(p => new
            {
                id = p.Id, kind = p.Kind, bot = p.Bot, summary = p.Summary, action = p.Action, hash = p.ActionHash, person_only = p.PersonOnly, bound_to = p.BoundTo,
                expires_at = p.ExpiresAt.ToString("O"), requester = p.RequesterKind + ":" + p.Requester, origin = p.Origin,
            }), Json));
        api.MapPost("/approvals/{id}", async (HttpContext ctx, string id) =>
        {
            var body = await Body(ctx).ConfigureAwait(false);
            var decision = Str(body, "decision");
            var hash = Str(body, "hash");
            if (decision is not ("allow" or "deny") || string.IsNullOrEmpty(hash)) return Results.Json(new { error = "decision (allow or deny) and the card's hash are required." }, statusCode: 400);
            var store = new ApprovalStore(D(ctx).Db, D(ctx).Clock, Redactor.Shared);
            if (store.Get(id) is not { } card || !MayUse(ctx, card.Bot)) return Results.Json(new { error = "No such card." }, statusCode: 404);
            var s = S(ctx);
            var r = store.Decide(id, Approver.Person(s.Name, s.UserId, s.Role), decision == "allow", Str(body, "reason"), hash, D(ctx).Policy?.Invoke() ?? new ApproverPolicy(D(ctx).Db, D(ctx).Clock, D(ctx).Config), Str(body, "text"));
            D(ctx).Audit.Write("web:" + s.Name, r.Ok ? "approval." + decision : "approval.refused", id, r.Message);
            return r.Ok ? Results.Json(new { ok = true, message = r.Message }) : Results.Json(new { error = r.Message }, statusCode: 409);
        });

        api.MapGet("/routines", (HttpContext ctx) =>
        {
            var store = new Routines.RoutineStore(D(ctx).Db, D(ctx).Clock);
            return Results.Json(new BotStore(D(ctx).Db, D(ctx).Clock).All().Where(b => MayUse(ctx, b.Slug)).SelectMany(b => b.Spec.Routines.Select(r =>
            {
                var st = store.Find(b.Slug, r.Name);
                return new { bot = b.Name, name = r.Name, rule = r.Describe(), enabled = r.Enabled, paused = st?.Paused ?? false, next = st?.NextFire?.ToString("O"), last_status = st?.LastStatus };
            })), Json);
        });

        api.MapGet("/audit", (HttpContext ctx) =>
        {
            if (S(ctx).Role != "owner") return Results.Json(new { error = "Only the owner reads the audit log." }, statusCode: 403);
            var n = int.TryParse(ctx.Request.Query["n"], out var v) ? Math.Clamp(v, 1, 500) : 100;
            return Results.Json(D(ctx).Db.Query("SELECT id, at, actor, action, subject, detail FROM audit ORDER BY id DESC LIMIT $n;",
                r => new { id = r.GetInt64(0), at = r.GetString(1), actor = r.GetString(2), action = r.GetString(3), subject = r.GetString(4), detail = r.GetString(5) }, ("$n", n)), Json);
        });

        api.MapGet("/memory/{bot}", (HttpContext ctx, string bot) =>
        {
            var row = new BotStore(D(ctx).Db, D(ctx).Clock).Find(bot);
            if (row is null || !MayUse(ctx, row.Slug)) return Results.Json(new { error = "No such bot." }, statusCode: 404);
            var store = Memory.MemoryStores.For(D(ctx).Db, D(ctx).Clock, D(ctx).Paths, row.Spec);
            if (store is null) return Results.Json(new { block = "", journal = Array.Empty<object>(), enabled = false });
            return Results.Json(new
            {
                enabled = true,
                block = store.Load().Block,
                journal = store.JournalRows(30).Select(j => new { id = j.Id, at = j.At, actor = j.Actor, file = j.File, reason = j.Reason, undoable = j.Undoable }),
            }, Json);
        });
        api.MapPost("/memory/{bot}/undo", async (HttpContext ctx, string bot) =>
        {
            var row = new BotStore(D(ctx).Db, D(ctx).Clock).Find(bot);
            if (row is null || !MayUse(ctx, row.Slug)) return Results.Json(new { error = "No such bot." }, statusCode: 404);
            var body = await Body(ctx).ConfigureAwait(false);
            if (body["id"] is not JsonValue iv || !iv.TryGetValue<long>(out var jid)) return Results.Json(new { error = "id is required." }, statusCode: 400);
            var store = Memory.MemoryStores.For(D(ctx).Db, D(ctx).Clock, D(ctx).Paths, row.Spec);
            if (store is null) return Results.Json(new { error = "Memory is off for this bot." }, statusCode: 409);
            var r = store.Undo(jid, "person");
            if (r.Ok) D(ctx).Audit.Write("web:" + S(ctx).Name, "memory.undo", row.Name, $"journal {jid}");
            return r.Ok ? Results.Json(new { ok = true, message = r.Message }) : Results.Json(new { error = r.Message }, statusCode: 409);
        });

        api.MapGet("/rooms", (HttpContext ctx) => Results.Json((D(ctx).Rooms?.Rooms() ?? []).Where(r => r.Members.Any(m => MayUse(ctx, Names.Slug(m))))
            .Select(r => new { name = r.Name, responder = r.Responder, members = r.Members }), Json));
        api.MapGet("/rooms/{name}", (HttpContext ctx, string name) =>
        {
            var rooms = D(ctx).Rooms;
            var room = rooms?.Find(name);
            if (rooms is null || room is null || !room.Members.Any(m => MayUse(ctx, Names.Slug(m)))) return Results.Json(new { error = "No such room." }, statusCode: 404);
            var after = long.TryParse(ctx.Request.Query["after"], out var a) ? a : 0;
            return Results.Json(new
            {
                messages = rooms.Messages(room.Name, after, 200).Select(m => new { id = m.Id, at = m.At, author = m.Author, kind = m.AuthorKind, text = m.Text }),
                turns = rooms.Turns(room.Name).Where(t => t.Status is "queued" or "running").Select(t => new { bot = t.Bot, status = t.Status, hop = t.Hop }),
            }, Json);
        });
        api.MapPost("/rooms/{name}", async (HttpContext ctx, string name) =>
        {
            var room = D(ctx).Rooms?.Find(name);
            if (room is null || !room.Members.Any(m => MayUse(ctx, Names.Slug(m)))) return Results.Json(new { error = "No such room." }, statusCode: 404);
            var body = await Body(ctx).ConfigureAwait(false);
            var (ok, message, id) = D(ctx).Rooms!.PostByPerson(room.Name, Str(body, "text") ?? "", "web:" + S(ctx).Name);
            return ok ? Results.Json(new { ok, message, id }) : Results.Json(new { error = message }, statusCode: 400);
        });
    }

    /// <summary>The address the request came from: behind the front, the last address the front added to X-Forwarded-For.</summary>
    public static string ClientIp(HttpContext ctx, WebDeps d)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (d.Options.BehindFront && remote is not null && IPAddress.IsLoopback(remote))
        {
            var last = ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null && IPAddress.TryParse(last, out var ip)) return ip.ToString();
        }
        return remote?.ToString() ?? "?";
    }

    private static void ClearCookie(HttpContext ctx, WebDeps d)
        => ctx.Response.Cookies.Delete(d.Options.Cookie, new CookieOptions { Path = "/", Secure = d.Options.SecureCookie, HttpOnly = true, SameSite = SameSiteMode.Strict });

    public static void SetCookie(HttpContext ctx, WebSession session, WebDeps d)
        => ctx.Response.Cookies.Append(d.Options.Cookie, session.Id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = d.Options.SecureCookie,
            Path = "/",
            // Relative, so a client clock that is off cannot keep or drop the cookie; the server enforces the expiry.
            MaxAge = session.ExpiresAt - d.Clock.UtcNow,
            IsEssential = true,
        });

    public static async Task<JsonObject> Body(HttpContext ctx)
    {
        if (!(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return [];
        try { return await JsonNode.ParseAsync(ctx.Request.Body).ConfigureAwait(false) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    public static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string Asset(string file)
    {
        var asm = typeof(WebServer).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("web." + file, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("Web asset not found: " + file);
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Local mode: 127.0.0.1 and localhost on the API port, plain http, no Secure cookie.</summary>
    public static WebOptions Local(RosterConfig c)
    {
        var p = c.Api.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new WebOptions("local", [$"127.0.0.1:{p}", $"localhost:{p}"], [$"http://127.0.0.1:{p}", $"http://localhost:{p}"], false);
    }
}
