// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Omabot.Common;
using Omabot.Containers;

namespace Omabot.Supervision;

/// <summary>
/// The supervisor process: the only holder of the Docker socket. Serves a
/// small API on run/supervisor.sock (0600) to callers with the supervisor
/// token, and re-checks every container spec before Docker sees it.
/// </summary>
public static class SupervisorHost
{
    public static WebApplication Build(string socketPath, string token, ISupervisorClient inner, Action<string>? log = null)
    {
        if (File.Exists(socketPath)) File.Delete(socketPath);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath));
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var given = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..].Trim() : "";
            if (!SecureFile.FixedTimeEquals(given, token))
            {
                ctx.Response.StatusCode = 401;
                return;
            }
            await next(ctx).ConfigureAwait(false);
        });
        app.MapGet("/ping", () => Results.Ok(new { ok = true }));
        app.MapGet("/containers", async (CancellationToken ct) => Results.Json(await inner.ListAsync(ct).ConfigureAwait(false), SocketSupervisorClient.Json));
        app.MapPost("/containers", async (HttpContext ctx) =>
        {
            ContainerSpec? spec;
            try
            {
                spec = await ctx.Request.ReadFromJsonAsync<ContainerSpec>(SocketSupervisorClient.Json, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "invalid spec" });
            }
            if (spec is null) return Results.BadRequest(new { error = "invalid spec" });
            try
            {
                var id = await inner.EnsureRunningAsync(spec, ctx.RequestAborted).ConfigureAwait(false);
                log?.Invoke($"started {spec.Name}");
                return Results.Ok(new { id });
            }
            catch (ContainerEngineException ex)
            {
                log?.Invoke($"refused {spec.Name}: {Redactor.Shared.Redact(ex.Message)}");
                return Results.UnprocessableEntity(new { error = Redactor.Shared.Redact(ex.Message) });
            }
        });
        app.MapPost("/containers/{name}/stop", async (string name, CancellationToken ct) =>
        {
            try
            {
                await inner.StopAsync(name, ct).ConfigureAwait(false);
                return Results.Ok();
            }
            catch (ContainerEngineException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });
        app.MapDelete("/containers/{name}", async (string name, CancellationToken ct) =>
        {
            try
            {
                await inner.RemoveAsync(name, ct).ConfigureAwait(false);
                return Results.Ok();
            }
            catch (ContainerEngineException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });
        app.MapGet("/images/{image}", async (string image, CancellationToken ct) =>
        {
            var id = await inner.ImageIdAsync(image, ct).ConfigureAwait(false);
            return id is null ? Results.NotFound() : Results.Ok(new { id });
        });
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (SecureFile.IsUnix && File.Exists(socketPath)) File.SetUnixFileMode(socketPath, SecureFile.PrivateFile);
        });
        return app;
    }
}
