// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Omabot.Common;

namespace Omabot.Gateway;

/// <summary>
/// Phase 1 gateway: checks the per-bot token, swaps the model nickname for
/// the upstream model, adds the upstream key and streams the answer back.
/// Bots never see the upstream key. Phase 2 adds the queue (S lanes, four
/// priorities, round robin, per-bot cap), label routing and usage metering
/// when <see cref="Backends"/> is set.
/// </summary>
public class PassThroughGateway(
    Func<IReadOnlyDictionary<string, string>> tokenHashes,
    Func<IReadOnlyDictionary<string, ModelRoute>> routes,
    Func<string, string?> readKeyFile,
    HttpClient upstream,
    IClock clock,
    bool allowPlainHttp,
    TimeSpan unreachableAfter)
{
    /// <summary>Backends with queues and label routing. Null runs the phase 1 pass-through.</summary>
    public BackendRegistry? Backends { get; init; }
    public TurnContexts? Contexts { get; init; }
    public UsageMeter? Usage { get; init; }
    public Priority FallbackPriority { get; init; } = Priority.Assignment;

    private DateTimeOffset? _failingSince;
    private DateTimeOffset? _lastOk;

    public bool InferenceUnreachable => _failingSince is { } since && clock.UtcNow - since >= unreachableAfter;
    public DateTimeOffset? LastOk => _lastOk;

    public string? Authenticate(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = auth["Bearer ".Length..].Trim();
        if (token.Length < 16) return null;
        return tokenHashes().TryGetValue(SecureFile.Sha256Hex(token), out var slug) ? slug : null;
    }

    public async Task HandleAsync(HttpContext ctx)
    {
        var slug = Authenticate(ctx);
        if (slug is null)
        {
            await Error(ctx, 401, "unauthorized", "Missing or unknown bot token.").ConfigureAwait(false);
            return;
        }
        await ServeAsync(ctx, slug, null, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves one authenticated request. <paramref name="forced"/> is only set by
    /// roster code running in this process (memory upkeep), never from a header.
    /// </summary>
    private async Task ServeAsync(HttpContext ctx, string slug, Priority? forced, string? forcedRoutine)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (ctx.Request.Method == "GET" && path.EndsWith("/models", StringComparison.Ordinal))
        {
            var data = new JsonArray([.. routes().Keys.Select(k => (JsonNode)new JsonObject { ["id"] = k, ["object"] = "model", ["owned_by"] = "oma-roster" })]);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString()).ConfigureAwait(false);
            return;
        }
        if (ctx.Request.Method != "POST" || !(path.EndsWith("/chat/completions", StringComparison.Ordinal) || path.EndsWith("/completions", StringComparison.Ordinal)))
        {
            await Error(ctx, 404, "not_found", "Only /v1/chat/completions and /v1/models are served.").ConfigureAwait(false);
            return;
        }

        JsonObject body;
        try
        {
            body = (await JsonNode.ParseAsync(ctx.Request.Body).ConfigureAwait(false)) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            await Error(ctx, 400, "invalid_json", "The request body is not a JSON object.").ConfigureAwait(false);
            return;
        }
        var nick = body["model"]?.GetValue<string>() ?? "";
        if (!routes().TryGetValue(nick, out var route))
        {
            await Error(ctx, 400, "unknown_model", $"Model \"{nick}\" is not known to the gateway.").ConfigureAwait(false);
            return;
        }
        if (Backends is null)
        {
            body["model"] = route.UpstreamModel;
            await ForwardAsync(ctx, route, body, path, null).ConfigureAwait(false);
            return;
        }

        var backend = Backends.Pick(route);
        if (backend is null)
        {
            await Error(ctx, 503, "no_backend", $"No backend has the labels {string.Join(", ", route.Labels)} that model \"{nick}\" needs.").ConfigureAwait(false);
            return;
        }
        var turn = forced is { } fp ? new TurnContext(fp, forcedRoutine) : Contexts?.For(slug) ?? new TurnContext(FallbackPriority, null);
        var started = clock.UtcNow;
        GatewayLease lease;
        try
        {
            lease = await backend.Queue.EnterAsync(slug, turn.Priority, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (QueueDroppedException ex)
        {
            await Error(ctx, 429, "queue_full", ex.Message).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return; // the bot gave up while waiting
        }
        using (lease)
        {
            var model = backend.ModelFor(route);
            body["model"] = model;
            if (backend.Settings.SlotAffinity && backend.Settings.Kind == "llama") body["id_slot"] = lease.Lane;
            var effective = route with { Backend = backend.Name, Endpoint = backend.Settings.Endpoint, KeyFile = backend.Settings.KeyFile, UpstreamModel = model };
            var sniffer = new UsageSniffer();
            var status = await ForwardAsync(ctx, effective, body, path, sniffer).ConfigureAwait(false);
            sniffer.Finish();
            Usage?.Record(new UsageRecord(slug, turn.Routine, turn.Priority, backend.Name, model, sniffer.PromptTokens, sniffer.CompletionTokens, sniffer.Estimated,
                (long)lease.Waited.TotalMilliseconds, (long)(clock.UtcNow - started).TotalMilliseconds, status));
        }
    }

    /// <summary>Forwards one request and streams the answer back unchanged. Returns the status sent to the bot.</summary>
    protected async Task<int> ForwardAsync(HttpContext ctx, ModelRoute route, JsonObject body, string path, UsageSniffer? sniffer)
    {
        var policy = UpstreamPolicy.Check(route.Endpoint, allowPlainHttp);
        if (policy is not null)
        {
            await Error(ctx, 502, "upstream_refused", policy).ConfigureAwait(false);
            return 502;
        }
        var suffix = path.EndsWith("/chat/completions", StringComparison.Ordinal) ? "/chat/completions" : "/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, route.Endpoint.TrimEnd('/') + suffix)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        var key = readKeyFile(route.KeyFile);
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage resp;
        try
        {
            resp = await upstream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
        {
            _failingSince ??= clock.UtcNow;
            await Error(ctx, 503, "inference_unreachable", "Inference upstream is unreachable: " + Redactor.Shared.Redact(ex.Message)).ConfigureAwait(false);
            return 503;
        }
        using (resp)
        {
            if ((int)resp.StatusCode >= 500) _failingSince ??= clock.UtcNow;
            else { _failingSince = null; _lastOk = clock.UtcNow; }
            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            await using var s = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            var buf = new byte[8192];
            int n;
            while ((n = await s.ReadAsync(buf, ctx.RequestAborted).ConfigureAwait(false)) > 0)
            {
                sniffer?.Feed(buf.AsSpan(0, n));
                await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
            return (int)resp.StatusCode;
        }
    }

    /// <summary>
    /// A non-streaming chat call made by the roster itself on a bot's behalf
    /// (memory upkeep). It goes through the same queue, routing and metering
    /// as the bot's own calls, at the priority given. Returns the reply text,
    /// or null when the call was dropped or failed.
    /// </summary>
    public async Task<string?> InternalCompleteAsync(string slug, string modelNick, Priority priority, string routine, string system, string user, int maxTokens, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = modelNick,
            ["stream"] = false,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/v1/chat/completions";
        ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()));
        var resp = new MemoryStream();
        ctx.Response.Body = resp;
        ctx.RequestAborted = ct;
        await ServeAsync(ctx, slug, priority, routine).ConfigureAwait(false);
        if (ctx.Response.StatusCode != 200) return null;
        try
        {
            var node = JsonNode.Parse(resp.ToArray());
            return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static async Task Error(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString()).ConfigureAwait(false);
    }

}
