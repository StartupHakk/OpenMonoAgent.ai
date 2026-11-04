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
    /// <summary>Search and scrape on the inference server for bots (plan 7.7). Null serves neither.</summary>
    public GatewayWeb? Web { get; init; }
    /// <summary>Tool limit, output cap, spill and repeat notes (plan 7.8). Null leaves requests as they are.</summary>
    public RequestShaper? Shaper { get; init; }
    /// <summary>Repairs tool calls in replies to requests that offered tools (plan 7.8).</summary>
    public bool RepairToolCalls { get; init; }
    /// <summary>Gateway events: (bot, kind, detail), for example tool_args_unreadable or repeat_streak.</summary>
    public Action<string, string, string>? OnEvent { get; init; }

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
        if (Web is not null && (path.EndsWith("/services", StringComparison.Ordinal) || path.EndsWith("/search", StringComparison.Ordinal) || path.EndsWith("/scrape", StringComparison.Ordinal)))
        {
            await ServeWebAsync(ctx, slug, path, Web).ConfigureAwait(false);
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
        if (Shaper is not null)
        {
            var shaped = Shaper.Apply(body, slug, route.ContextSize);
            if (shaped.ToolsTrimmed > 0) OnEvent?.Invoke(slug, "tools_trimmed", $"{shaped.ToolsTrimmed} tool(s) over the limit were left out");
            if (shaped.Spilled > 0) OnEvent?.Invoke(slug, "tool_result_spilled", $"{shaped.Spilled} long tool result(s) spilled to files");
            if (shaped.RepeatNote is not null) OnEvent?.Invoke(slug, "repeat_streak", shaped.RepeatNote);
        }
        if (Backends is null)
        {
            body["model"] = route.UpstreamModel;
            await ForwardAsync(ctx, route, body, path, null, slug).ConfigureAwait(false);
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
            var status = await ForwardAsync(ctx, effective, body, path, sniffer, slug).ConfigureAwait(false);
            sniffer.Finish();
            Usage?.Record(new UsageRecord(slug, turn.Routine, turn.Priority, backend.Name, model, sniffer.PromptTokens, sniffer.CompletionTokens, sniffer.Estimated,
                (long)lease.Waited.TotalMilliseconds, (long)(clock.UtcNow - started).TotalMilliseconds, status, turn.Assignment));
        }
    }

    /// <summary>Forwards one request and streams the answer back unchanged. Returns the status sent to the bot.</summary>
    protected async Task<int> ForwardAsync(HttpContext ctx, ModelRoute route, JsonObject body, string path, UsageSniffer? sniffer, string slug = "")
    {
        var policy = UpstreamPolicy.Check(route.Endpoint, allowPlainHttp);
        if (policy is not null)
        {
            await Error(ctx, 502, "upstream_refused", policy).ConfigureAwait(false);
            return 502;
        }
        var suffix = path.EndsWith("/chat/completions", StringComparison.Ordinal) ? "/chat/completions" : "/completions";
        if (RepairToolCalls && suffix == "/chat/completions" && ToolCallRepair.OfferedTools(body) is { Count: > 0 } offered)
            return await ForwardRepairedAsync(ctx, route, body, suffix, sniffer, slug, offered).ConfigureAwait(false);
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

    /// <summary>
    /// The repair path (plan 7.8): the reply is read whole, repaired, and sent
    /// on as one JSON body or a rebuilt SSE stream. An unreadable call is sent
    /// back to the model once with a note; if it is still unreadable it is
    /// turned into text, never dropped without a trace.
    /// </summary>
    private async Task<int> ForwardRepairedAsync(HttpContext ctx, ModelRoute route, JsonObject body, string suffix, UsageSniffer? sniffer, string slug, Dictionary<string, JsonObject?> offered)
    {
        var stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) && st;
        var first = await SendWholeAsync(ctx, route, body, suffix).ConfigureAwait(false);
        if (first is null) return 503;
        sniffer?.Feed(System.Text.Encoding.UTF8.GetBytes(first.Value.Text));
        if (first.Value.Status != 200 || Parse(first.Value.Text, stream) is not { } reply)
        {
            ctx.Response.StatusCode = first.Value.Status;
            ctx.Response.ContentType = first.Value.ContentType;
            await ctx.Response.WriteAsync(first.Value.Text, ctx.RequestAborted).ConfigureAwait(false);
            return first.Value.Status;
        }
        var (stats, unreadable) = ToolCallRepair.Repair(reply, offered);
        if (unreadable.Count > 0)
        {
            OnEvent?.Invoke(slug, "tool_args_unreadable", string.Join(", ", unreadable.Select(c => c.Name)));
            var again = (JsonObject)body.DeepClone();
            var messages = again["messages"] as JsonArray ?? [];
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = (reply.Content ?? "") + string.Concat(unreadable.Select(c => $"\n<tool_call>{{\"name\": \"{c.Name}\", \"arguments\": {c.Arguments}}}</tool_call>")),
            });
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"[roster gateway] Your call to {string.Join(", ", unreadable.Select(c => c.Name).Distinct())} was not run because its arguments are not valid JSON. Send the same call again with valid JSON arguments.",
            });
            again["messages"] = messages;
            var second = await SendWholeAsync(ctx, route, again, suffix).ConfigureAwait(false);
            if (second is { Status: 200 } ok && Parse(ok.Text, stream) is { } retried)
            {
                if (sniffer is not null)
                {
                    var extra = new UsageSniffer();
                    extra.Feed(System.Text.Encoding.UTF8.GetBytes(ok.Text));
                    extra.Finish();
                    sniffer.Add(extra);
                }
                var (stats2, unreadable2) = ToolCallRepair.Repair(retried, offered);
                if (unreadable2.Count > 0)
                {
                    OnEvent?.Invoke(slug, "tool_args_unreadable_after_resend", string.Join(", ", unreadable2.Select(c => c.Name)));
                    ToolCallRepair.Explain(retried, unreadable2);
                }
                reply = retried;
                stats = stats2;
            }
            else
            {
                ToolCallRepair.Explain(reply, unreadable);
            }
        }
        if (stats.Repaired + stats.Recovered + stats.Coerced + stats.Dropped > 0)
            OnEvent?.Invoke(slug, "tool_calls_repaired", $"repaired {stats.Repaired}, recovered {stats.Recovered}, coerced {stats.Coerced}, dropped {stats.Dropped}");
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = stream ? "text/event-stream" : "application/json";
        await ctx.Response.WriteAsync(stream ? ToolCallRepair.ToSse(reply) : ToolCallRepair.ToJson(reply).ToJsonString(), ctx.RequestAborted).ConfigureAwait(false);
        return 200;
    }

    private static AssembledReply? Parse(string text, bool stream)
    {
        try
        {
            if (stream || text.TrimStart().StartsWith("data:", StringComparison.Ordinal)) return ToolCallRepair.FromSse(text);
            return JsonNode.Parse(text) is JsonObject o ? ToolCallRepair.FromJson(o) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One upstream call read to the end (at most 16 MB). Null after writing the error when the upstream is unreachable.</summary>
    private async Task<(int Status, string ContentType, string Text)?> SendWholeAsync(HttpContext ctx, ModelRoute route, JsonObject body, string suffix)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, route.Endpoint.TrimEnd('/') + suffix)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        var key = readKeyFile(route.KeyFile);
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var resp = await upstream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 500) _failingSince ??= clock.UtcNow;
            else { _failingSince = null; _lastOk = clock.UtcNow; }
            await using var s = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[16384];
            int n;
            while ((n = await s.ReadAsync(buf, ctx.RequestAborted).ConfigureAwait(false)) > 0 && ms.Length < 16 * 1024 * 1024)
                ms.Write(buf, 0, n);
            return ((int)resp.StatusCode, resp.Content.Headers.ContentType?.ToString() ?? "application/json", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
        {
            _failingSince ??= clock.UtcNow;
            await Error(ctx, 503, "inference_unreachable", "Inference upstream is unreachable: " + Redactor.Shared.Redact(ex.Message)).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// OMA's web tools ask the gateway first: GET services, GET search
    /// (SearXNG JSON) and POST scrape (Scrapling). Search needs web.search for
    /// the bot; scrape needs web.scrape and a URL whose host is on the bot's
    /// web.allow_hosts, never blocked and never an IP address.
    /// </summary>
    private async Task ServeWebAsync(HttpContext ctx, string slug, string path, GatewayWeb web)
    {
        var spec = web.BotSpec(slug);
        var search = web.SearchUpstream is { Length: > 0 } && spec is { WebSearch: true } && spec.ToolsAllow.Contains("WebSearch");
        var scrape = web.ScrapeUpstream is { Length: > 0 } && spec is { WebScrape: true } && spec.ToolsAllow.Contains("WebFetch");
        if (path.EndsWith("/services", StringComparison.Ordinal))
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(new JsonObject { ["search"] = search, ["scrape"] = scrape }.ToJsonString()).ConfigureAwait(false);
            return;
        }
        if (path.EndsWith("/search", StringComparison.Ordinal))
        {
            if (!search) { await Error(ctx, 403, "search_off", "Web search is off for this bot.").ConfigureAwait(false); return; }
            var q = ctx.Request.Query["q"].ToString();
            if (q.Length is 0 or > 500) { await Error(ctx, 400, "bad_query", "The query must be 1 to 500 characters.").ConfigureAwait(false); return; }
            var target = $"{web.SearchUpstream!.TrimEnd('/')}/search?q={Uri.EscapeDataString(q)}&format=json";
            await ProxyWebAsync(ctx, HttpMethod.Get, target, null, web).ConfigureAwait(false);
            return;
        }
        if (!scrape || ctx.Request.Method != "POST") { await Error(ctx, 403, "scrape_off", "Fetching pages is off for this bot.").ConfigureAwait(false); return; }
        JsonObject body;
        try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { await Error(ctx, 400, "bad_body", "The body must be a JSON object.").ConfigureAwait(false); return; }
        var url = body["url"] is JsonValue uv && uv.TryGetValue<string>(out var us) ? us : "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0)
        { await Error(ctx, 400, "bad_url", "Only http and https URLs can be fetched.").ConfigureAwait(false); return; }
        var host = Egress.AddressPolicy.Normalize(u.IdnHost);
        var refusal = System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _) ? "IP addresses are never on an allowlist"
            : Egress.AddressPolicy.HostBlocked(host, web.BlockedHosts().Concat(Egress.EgressPolicy.AlwaysBlocked)) ? "blocked host"
            : !Egress.AddressPolicy.HostAllowed(host, spec!.AllowHosts) ? "not on this bot's web.allow_hosts"
            : null;
        if (refusal is not null) { await Error(ctx, 403, "url_refused", $"{host}: {refusal}.").ConfigureAwait(false); return; }
        var clean = new JsonObject
        {
            ["url"] = url,
            ["max_length"] = Math.Clamp(body["max_length"] is JsonValue ml && ml.TryGetValue<int>(out var m) ? m : 20000, 100, 200000),
            ["format"] = body["format"] is JsonValue fv && fv.TryGetValue<string>(out var f) && f is "markdown" or "text" or "html" ? f : "markdown",
            ["render"] = body["render"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r,
            ["headless"] = body["headless"] is not JsonValue hv || !hv.TryGetValue<bool>(out var h) || h,
        };
        await ProxyWebAsync(ctx, HttpMethod.Post, $"{web.ScrapeUpstream!.TrimEnd('/')}/scrape", clean, web).ConfigureAwait(false);
    }

    private async Task ProxyWebAsync(HttpContext ctx, HttpMethod method, string target, JsonObject? body, GatewayWeb web)
    {
        if (UpstreamPolicy.Check(target, allowPlainHttp) is { } policy) { await Error(ctx, 502, "upstream_refused", policy).ConfigureAwait(false); return; }
        using var req = new HttpRequestMessage(method, target);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        if (readKeyFile(web.KeyFile) is { Length: > 0 } key) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var resp = await upstream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            await using var s = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            var buf = new byte[8192];
            long total = 0;
            int n;
            while ((n = await s.ReadAsync(buf, ctx.RequestAborted).ConfigureAwait(false)) > 0 && (total += n) <= 4 * 1024 * 1024)
                await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
        {
            await Error(ctx, 503, "web_unreachable", "Search and scrape on the inference server are unreachable: " + Redactor.Shared.Redact(ex.Message)).ConfigureAwait(false);
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

/// <summary>Where bots' search and scrape go, and the bot facts the gateway checks.</summary>
public sealed record GatewayWeb(string? SearchUpstream, string? ScrapeUpstream, string KeyFile, Func<string, Team.BotSpec?> BotSpec, Func<IEnumerable<string>> BlockedHosts);
