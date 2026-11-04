// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Acp;
using Omabot.Common;
using Omabot.Db;
using Omabot.Supervision;

namespace Omabot.Runtime;

/// <summary>What one turn should be. The roster writes every field; nothing comes from model text.</summary>
public sealed record TurnRequest(
    string BotSlug,
    string Message,
    string Origin,
    string Session,
    string? Mode,
    IReadOnlyList<string> BlockedTools,
    TimeSpan Timeout,
    Action<SseEvent>? OnEvent = null);

/// <summary>What the roster saw of a turn: tool calls and their errors, never the model's claims.</summary>
public sealed record TurnEvidence(int OkTools, int FailedTools, IReadOnlyList<string> Tools, IReadOnlyList<string> Errors)
{
    public static readonly TurnEvidence None = new(0, 0, [], []);

    public string Summary()
    {
        if (Tools.Count == 0) return "no tool calls";
        var counts = Tools.GroupBy(t => t, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}");
        return $"tool calls: {string.Join(", ", counts)}; {OkTools} ok, {FailedTools} failed";
    }
}

public sealed record TurnReport(
    TurnEnd End,
    string Text,
    AcpPause? Pause,
    string? Error,
    string? SessionId,
    TurnEvidence Evidence,
    bool TimedOut,
    AcpUsage? Usage,
    string? Problem = null)
{
    public static TurnReport Unavailable(string problem, string? session = null)
        => new(TurnEnd.Error, "", null, problem, session, TurnEvidence.None, false, null, problem);
}

/// <summary>
/// Runs one roster-started turn on a bot: makes sure the bot is up, picks the
/// session, sets the mode, writes the turn gate, sends the message and reads
/// the SSE stream to its end, with a time limit that aborts the turn. The
/// caller holds the bot's turn lease for the whole turn.
/// </summary>
public sealed class TurnDriver(RosterDb db, IClock clock, RosterPaths paths, Func<int, AcpClient> acpFor, Func<string, CancellationToken, Task<string?>>? ensureRunning = null)
{
    private readonly BotStore _bots = new(db, clock);

    public RosterDb Db => db;

    public async Task<TurnReport> RunAsync(TurnRequest req, CancellationToken ct)
    {
        var row = _bots.Find(req.BotSlug);
        if (row is null) return TurnReport.Unavailable($"no bot {req.BotSlug}");
        if (!row.Enabled) return TurnReport.Unavailable($"{row.Name} is disabled");
        if (row.State != BotStates.Running)
        {
            if (ensureRunning is null) return TurnReport.Unavailable($"{row.Name} is {row.State}");
            var problem = await ensureRunning(row.Name, ct).ConfigureAwait(false);
            if (problem is not null) return TurnReport.Unavailable(problem);
            row = _bots.Find(req.BotSlug)!;
        }
        var acp = acpFor(row.Port);
        string session;
        try
        {
            session = await SessionFor(row, req.Session, acp, ct).ConfigureAwait(false);
            if (req.Mode is ("plan" or "build") && !await acp.SetModeAsync(session, req.Mode, ct).ConfigureAwait(false))
                return TurnReport.Unavailable($"{row.Name} would not switch to {req.Mode} mode", session);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return TurnReport.Unavailable("the bot's ACP server is not reachable: " + Redactor.Shared.Redact(ex.Message));
        }
        return await StreamAsync(row, acp, session, req.Origin, req.BlockedTools, req.Timeout, req.OnEvent,
            (onEvent, token) => acp.SendAsync(session, req.Message, onEvent, token), ct).ConfigureAwait(false);
    }

    /// <summary>Answers a pause and reads the continued turn (the broker's half of a pause, plan 7.6).</summary>
    public async Task<TurnReport> ResumeAsync(string botSlug, string session, AcpPause pause, bool allow, string? value, string origin,
        IReadOnlyList<string> blocked, TimeSpan timeout, Action<SseEvent>? onEvent, CancellationToken ct)
    {
        var row = _bots.Find(botSlug);
        if (row is null) return TurnReport.Unavailable($"no bot {botSlug}");
        var acp = acpFor(row.Port);
        return await StreamAsync(row, acp, session, origin, blocked, timeout, onEvent,
            (cb, token) => acp.AnswerAsync(session, pause, allow, value, cb, token), ct).ConfigureAwait(false);
    }

    private async Task<TurnReport> StreamAsync(BotRow row, AcpClient acp, string session, string origin, IReadOnlyList<string> blocked, TimeSpan timeout,
        Action<SseEvent>? onEvent, Func<Action<SseEvent>, CancellationToken, Task<AcpTurnResult>> send, CancellationToken ct)
    {
        TurnGate.Write(paths, row.Slug, origin, blocked);
        var ok = 0;
        var failed = 0;
        var tools = new List<string>();
        var errors = new List<string>();
        var log = Path.Combine(paths.Logs, $"events-{row.Slug}.log");
        SecureFile.EnsureDirNoClobber(paths.Logs);
        void Observe(SseEvent e)
        {
            try { File.AppendAllText(log, $"{clock.UtcNow:O} {e.Name} {Redactor.Shared.Redact(e.Data)}\n"); }
            catch (IOException) { }
            if (e.Name == "tool_start" && Field(e.Data, "name") is { Length: > 0 } n) tools.Add(n);
            if (e.Name == "tool_end")
            {
                if (Bool(e.Data, "ok")) ok++;
                else
                {
                    failed++;
                    errors.Add(Redactor.Shared.Redact((Field(e.Data, "name") + ": " + (Field(e.Data, "reason") ?? Field(e.Data, "error_code") ?? "failed")).Trim()));
                }
            }
            onEvent?.Invoke(e);
        }
        AcpTurnResult result;
        var timedOut = false;
        var paused = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                result = await send(Observe, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { await acp.AbortAsync(session, CancellationToken.None).ConfigureAwait(false); }
                catch (HttpRequestException) { }
                result = new AcpTurnResult(TurnEnd.Error, "", null, "turn time limit reached; the turn was aborted", null, tools);
            }
            catch (HttpRequestException ex)
            {
                result = new AcpTurnResult(TurnEnd.StreamCut, "", null, Redactor.Shared.Redact(ex.Message), null, tools);
            }
            paused = result.End == TurnEnd.Paused;
        }
        finally
        {
            // A paused turn is not over: the gate stays until it ends (EndPausedAsync or a resumed turn that finishes).
            if (!paused) TurnGate.Reset(paths, row.Slug);
        }
        _bots.Touch(row.Slug);
        return new TurnReport(result.End, result.Text, result.Pause, result.Error, session, new TurnEvidence(ok, failed, tools, errors), timedOut, result.Usage);
    }

    /// <summary>Ends a paused turn without answering it: aborts it in the bot (dropping the pause) and clears the gate.</summary>
    public async Task EndPausedAsync(string botSlug, string session, CancellationToken ct)
    {
        var row = _bots.Find(botSlug);
        if (row is null) return;
        try { await acpFor(row.Port).AbortAsync(session, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { }
        TurnGate.Reset(paths, row.Slug);
    }

    private async Task<string> SessionFor(BotRow row, string kind, AcpClient acp, CancellationToken ct)
    {
        if (kind == "fresh") return await acp.CreateSessionAsync(row.Spec.Model, ct).ConfigureAwait(false);
        if (kind.StartsWith("id:", StringComparison.Ordinal))
        {
            var sid = kind[3..];
            return await acp.GetSessionAsync(sid, ct).ConfigureAwait(false) is not null ? sid : throw new KeyNotFoundException("the session to continue is gone");
        }
        var key = kind == "standing" ? "standing" : kind;
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9:_-]{0,80}$")) throw new InvalidOperationException("bad session kind");
        var id = _bots.GetSession(row.Id, key);
        if (id is not null && await acp.GetSessionAsync(id, ct).ConfigureAwait(false) is not null) return id;
        id = await acp.CreateSessionAsync(row.Spec.Model, ct).ConfigureAwait(false);
        _bots.SetSession(row.Id, id, key);
        return id;
    }

    private static string? Field(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Bool(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
