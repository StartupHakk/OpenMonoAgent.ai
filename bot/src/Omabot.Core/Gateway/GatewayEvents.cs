// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Common;

namespace Omabot.Gateway;

public sealed record GatewayEvent(string At, string Bot, string Kind, string Detail);

/// <summary>
/// Gateway events as JSON lines (logs/gateway-events.log): repaired and
/// unreadable tool calls, repeat streaks, trimmed tools, spilled results.
/// Details are redacted; no message content is written.
/// </summary>
public sealed class GatewayEventLog(string path, IClock clock, long maxBytes = 10 * 1024 * 1024)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly Lock _lock = new();

    public string Path => path;

    public void Write(string bot, string kind, string detail)
    {
        var line = JsonSerializer.Serialize(new GatewayEvent(clock.UtcNow.ToString("O"), bot, kind, Redactor.Shared.Redact(detail.Length > 500 ? detail[..500] : detail)), Json) + "\n";
        lock (_lock)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
            }
            catch (IOException)
            {
                // Logging must never break a model call.
            }
        }
    }

    public static IEnumerable<GatewayEvent> Read(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        {
            GatewayEvent? e = null;
            try { e = JsonSerializer.Deserialize<GatewayEvent>(line, Json); }
            catch (JsonException) { }
            if (e is not null) yield return e;
        }
    }
}
