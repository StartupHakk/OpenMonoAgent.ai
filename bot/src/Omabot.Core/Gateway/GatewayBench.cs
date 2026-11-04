// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Omabot.Common;

namespace Omabot.Gateway;

public sealed record BenchStream(double FirstTokenSeconds, double TotalSeconds, int PromptTokens, int CompletionTokens, bool Estimated, string? Error);

public sealed record BenchLevel(int Concurrency, IReadOnlyList<BenchStream> Streams)
{
    public IEnumerable<BenchStream> Ok => Streams.Where(s => s.Error is null && s.CompletionTokens > 0);
    /// <summary>Mean decode tokens per second per stream (after the first token).</summary>
    public double PerStreamTps => Ok.Any() ? Ok.Average(s => s.CompletionTokens / Math.Max(0.001, s.TotalSeconds - s.FirstTokenSeconds)) : 0;
    public double AggregateTps => PerStreamTps * Ok.Count();
    public double MeanFirstToken => Ok.Any() ? Ok.Average(s => s.FirstTokenSeconds) : 0;
}

public sealed record BenchReport(string Backend, string Model, SlotReading Discovery, double LatencySeconds, double PromptTokensPerSecond, IReadOnlyList<BenchLevel> Levels, DateTimeOffset At)
{
    public double R1 => Levels.FirstOrDefault(l => l.Concurrency == 1)?.PerStreamTps ?? 0;

    public double SpeedFactor(int level)
    {
        var l = Levels.FirstOrDefault(x => x.Concurrency == level);
        return l is null || R1 <= 0 ? 0 : Math.Round(l.PerStreamTps / R1, 3);
    }

    public string ToMarkdown()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(ci, $"# Gateway bench: {Backend}\n\nMeasured {At:yyyy-MM-dd HH:mm} UTC. Model {Model}.\n\n");
        sb.Append(ci, $"- Slots reported by the server: {(Discovery.Slots?.ToString(ci) ?? "unknown")} ({Discovery.Source}{(Discovery.Problem is null ? "" : "; " + Discovery.Problem)})\n");
        sb.Append(ci, $"- Round trip and first byte, L: {LatencySeconds:0.000} s\n");
        sb.Append(ci, $"- Prompt processing, pp: {PromptTokensPerSecond:0} tokens per second\n");
        sb.Append(ci, $"- Single stream, r1: {R1:0.0} tokens per second\n\n");
        sb.Append("| Concurrency | OK streams | Per stream tok/s | Aggregate tok/s | f(S) | Mean first token s |\n|---|---|---|---|---|---|\n");
        foreach (var l in Levels)
            sb.Append(ci, $"| {l.Concurrency} | {l.Ok.Count()} of {l.Streams.Count} | {l.PerStreamTps:0.0} | {l.AggregateTps:0.0} | {SpeedFactor(l.Concurrency):0.000} | {l.MeanFirstToken:0.00} |\n");
        var errors = Levels.SelectMany(l => l.Streams).Where(s => s.Error is not null).Select(s => s.Error!).Distinct().ToList();
        if (errors.Count > 0) sb.Append("\nErrors:\n").Append(string.Join("\n", errors.Select(e => "- " + e))).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// `oma-bot gateway bench` (plan 7.9 task C1): measures L, pp, r1 and f(S)
/// at the given concurrency levels straight against one backend. Numbers
/// come only from this measurement; nothing is assumed.
/// </summary>
public sealed class GatewayBench(HttpClient http, string endpoint, string? key, string model, IClock clock)
{
    public int MaxTokens { get; init; } = 200;
    public int PromptWords { get; init; } = 1500;

    public async Task<BenchReport> RunAsync(string backend, IReadOnlyList<int> levels, int rounds, SlotReading discovery, CancellationToken ct = default)
    {
        var tiny = await StreamAsync("Say OK.", 1, ct).ConfigureAwait(false);
        var latency = tiny.Error is null ? tiny.FirstTokenSeconds : 0;
        var longPrompt = BuildPrompt(PromptWords);
        var big = await StreamAsync(longPrompt, 1, ct).ConfigureAwait(false);
        var promptTokens = big.PromptTokens > 0 ? big.PromptTokens : longPrompt.Length / 4;
        var pp = big.Error is null && big.FirstTokenSeconds - latency > 0.001 ? promptTokens / (big.FirstTokenSeconds - latency) : 0;
        var results = new List<BenchLevel>();
        foreach (var level in levels.Distinct().OrderBy(x => x))
        {
            var streams = new List<BenchStream>();
            for (var r = 0; r < Math.Max(1, rounds); r++)
            {
                var tasks = Enumerable.Range(0, level).Select(i => StreamAsync($"Write a short numbered list of {12 + i} facts about rivers.", MaxTokens, ct));
                streams.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
            }
            results.Add(new BenchLevel(level, streams));
        }
        return new BenchReport(backend, model, discovery, latency, pp, results, clock.UtcNow);
    }

    private static string BuildPrompt(int words)
    {
        var sb = new StringBuilder("Read this text and reply with the single word DONE.\n");
        for (var i = 0; i < words; i++) sb.Append("river").Append(i % 97).Append(' ');
        return sb.ToString();
    }

    public async Task<BenchStream> StreamAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var sw = Stopwatch.StartNew();
        double first = -1;
        var sniffer = new UsageSniffer();
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new BenchStream(0, sw.Elapsed.TotalSeconds, 0, 0, true, $"HTTP {(int)resp.StatusCode}");
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[4096];
            int n;
            while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                var before = sniffer.ContentChunks;
                sniffer.Feed(buf.AsSpan(0, n));
                if (first < 0 && sniffer.ContentChunks > before) first = sw.Elapsed.TotalSeconds;
            }
            sniffer.Finish();
            if (first < 0) first = sw.Elapsed.TotalSeconds;
            return new BenchStream(first, sw.Elapsed.TotalSeconds, sniffer.PromptTokens, sniffer.CompletionTokens, sniffer.Estimated, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new BenchStream(0, sw.Elapsed.TotalSeconds, 0, 0, true, Redactor.Shared.Redact(ex.Message));
        }
    }
}
