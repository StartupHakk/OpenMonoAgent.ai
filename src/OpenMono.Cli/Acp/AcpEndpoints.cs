using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenMono.Config;
using OpenMono.HostBridge;
using OpenMono.Llm;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Acp;






public static class AcpEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/discovery", GetDiscovery);
        app.MapGet("/api/v1/sessions", GetSessions);
        app.MapPost("/api/v1/sessions", PostSession);
        app.MapGet("/api/v1/sessions/{id}", GetSession);
        app.MapGet("/api/v1/sessions/{id}/messages", GetMessages);
        app.MapPost("/api/v1/sessions/{id}/turn", PostTurn);
        app.MapPost("/api/v1/sessions/{id}/host", ToggleHost);
        app.MapDelete("/api/v1/sessions/{id}", DeleteSession);

        app.MapGet("/api/v1/sessions/{id}/diffs", GetDiffs);
        app.MapGet("/api/v1/sessions/{id}/diffs/{toolCallId}", GetDiff);
        app.MapPost("/api/v1/sessions/{id}/diffs/undo", UndoDiff);
        app.MapPost("/api/v1/sessions/{id}/diffs/redo", RedoDiff);

        // Backend (inference endpoint) management — session-scoped hot-swap.
        app.MapGet("/api/v1/backends", GetBackends);
        app.MapPost("/api/v1/backends", PostBackend);
        app.MapDelete("/api/v1/backends/{name}", DeleteBackend);
        app.MapPost("/api/v1/sessions/{id}/backend", SetSessionBackend);
    }



    private static IResult GetDiscovery(AcpLockFileWriter lockfile)
    {
        var uptime = (int)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;
        return Results.Ok(new
        {
            version = "1.0.0",
            agent_id = lockfile.AgentId,
            host_workspace = lockfile.HostWorkspace,
            container_workspace = lockfile.ContainerWorkspace,
            status = "ready",
            uptime_seconds = uptime,
        });
    }



    private static IResult GetSessions(AcpSessionStore store)
    {
        var digests = store.List()
            .Select(s => new SessionDigestDto
            {
                SessionId = s.Id,
                Title = string.IsNullOrEmpty(s.Title) ? s.FirstMessage : s.Title,
                StartedAt = s.StartedAt.ToString("o"),
                LastActivityAt = s.LastActivityAt.ToString("o"),
                TurnCount = s.TurnCount,
                Model = s.Model,
                MessageCount = s.MessageCount,
                Summary = s.LatestSummary,
            })
            .ToList();
        return Results.Ok(new SessionsEnvelope { Sessions = digests });
    }



    private static async Task<IResult> PostSession(HttpContext ctx, AcpSessionStore store, AppConfig config)
    {
        CreateSessionBody? body = null;
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var raw = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (!string.IsNullOrWhiteSpace(raw))
                body = JsonSerializer.Deserialize<CreateSessionBody>(raw, JsonDefaults);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", detail = ex.Message });
        }

        var session = store.Create(body?.Model, config, backendName: body?.Backend);
        return Results.Ok(new
        {
            session_id = session.Id,
            model = session.Model,
            active_backend = session.State.ActiveBackend,
        });
    }



    private static IResult GetSession(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();
        Log.Info($"[OMA_MODE] GET session={id}: plan_mode={session.PlanMode} (frontend is pulling current mode)");
        var thinkingProfile = store.ResolveReasoning(session.Model);
        var thinkingLevel = session.State.Meta.ThinkingLevel ?? thinkingProfile.DefaultLevel;
        var thinkingEnabled = session.State.Meta.ThinkingLevel is null
            ? thinkingProfile.DefaultEnabled && thinkingProfile.DefaultLevel != "off"
            : session.State.Meta.ThinkingEnabled;
        return Results.Ok(new
        {
            session_id = session.Id,
            model = session.Model,
            active_backend = session.State.ActiveBackend,
            started_at = session.StartedAt.ToString("o"),
            turn_count = session.TurnCount,
            plan_mode = session.PlanMode,
            host_enabled = session.State.Meta.HostEnabled,
            thinking_level = thinkingLevel,
            thinking_enabled = thinkingEnabled,
            thinking_levels = thinkingProfile.Kind == ReasoningKind.EffortLevels ? thinkingProfile.Levels : ["off", "on"],
            thinking_description = thinkingProfile.Kind == ReasoningKind.EffortLevels ? ThinkingLevels.Describe(thinkingLevel) : "",
        });
    }



    private static IResult GetMessages(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();
        return Results.Ok(new MessagesEnvelope { Messages = ProjectMessages(session.Messages) });
    }



    /// <summary>
    /// Host-mode toggle for a sandbox session (<c>{"action":"on"|"off"}</c>).
    /// The editor/extension equivalent of the TUI's <c>/host on|off</c>: on
    /// pairs with the host daemon and injects the standing orders, off revokes
    /// the pairing and strips them. Wire contract: 200 with
    /// <c>{ ok, host_enabled, run_as?, sudo?, error? }</c>; 404 unknown session;
    /// 400 malformed body or unknown action. Pairing failures are NOT 4xx —
    /// they come back as <c>ok:false</c> with a human-readable <c>error</c>
    /// so the client can render guidance (start the daemon, check the secret).
    /// </summary>
    private static async Task<IResult> ToggleHost(
        HttpContext ctx,
        string id,
        AcpSessionStore store,
        CancellationToken ct)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();

        string action;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            action = doc.RootElement.TryGetProperty("action", out var aEl) ? aEl.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", detail = ex.Message });
        }

        action = action.Trim().ToLowerInvariant();
        if (action is not ("on" or "off"))
            return Results.BadRequest(new { error = "invalid_action", detail = "action must be \"on\" or \"off\"" });

        if (action == "on")
        {
            var gateway = HostGatewayRegistry.Get(session.Id);
            if (gateway is { IsPaired: true } && !await gateway.IsPairingLiveAsync(ct))
            {
                // Stale token (daemon restarted): the local flag says ON but
                // the daemon would reject every exec. Drop it and re-pair
                // below instead of no-opping on the dead pairing.
                HostGatewayRegistry.Unregister(session.Id);
                gateway.Dispose();
                gateway = null;
            }
            if (gateway is not { IsPaired: true })
            {
                var (paired, error) = await HostGatewayBootstrap.TryPairAsync(session.State, ct);
                if (paired is null)
                    return Results.Ok(new { ok = false, host_enabled = false, error = error ?? "pairing failed" });
                gateway = paired;
            }

            session.State.Meta.HostEnabled = true;
            InjectHostStandingOrders(session, gateway);
            store.Save(session);
            return Results.Ok(new
            {
                ok = true,
                host_enabled = true,
                run_as = gateway.RunAs,
                sudo = gateway.Sudo.ToString().ToLowerInvariant(),
            });
        }

        // off: idempotent — an unpaired session is already sandboxed.
        var existing = HostGatewayRegistry.Get(session.Id);
        if (existing is { IsPaired: true })
        {
            await existing.UnpairAsync(ct);
            HostGatewayRegistry.Unregister(session.Id);
            existing.Dispose();
        }
        session.State.Meta.HostEnabled = false;
        StripHostStandingOrders(session);
        store.Save(session);
        return Results.Ok(new { ok = true, host_enabled = false });
    }

    private static void InjectHostStandingOrders(AcpSession session, HostGateway gateway)
    {
        var orders = gateway.BuildStandingOrders();
        if (orders is null) return;
        // Same marker guard as the first-turn path in AcpTurnRunner: when no
        // system message exists yet, the next turn injects the orders itself.
        var index = session.Messages.FindIndex(m => m.Role == MessageRole.System);
        if (index < 0) return;
        var system = session.Messages[index];
        if (system.Content is not null && system.Content.Contains(HostStandingOrders.Marker)) return;
        session.Messages[index] = system with { Content = system.Content + "\n\n" + orders };
    }

    private static void StripHostStandingOrders(AcpSession session)
    {
        // The standing orders are always appended as the last block of the
        // system message, so cutting at the marker removes exactly them.
        for (var i = 0; i < session.Messages.Count; i++)
        {
            var m = session.Messages[i];
            if (m.Role != MessageRole.System || m.Content is null) continue;
            var cut = m.Content.IndexOf(HostStandingOrders.Marker, StringComparison.Ordinal);
            if (cut < 0) continue;
            session.Messages[i] = m with { Content = m.Content[..cut].TrimEnd() };
        }
    }



    private static async Task PostTurn(
        HttpContext ctx,
        string id,
        AcpSessionStore store,
        AcpTurnRunnerFactory runners)
    {
        var session = store.TryGet(id);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Parse the body before taking the turn lock: an abort must cancel the
        // in-flight turn without needing the lock (the lock is held by the turn
        // it is trying to stop, so requiring it would deadlock into 409).
        JsonDocument body;
        try
        {
            body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json", detail = ex.Message }, ctx.RequestAborted);
            return;
        }

        using (body)
        {
            var root = body.RootElement;

            if (root.TryGetProperty("abort", out var abortEl) && abortEl.GetBoolean())
            {
                // Reusable turn-wide stop: cancels the registered turn CTS (LLM
                // stream + tool execution) and drops pending pauses. Works even
                // while a turn holds TurnLock.
                session.CancelTurn();
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                session.LastActivityAt = DateTime.UtcNow;
                store.Save(session);
                return;
            }

            if (!await session.TurnLock.WaitAsync(0, ctx.RequestAborted))
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsJsonAsync(new { error = "session_busy" }, ctx.RequestAborted);
                return;
            }

            // Turn-wide CTS: linked to the SSE disconnect so a dropped connection
            // aborts the turn, and registered on the session so abort:true /
            // session delete can cancel it from outside the lock.
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            session.RegisterTurn(turnCts);
            var ct = turnCts.Token;

            try
            {
                if (root.TryGetProperty("message", out var msgEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.RunUserMessageAsync(msgEl.GetString() ?? "", ct);
                }
                else if (root.TryGetProperty("permission", out var permEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithPermissionAsync(permEl, ct);
                }
                else if (root.TryGetProperty("user_input", out var uinEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithUserInputAsync(uinEl, ct);
                }
                else if (root.TryGetProperty("playbookPermission", out var pbkEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithPlaybookApprovalAsync(pbkEl, ct);
                }
                else if (root.TryGetProperty("plan_decision", out var pdEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithPlanDecisionAsync(pdEl.GetString() ?? "keep", ct);
                }
                else if (root.TryGetProperty("toggle_mode", out var tmEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithToggleModeAsync(tmEl, ct);
                }
                else if (root.TryGetProperty("mode", out var modeEl))
                {
                    var mode = modeEl.GetString() ?? "";
                    var isPlanMode = mode.Equals("plan", StringComparison.OrdinalIgnoreCase);
                    var wasPlanMode = session.PlanMode;
                    session.PlanMode = isPlanMode;
                    Log.Info($"[OMA_MODE] TOGGLE session={id}: PlanMode {wasPlanMode} → {isPlanMode} (frontend requested '{mode}')");
                    // On an actual change, drop a one-time notice into the conversation so the
                    // agent's next turn registers the switch (not just the static per-turn banner).
                    if (wasPlanMode != isPlanMode)
                        session.Messages.Add(new OpenMono.Session.Message
                        {
                            Role = OpenMono.Session.MessageRole.User,
                            Content = isPlanMode ? ModeInstructions.SwitchedToPlan : ModeInstructions.SwitchedToBuild,
                        });
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    await ctx.Response.WriteAsJsonAsync(new { mode = isPlanMode ? "plan" : "build" }, ctx.RequestAborted);
                }
                else
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(
                        new
                        {
                            error = "invalid_body",
                            detail = "body must contain `message`, `permission`, `playbookPermission`, `user_input`, `mode`, or `abort:true`",
                        },
                        ctx.RequestAborted);
                }
            }
            catch (InvalidOperationException ex)
            {
                if (ctx.Response.HasStarted)
                {
                    var writer = new SseWriter(ctx.Response.Body, ctx.RequestAborted);
                    await writer.WriteEventAsync("error", new { message = ex.Message });
                }
                else
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = "resume_error", detail = ex.Message }, ctx.RequestAborted);
                }
            }
            finally
            {
                session.ClearTurn();
                session.LastActivityAt = DateTime.UtcNow;
                store.Save(session);
                session.TurnLock.Release();
            }
        }
    }



    private static IResult DeleteSession(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NoContent();
        session.CancelTurn();
        store.Delete(id);
        return Results.NoContent();
    }

    // ---- Per-session file diffs (staged under <DataDirectory>/sessions/<id>/diffs/) ----
    // The extension renders these in an in-memory diff provider; it never opens file://
    // paths into the user's repo. before = original project file, after = agent's change.

    private static IResult GetDiffs(string id, AcpSessionStore store)
    {
        if (store.TryGet(id) is null) return Results.NotFound();
        var stager = store.GetDiffStager(id);
        return Results.Ok(new DiffsEnvelope { Diffs = stager.List().Select(ToDiffDto).ToList() });
    }

    private static IResult GetDiff(string id, string toolCallId, AcpSessionStore store)
    {
        if (store.TryGet(id) is null) return Results.NotFound();
        var stager = store.GetDiffStager(id);
        var rec = stager.Get(toolCallId);
        if (rec is null) return Results.NotFound();
        var content = stager.GetContent(toolCallId);
        return Results.Ok(new
        {
            tool_call_id = toolCallId,
            file_path = rec.FilePath,
            is_creation = rec.IsCreation,
            status = rec.Status,
            before = content?.Before,
            after = content?.After,
        });
    }

    private static IResult UndoDiff(string id, AcpSessionStore store)
    {
        if (store.TryGet(id) is null) return Results.NotFound();
        var path = store.GetDiffStager(id).Undo();
        return Results.Ok(new { ok = path is not null, file_path = path ?? "" });
    }

    private static IResult RedoDiff(string id, AcpSessionStore store)
    {
        if (store.TryGet(id) is null) return Results.NotFound();
        var path = store.GetDiffStager(id).Redo();
        return Results.Ok(new { ok = path is not null, file_path = path ?? "" });
    }

    // ── Backend (inference endpoint) management ──────────────────────────────

    /// <summary>
    /// Lists all configured inference backends. Returns names, endpoints, default
    /// models, and whether a key is currently stored in memory. Never exposes key values.
    /// </summary>
    private static IResult GetBackends(BackendRegistry registry)
    {
        var list = registry.All
            .Select(b => new
            {
                name = b.Name,
                endpoint = b.Endpoint,
                nickname = b.Nickname,
                default_model = b.DefaultModel,
                provider_type = b.ProviderType,
                is_local = b.IsLocal,
                has_key = registry.HasKey(b.Name),
            })
            .ToList();

        return Results.Ok(new { backends = list });
    }

    /// <summary>
    /// Creates or updates a backend. The body may include <c>api_key</c>; if present it is
    /// stored in-memory only (never persisted to disk). Wire contract: 200 with
    /// <c>{ ok, name, has_key }</c>; 400 malformed body.
    /// </summary>
    private static async Task<IResult> PostBackend(
        HttpContext ctx,
        BackendRegistry registry,
        LlmClientPool clientPool,
        AppConfig config,
        CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
            var endpoint = root.TryGetProperty("endpoint", out var eEl) ? eEl.GetString() ?? "" : "";
            var apiKey = root.TryGetProperty("api_key", out var kEl) ? kEl.GetString() : null;
            var defaultModel = root.TryGetProperty("default_model", out var dmEl) ? dmEl.GetString() : null;
            var providerType = root.TryGetProperty("provider_type", out var ptEl) ? ptEl.GetString() ?? "openai-compat" : "openai-compat";
            var isLocal = root.TryGetProperty("is_local", out var ilEl) ? ilEl.GetBoolean() : false;
            var nickname = root.TryGetProperty("nickname", out var nkEl) ? nkEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "invalid_name", detail = "name is required" });
            if (string.IsNullOrWhiteSpace(endpoint))
                return Results.BadRequest(new { error = "invalid_endpoint", detail = "endpoint is required" });

            registry.Add(new BackendDefinition
            {
                Name = name,
                Endpoint = endpoint,
                DefaultModel = defaultModel,
                ProviderType = providerType,
                IsLocal = isLocal,
                Nickname = string.IsNullOrEmpty(nickname) ? null : nickname,
            });

            if (!string.IsNullOrEmpty(apiKey))
            {
                registry.SetKey(name, apiKey);
            }

            // Invalidate any cached client so the new config takes effect on the next turn.
            clientPool.Invalidate(name);

            // Persist so the backend (and any supplied key) survives a restart.
            TrySaveBackends(registry, config);

            return Results.Ok(new
            {
                ok = true,
                name = name,
                endpoint = endpoint,
                has_key = registry.HasKey(name),
            });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", detail = ex.Message });
        }
    }

    /// <summary>
    /// Removes a backend and its in-memory key. 204 on success; 404 if unknown.
    /// </summary>
    private static IResult DeleteBackend(string name, BackendRegistry registry, LlmClientPool clientPool, AppConfig config)
    {
        if (!registry.Has(name)) return Results.NotFound();
        registry.Remove(name);
        clientPool.Invalidate(name);
        TrySaveBackends(registry, config);
        return Results.NoContent();
    }

    /// <summary>
    /// Persists the registry to <c>backends.json</c> best-effort — a write failure must not
    /// fail a live API request, so errors are logged, not thrown.
    /// </summary>
    private static void TrySaveBackends(BackendRegistry registry, AppConfig config)
    {
        try
        {
            registry.SaveToFile(Path.Combine(config.DataDirectory, "backends.json"));
        }
        catch (Exception ex)
        {
            Log.Warn($"BackendRegistry: could not persist backends.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Switches a session's active backend. The body may include <c>api_key</c>; if present
    /// it is stored in-memory only. The backend name must exist (or be "default").
    /// Wire contract: 200 with <c>{ ok, backend, has_key }</c>; 404 unknown session;
    /// 400 unknown backend.
    /// </summary>
    private static async Task<IResult> SetSessionBackend(
        HttpContext ctx,
        string id,
        AcpSessionStore store,
        BackendRegistry registry,
        LlmClientPool clientPool,
        CancellationToken ct)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();

        string backendName;
        string? apiKey;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;

            backendName = root.TryGetProperty("backend", out var bEl) ? bEl.GetString() ?? "" : "";
            apiKey = root.TryGetProperty("api_key", out var kEl) ? kEl.GetString() : null;
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", detail = ex.Message });
        }

        if (string.IsNullOrWhiteSpace(backendName))
            return Results.BadRequest(new { error = "invalid_backend", detail = "backend is required" });

        var name = backendName.Trim();

        // The backend must already exist (created via POST /api/v1/backends or seeded at
        // startup). Accepts a backend name or its nickname.
        var resolved = registry.Resolve(name);
        if (resolved is null)
            return Results.BadRequest(new { error = "unknown_backend", detail = $"backend '{name}' not found. Use GET /api/v1/backends to list." });
        name = resolved.Name;

        var def = registry.Get(name)!;

        // If a key was provided, store it in memory.
        if (!string.IsNullOrEmpty(apiKey))
        {
            registry.SetKey(name, apiKey);
        }

        // If no key is stored and the backend is not local, reject.
        if (!registry.HasKey(name) && !def.IsLocal)
        {
            return Results.Ok(new { ok = false, backend = name, has_key = false, error = "no_api_key" });
        }

        clientPool.Invalidate(name);

        session.State.ActiveBackend = name;
        // Adopt the backend's default model only if the session has no explicit model yet.
        if (string.IsNullOrWhiteSpace(session.State.Model))
            session.State.Model = def.DefaultModel;

        store.Save(session);

        return Results.Ok(new
        {
            ok = true,
            backend = name,
            model = session.State.Model,
            has_key = registry.HasKey(name),
        });
    }

    private static DiffDto ToDiffDto(OpenMono.Session.DiffRecord r) => new()
    {
        ToolCallId = r.ToolCallId,
        FilePath = r.FilePath,
        IsCreation = r.IsCreation,
        Status = r.Status,
        Timestamp = r.Timestamp.ToString("o"),
    };



    private static void StartSseResponse(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
    }







    internal static List<HistoryMessageDto> ProjectMessages(IReadOnlyList<Message> messages)
    {
        var toolById = new Dictionary<string, Message>();
        foreach (var m in messages)
            if (m.Role == MessageRole.Tool && m.ToolCallId is { } id)
                toolById[id] = m;

        var result = new List<HistoryMessageDto>();
        foreach (var m in messages)
        {
            if (m.Role == MessageRole.Tool || m.Role == MessageRole.System) continue;

            var role = m.Role switch
            {
                MessageRole.System => "system",
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                _ => m.Role.ToString().ToLowerInvariant(),
            };

            List<HistoryToolCallDto>? toolCalls = null;
            if (m.Role == MessageRole.Assistant && m.ToolCalls is { Count: > 0 } calls)
            {
                toolCalls = new List<HistoryToolCallDto>(calls.Count);
                foreach (var call in calls)
                {
                    toolById.TryGetValue(call.Id, out var toolMsg);
                    toolCalls.Add(new HistoryToolCallDto
                    {
                        Id = call.Id,
                        Name = call.Name,
                        Summary = TruncateForUi(call.Arguments, 120),
                        Ok = ToolResultLooksOk(toolMsg),
                        Preview = toolMsg?.Content,
                    });
                }
            }

            result.Add(new HistoryMessageDto
            {
                Role = role,
                Content = m.Content ?? "",
                Timestamp = m.Timestamp.ToString("o"),
                ToolCalls = toolCalls,
                Thinking = m.ThinkingContent,
            });
        }
        return result;
    }

    private static bool ToolResultLooksOk(Message? toolMsg)
    {
        if (toolMsg?.Content is null) return false;
        var content = toolMsg.Content;
        if (content.StartsWith("Permission denied", StringComparison.OrdinalIgnoreCase)) return false;
        if (content.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) return false;
        if (content.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string TruncateForUi(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var trimmed = s.Trim();
        if (trimmed.Length <= max) return trimmed;
        return trimmed[..max] + "...";
    }

    private static readonly JsonSerializerOptions JsonDefaults = new(JsonSerializerDefaults.Web);



    internal sealed record HistoryMessageDto
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
        [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }
        [JsonPropertyName("toolCalls")] public List<HistoryToolCallDto>? ToolCalls { get; init; }
        [JsonPropertyName("thinking")] public string? Thinking { get; init; }
    }

    internal sealed record HistoryToolCallDto
    {
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("summary")] public required string Summary { get; init; }
        [JsonPropertyName("ok")] public required bool Ok { get; init; }
        [JsonPropertyName("preview")] public string? Preview { get; init; }
    }

    private sealed class MessagesEnvelope
    {
        [JsonPropertyName("messages")]
        public List<HistoryMessageDto> Messages { get; set; } = new();
    }

    private sealed class SessionsEnvelope
    {
        [JsonPropertyName("sessions")]
        public List<SessionDigestDto> Sessions { get; set; } = new();
    }

    internal sealed record DiffDto
    {
        [JsonPropertyName("tool_call_id")] public required string ToolCallId { get; init; }
        [JsonPropertyName("file_path")] public required string FilePath { get; init; }
        [JsonPropertyName("is_creation")] public bool IsCreation { get; init; }
        [JsonPropertyName("status")] public required string Status { get; init; }
        [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }
    }

    private sealed class DiffsEnvelope
    {
        [JsonPropertyName("diffs")]
        public List<DiffDto> Diffs { get; set; } = new();
    }

    internal sealed record SessionDigestDto
    {
        [JsonPropertyName("session_id")] public required string SessionId { get; init; }
        [JsonPropertyName("title")] public required string Title { get; init; }
        [JsonPropertyName("started_at")] public required string StartedAt { get; init; }
        [JsonPropertyName("last_activity_at")] public required string LastActivityAt { get; init; }
        [JsonPropertyName("turn_count")] public int TurnCount { get; init; }
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("message_count")] public int MessageCount { get; init; }
        [JsonPropertyName("summary")] public string? Summary { get; init; }
    }

    private sealed class CreateSessionBody
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("backend")] public string? Backend { get; set; }
    }
}
