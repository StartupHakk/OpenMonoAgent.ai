// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace Omabot.Fleet;

/// <summary>
/// The controller's node API (plan 7.21). TLS everywhere with the fleet CA's
/// server certificate; enrollment is the only call without a client
/// certificate and only on the base port. Every other call needs a client
/// certificate from the fleet CA, from an approved node, on that node's own
/// port (each node reaches the controller through its own relay port). It
/// binds loopback: the relay client on this host forwards to it.
/// </summary>
public static class FleetServer
{
    private static async Task<JsonObject> Body(HttpContext ctx)
    {
        try { return await JsonNode.ParseAsync(ctx.Request.Body).ConfigureAwait(false) as JsonObject ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static IResult Reply(FleetReply r) => Results.Json(r.Data ?? new JsonObject { ["message"] = r.Message }, statusCode: r.Status);

    private static long Fencing(JsonObject b) => b["fencing"] is JsonValue v && v.TryGetValue<long>(out var f) ? f : -1;

    public static WebApplication Build(FleetController fleet, X509Certificate2 serverCert, IPAddress ip, int basePort, int maxNodes)
    {
        if (!IPAddress.IsLoopback(ip)) throw new ArgumentException("The fleet controller binds loopback only; the relay client forwards to it.", nameof(ip));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = FleetController.MaxArtifactBytes + 4096;
            for (var i = 0; i <= maxNodes; i++)
                k.Listen(ip, basePort + i, o => o.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = serverCert,
                    ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                    ClientCertificateValidation = (cert, _, _) => fleet.Ca.VerifyClient(cert),
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }));
        });
        var app = builder.Build();

        // Public: the node compares it with the fingerprint it was given before it sends anything.
        app.MapGet("/fleet/v1/ca", (HttpContext ctx) => ctx.Connection.LocalPort != basePort ? Results.NotFound() : Results.Json(new { ca = fleet.Ca.CaPem() }));
        app.MapPost("/fleet/v1/enroll", async (HttpContext ctx) =>
        {
            if (ctx.Connection.LocalPort != basePort) return Results.NotFound();
            var b = await Body(ctx).ConfigureAwait(false);
            return Reply(fleet.Enroll(b["code"]?.GetValue<string>(), b["node"]?.GetValue<string>(), b["csr"]?.GetValue<string>()));
        });

        var api = app.MapGroup("/fleet/v1");
        api.AddEndpointFilter(async (fc, next) =>
        {
            var ctx = fc.HttpContext;
            if (ctx.Request.Path.Equals("/fleet/v1/enroll", StringComparison.Ordinal)) return await next(fc).ConfigureAwait(false);
            if (fleet.GuardProblem() is { } g) return Results.Json(new { message = g }, statusCode: 403);
            var (node, problem) = fleet.NodeFor(ctx.Connection.ClientCertificate, ctx.Connection.LocalPort);
            if (node is null) return Results.Json(new { message = "Refused: " + problem }, statusCode: 403);
            ctx.Items["node"] = node;
            return await next(fc).ConfigureAwait(false);
        });
        static FleetNode N(HttpContext ctx) => (FleetNode)ctx.Items["node"]!;

        api.MapPost("/heartbeat", async (HttpContext ctx) =>
        {
            var b = await Body(ctx).ConfigureAwait(false);
            var token = fleet.Heartbeat(N(ctx), b["version"]?.GetValue<string>(), b["capacity"] is JsonValue c && c.TryGetValue<int>(out var cap) ? cap : 1);
            return Results.Json(new { drain = N(ctx).State == "draining", gateway_token = token });
        });
        api.MapPost("/lease", (HttpContext ctx) =>
        {
            var lease = fleet.Lease(N(ctx));
            return lease is null ? Results.NoContent() : Results.Json(new JsonObject
            {
                ["job_id"] = lease.JobId, ["lease_id"] = lease.LeaseId, ["fencing"] = lease.Fencing, ["expires_at"] = lease.ExpiresAt.ToString("O"), ["spec"] = lease.Spec,
            });
        });
        api.MapPost("/jobs/{id}/renew", async (HttpContext ctx, string id) => Reply(fleet.Renew(N(ctx), id, Fencing(await Body(ctx).ConfigureAwait(false)))));
        api.MapPost("/jobs/{id}/steps/{step}", async (HttpContext ctx, string id, string step) =>
        {
            var b = await Body(ctx).ConfigureAwait(false);
            return Reply(fleet.Step(N(ctx), id, step, b["phase"]?.GetValue<string>() ?? "", Fencing(b), b["external_id"]?.GetValue<string>()));
        });
        api.MapPost("/jobs/{id}/complete", async (HttpContext ctx, string id) =>
        {
            var b = await Body(ctx).ConfigureAwait(false);
            return Reply(fleet.Complete(N(ctx), id, Fencing(b), b["result"] as JsonObject ?? []));
        });
        api.MapPost("/jobs/{id}/secrets", async (HttpContext ctx, string id) => Reply(fleet.Secrets(N(ctx), id, Fencing(await Body(ctx).ConfigureAwait(false)))));
        api.MapPost("/jobs/{id}/artifacts/{name}", async (HttpContext ctx, string id, string name) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms).ConfigureAwait(false);
            var fencing = long.TryParse(ctx.Request.Query["fencing"], out var f) ? f : -1;
            return Reply(fleet.Artifact(N(ctx), id, fencing, name, ms.ToArray(), ctx.Request.Headers["X-Sha256"].ToString()));
        });
        return app;
    }
}
