using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OpenMono.Config;
using OpenMono.Session;

namespace OpenMono.Acp;

/// <summary>
/// HTTP surface for the ACP server. All endpoints live under <c>/api/v1</c>.
/// Each handler is a single static method so the dependency wiring stays visible
/// in one place and minimal-API parameter injection does the rest.
/// </summary>
public static class AcpEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/discovery", GetDiscovery);
        app.MapPost("/api/v1/sessions", PostSession);
        app.MapGet("/api/v1/sessions/{id}", GetSession);
        app.MapGet("/api/v1/sessions/{id}/messages", GetMessages);
        app.MapPost("/api/v1/sessions/{id}/turn", PostTurn);
        app.MapDelete("/api/v1/sessions/{id}", DeleteSession);
    }

    // ── GET /api/v1/discovery ──────────────────────────────────────────────────

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

    // ── POST /api/v1/sessions ──────────────────────────────────────────────────

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
        return Results.Ok(new { session_id = session.Id, model = session.Model });
    }

    // ── GET /api/v1/sessions/{id} ──────────────────────────────────────────────

    private static IResult GetSession(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();
        return Results.Ok(new
        {
            session_id = session.Id,
            model = session.Model,
            started_at = session.StartedAt.ToString("o"),
            turn_count = session.TurnCount,
            plan_mode = session.PlanMode,
        });
    }

    // ── GET /api/v1/sessions/{id}/messages ─────────────────────────────────────

    private static IResult GetMessages(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NotFound();
        return Results.Ok(new MessagesEnvelope { Messages = ProjectMessages(session.Messages) });
    }

    // ── POST /api/v1/sessions/{id}/turn ────────────────────────────────────────

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

        // 409 if a turn is already streaming on this session. Body parsing happens
        // after the lock check so a client can't queue body work behind a held lock.
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
                else if (root.TryGetProperty("abort", out var abortEl) && abortEl.GetBoolean())
                {
                    // Abort is the one body shape that doesn't stream. It just cancels
                    // pending pauses and returns 204; any concurrently-running turn (held
                    // by the lock) finishes via ctx.RequestAborted on its own request.
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
                            detail = "body must contain `message`, `permission`, `user_input`, or `abort:true`",
                        },
                        ctx.RequestAborted);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            // Resume*Async throws this for unknown ids / kind mismatches. If the SSE
            // response was already started, surface as an `error` SSE event; otherwise
            // return 400. We can't easily detect "already started" mid-stream, so write
            // a structured error and let the writer no-op if headers already shipped.
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

    // ── DELETE /api/v1/sessions/{id} ───────────────────────────────────────────

    private static IResult DeleteSession(string id, AcpSessionStore store)
    {
        var session = store.TryGet(id);
        if (session is null) return Results.NoContent(); // idempotent
        session.CancelAllPending();
        store.Delete(id);
        return Results.NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static void StartSseResponse(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>
    /// Project the persisted Messages list into the shape the extension's
    /// HistoryMessage type expects. Tool messages are folded into the
    /// preceding assistant message's toolCalls array (matched by ToolCallId)
    /// so the chat UI can render them as expandable rows.
    /// </summary>
    internal static List<HistoryMessageDto> ProjectMessages(IReadOnlyList<Message> messages)
    {
        var toolById = new Dictionary<string, Message>();
        foreach (var m in messages)
            if (m.Role == MessageRole.Tool && m.ToolCallId is { } id)
                toolById[id] = m;

        var result = new List<HistoryMessageDto>();
        foreach (var m in messages)
        {
            if (m.Role == MessageRole.Tool) continue;   // folded into the prior assistant

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

    // ── DTOs (camelCase enforced even if global naming policy changes) ─────────

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

    private sealed class CreateSessionBody
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
    }
}
