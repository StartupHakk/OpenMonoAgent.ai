using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenMono.Config;
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
        app.MapDelete("/api/v1/sessions/{id}", DeleteSession);
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

        var session = store.Create(body?.Model, config);
        // Sessions default to plan mode (AcpSessionStore.Create); an explicit plan_mode in the
        // create body overrides in either direction, with the same notice the mode toggle sends.
        if (body?.PlanMode is bool pm && pm != session.PlanMode)
        {
            session.PlanMode = pm;
            session.Messages.Add(new Message
            {
                Role = MessageRole.User,
                Content = pm ? ModeInstructions.SwitchedToPlan : ModeInstructions.SwitchedToBuild,
            });
        }
        return Results.Ok(new { session_id = session.Id, model = session.Model, plan_mode = session.PlanMode });
    }



    private static IResult GetSession(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();
        Log.Info($"[OMA_MODE] GET session={id}: plan_mode={session.PlanMode} (frontend is pulling current mode)");
        return Results.Ok(new
        {
            session_id = session.Id,
            model = session.Model,
            started_at = session.StartedAt.ToString("o"),
            turn_count = session.TurnCount,
            plan_mode = session.PlanMode,
        });
    }



    private static IResult GetMessages(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();
        return Results.Ok(new MessagesEnvelope { Messages = ProjectMessages(session.Messages) });
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



        if (!await session.TurnLock.WaitAsync(0, ctx.RequestAborted))
        {
            ctx.Response.StatusCode = StatusCodes.Status409Conflict;
            await ctx.Response.WriteAsJsonAsync(new { error = "session_busy" }, ctx.RequestAborted);
            return;
        }

        try
        {
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

                if (root.TryGetProperty("message", out var msgEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.RunUserMessageAsync(msgEl.GetString() ?? "", ctx.RequestAborted);
                }
                else if (root.TryGetProperty("permission", out var permEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithPermissionAsync(permEl, ctx.RequestAborted);
                }
                else if (root.TryGetProperty("user_input", out var uinEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithUserInputAsync(uinEl, ctx.RequestAborted);
                }
                else if (root.TryGetProperty("playbookPermission", out var pbkEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithPlaybookApprovalAsync(pbkEl, ctx.RequestAborted);
                }
                else if (root.TryGetProperty("plan_decision", out var pdEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithPlanDecisionAsync(pdEl.GetString() ?? "keep", ctx.RequestAborted);
                }
                else if (root.TryGetProperty("toggle_mode", out var tmEl))
                {
                    StartSseResponse(ctx);
                    var runner = runners.Create(session, new SseWriter(ctx.Response.Body, ctx.RequestAborted));
                    await runner.ResumeWithToggleModeAsync(tmEl, ctx.RequestAborted);
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
                else if (root.TryGetProperty("abort", out var abortEl) && abortEl.GetBoolean())
                {



                    session.CancelAllPending();
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
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
            session.LastActivityAt = DateTime.UtcNow;
            store.Save(session);
            session.TurnLock.Release();
        }
    }



    private static IResult DeleteSession(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NoContent();
        session.CancelAllPending();
        store.Delete(id);
        return Results.NoContent();
    }



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
        [JsonPropertyName("plan_mode")] public bool? PlanMode { get; set; }
    }
}
