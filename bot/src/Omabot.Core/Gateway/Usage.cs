// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Gateway;

public sealed record UsageRecord(string Bot, string? Routine, Priority Priority, string Backend, string Model, int PromptTokens, int CompletionTokens, bool Estimated, long QueueMs, long TotalMs, int Status);

public sealed record UsageSummary(string Bot, string? Routine, long Calls, long PromptTokens, long CompletionTokens, long QueueMsP95, long Estimated);

/// <summary>Usage metering per bot and per routine (plan phase 2 task 4).</summary>
public sealed class UsageMeter(RosterDb db, IClock clock)
{
    public void Record(UsageRecord u)
        => db.Exec("""
            INSERT INTO usage(at, bot, routine, priority, backend, model, prompt_tokens, completion_tokens, estimated, queue_ms, total_ms, status)
            VALUES ($at, $b, $r, $p, $be, $m, $pt, $ct, $e, $q, $t, $s);
            """, ("$at", clock.UtcNow.ToString("O")), ("$b", u.Bot), ("$r", u.Routine), ("$p", Priorities.Name(u.Priority)), ("$be", u.Backend), ("$m", u.Model),
            ("$pt", u.PromptTokens), ("$ct", u.CompletionTokens), ("$e", u.Estimated ? 1 : 0), ("$q", u.QueueMs), ("$t", u.TotalMs), ("$s", u.Status));

    public long TokensToday(string bot, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset).ToUniversalTime();
        return Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(prompt_tokens + completion_tokens), 0) FROM usage WHERE bot=$b AND at >= $s;", ("$b", bot), ("$s", start.ToString("O"))) ?? 0L);
    }

    public List<UsageSummary> Summary(TimeSpan window, string? bot = null)
    {
        var since = (clock.UtcNow - window).ToString("O");
        var rows = db.Query("SELECT bot, routine, prompt_tokens, completion_tokens, queue_ms, estimated FROM usage WHERE at >= $s AND ($b IS NULL OR bot = $b);",
            r => (Bot: r.GetString(0), Routine: r.IsDBNull(1) ? null : r.GetString(1), P: r.GetInt64(2), C: r.GetInt64(3), Q: r.GetInt64(4), E: r.GetInt64(5)),
            ("$s", since), ("$b", bot));
        return [.. rows.GroupBy(x => (x.Bot, x.Routine))
            .Select(g =>
            {
                var q = g.Select(x => x.Q).OrderBy(x => x).ToList();
                var p95 = q[Math.Min(q.Count - 1, (int)Math.Ceiling(q.Count * 0.95) - 1)];
                return new UsageSummary(g.Key.Bot, g.Key.Routine, g.LongCount(), g.Sum(x => x.P), g.Sum(x => x.C), p95, g.Sum(x => x.E));
            })
            .OrderBy(s => s.Bot, StringComparer.Ordinal).ThenBy(s => s.Routine ?? "", StringComparer.Ordinal)];
    }
}

/// <summary>
/// Reads token counts from bytes passing through without changing them.
/// Uses the server's usage block when present (streamed or not); otherwise
/// counts streamed content chunks as an estimate.
/// </summary>
public sealed class UsageSniffer
{
    private const int MaxBuffer = 1 << 20;
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _whole = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _sse;
    private bool _decided;

    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public bool HasUsage { get; private set; }
    public int ContentChunks { get; private set; }
    public bool Estimated => !HasUsage;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[_decoder.GetCharCount(bytes, false)];
        _decoder.GetChars(bytes, chars, false);
        foreach (var ch in chars)
        {
            if (!_decided && !char.IsWhiteSpace(ch))
            {
                _sse = ch != '{';
                _decided = true;
            }
            if (!_sse)
            {
                if (_whole.Length < MaxBuffer) _whole.Append(ch);
                continue;
            }
            if (ch == '\n')
            {
                Line(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else if (_line.Length < MaxBuffer) _line.Append(ch);
        }
    }

    public void Finish()
    {
        if (_sse)
        {
            if (_line.Length > 0) Line(_line.ToString());
        }
        else if (_whole.Length > 0) Json(_whole.ToString());
        if (!HasUsage) CompletionTokens = ContentChunks;
    }

    private void Line(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        var data = line[5..].Trim();
        if (data.Length == 0 || data == "[DONE]") return;
        Json(data);
    }

    private void Json(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                if (u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv)) PromptTokens = pv;
                if (u.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv)) CompletionTokens = cv;
                HasUsage = true;
            }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object &&
                        ((d.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String && dc.GetString()!.Length > 0) || d.TryGetProperty("tool_calls", out _)))
                        ContentChunks++;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON; pass-through is unaffected.
        }
    }
}
