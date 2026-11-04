// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text.Json;
using Omabot.Common;

namespace Omabot.Gateway;

public sealed record SlotReading(int? Slots, string Source, string? Problem);

/// <summary>
/// Reads the real slot count from the inference server (plan 7.9, INT-07):
/// Strata answers GET /v1/status with concurrency.serving; llama-server
/// answers GET /slots with one entry per slot. Nothing is guessed here.
/// </summary>
public static class SlotDiscovery
{
    public static string Root(string endpoint)
    {
        var e = endpoint.TrimEnd('/');
        return e.EndsWith("/v1", StringComparison.Ordinal) ? e[..^3] : e;
    }

    public static async Task<SlotReading> DiscoverAsync(HttpClient http, string endpoint, string? key, string kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return new SlotReading(null, "none", "No endpoint configured.");
        var problems = new List<string>();
        var order = kind == "llama" ? new[] { "llama", "strata" } : ["strata", "llama"];
        foreach (var k in order)
        {
            var url = k == "strata" ? Root(endpoint) + "/v1/status" : Root(endpoint) + "/slots";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    problems.Add($"{k}: HTTP {(int)resp.StatusCode}");
                    continue;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                var n = k == "strata" ? ReadStrata(doc.RootElement) : ReadLlama(doc.RootElement);
                if (n is >= 1) return new SlotReading(n, k == "strata" ? "strata /v1/status concurrency.serving" : "llama-server /slots", null);
                problems.Add($"{k}: no slot count in the answer");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                problems.Add($"{k}: {Redactor.Shared.Redact(ex.Message)}");
            }
        }
        return new SlotReading(null, "none", string.Join("; ", problems));
    }

    public static int? ReadStrata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("concurrency", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (!c.TryGetProperty("serving", out var s)) return null;
        return s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? n : null;
    }

    public static int? ReadLlama(JsonElement root)
        => root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : null;
}

/// <summary>Where S came from for one backend.</summary>
public sealed record SlotDecision(int Slots, string Source, bool Known);

public static class SlotResolver
{
    /// <summary>
    /// Order: a measured number (node class or gateway.slots), then the
    /// server's own answer, then 1 lane marked unknown (builder choice B21:
    /// one lane is the inference server default and the safest fallback).
    /// </summary>
    public static SlotDecision Decide(int? measured, string? configured, SlotReading? discovered)
    {
        if (measured is >= 1) return new SlotDecision(measured.Value, "measured (node class)", true);
        if (configured is not null && configured != "auto" && int.TryParse(configured, out var c) && c >= 1)
            return new SlotDecision(c, "measured (gateway.slots)", true);
        if (discovered?.Slots is >= 1) return new SlotDecision(discovered.Slots.Value, discovered.Source, true);
        return new SlotDecision(1, "unknown, one lane until measured", false);
    }
}
