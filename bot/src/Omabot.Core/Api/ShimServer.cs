// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Omabot.Common;

namespace Omabot.Api;

/// <summary>
/// The roster side of the MCP shim. Each bot gets its own unix socket in
/// bots/BOT/shim/, mounted only into that bot's container, so the bot is
/// known from the socket the call came in on. Calls also need the per-turn
/// token from <see cref="ShimTokens"/>.
/// </summary>
public sealed class ShimServer(RosterToolHost tools, ShimTokens tokens)
{
    private readonly Dictionary<string, WebApplication> _apps = new(StringComparer.Ordinal);

    /// <summary>Handles one request for <paramref name="bot"/> (the socket owner).</summary>
    public async Task HandleAsync(HttpContext ctx, string bot)
    {
        var path = ctx.Request.Path.Value ?? "";
        ctx.Response.ContentType = "application/json";
        if (ctx.Request.Method == "GET" && path == "/v1/tools")
        {
            await ctx.Response.WriteAsync(new JsonObject { ["tools"] = tools.ListJson(bot) }.ToJsonString()).ConfigureAwait(false);
            return;
        }
        if (ctx.Request.Method == "POST" && path == "/v1/turn/claim")
        {
            var token = tokens.Claim(bot);
            if (token is null)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsync("""{"error":"no_turn","message":"Roster tools work only during a turn the roster started."}""").ConfigureAwait(false);
                return;
            }
            await ctx.Response.WriteAsync(new JsonObject { ["token"] = token }.ToJsonString()).ConfigureAwait(false);
            return;
        }
        if (ctx.Request.Method == "POST" && path == "/v1/tools/call")
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : null;
            if (!tokens.Check(bot, token, out var routine, out var assignment))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("""{"error":"turn_token","message":"The turn token is missing or from an earlier turn. Claim a new one."}""").ConfigureAwait(false);
                return;
            }
            JsonObject body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body).ConfigureAwait(false) as JsonObject ?? throw new JsonException(); }
            catch (JsonException)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("""{"error":"invalid_json"}""").ConfigureAwait(false);
                return;
            }
            var name = body["name"]?.GetValue<string>() ?? "";
            var args = body["arguments"] as JsonObject ?? [];
            var result = await tools.CallAsync(new ToolCaller(bot, routine, assignment), name, args, ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.StatusCode = result.Status;
            await ctx.Response.WriteAsync(result.ToJson().ToJsonString()).ConfigureAwait(false);
            return;
        }
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("""{"error":"not_found"}""").ConfigureAwait(false);
    }

    /// <summary>Starts a listener on the bot's socket if one is not running.</summary>
    public async Task EnsureListeningAsync(string bot, string socketPath, CancellationToken ct)
    {
        if (_apps.ContainsKey(bot)) return;
        SecureFile.EnsureDirNoClobber(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath)) File.Delete(socketPath);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath));
        var app = builder.Build();
        app.Run(ctx => HandleAsync(ctx, bot));
        await app.StartAsync(ct).ConfigureAwait(false);
        // The container user must reach the socket; the folder stays private to the roster user and that bot's mount.
        if (SecureFile.IsUnix) File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        _apps[bot] = app;
    }

    public async Task StopAsync(string bot)
    {
        if (_apps.Remove(bot, out var app)) await app.DisposeAsync().ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        foreach (var b in _apps.Keys.ToList()) await StopAsync(b).ConfigureAwait(false);
    }
}
