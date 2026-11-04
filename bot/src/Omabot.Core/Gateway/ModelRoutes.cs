// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Config;
using Omabot.Db;

namespace Omabot.Gateway;

/// <summary>A model nickname. Backend empty means any backend whose labels include <see cref="Labels"/>.</summary>
public sealed record ModelRoute(string Nickname, string UpstreamModel, string Backend, string Endpoint, string KeyFile, IReadOnlyList<string> Labels, int? ContextSize = null);

/// <summary>
/// Maps a model nickname (what bots ask for) to an upstream model on a
/// backend. Sources: team file models (team_items) and roster.json backends.
/// </summary>
public static class ModelRoutes
{
    public static Dictionary<string, ModelRoute> Load(RosterConfig config, RosterDb? db)
    {
        var routes = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase);
        var backends = config.Gateway.Backends.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var defaultEndpoint = config.Gateway.Upstream ?? config.Gateway.Backends.FirstOrDefault()?.Endpoint ?? "";
        var defaultKey = config.Gateway.UpstreamKeyFile;
        foreach (var b in config.Gateway.Backends)
            foreach (var (nick, upstream) in b.Models)
                routes[nick] = new ModelRoute(nick, upstream, b.Name, b.Endpoint, b.KeyFile, b.Labels);
        if (db is not null)
        {
            foreach (var json in db.Query("SELECT json FROM team_items WHERE kind='model';", r => r.GetString(0)))
            {
                using var doc = JsonDocument.Parse(json);
                var j = doc.RootElement;
                var nick = j.GetProperty("nickname").GetString()!;
                var upstream = j.GetProperty("upstream_model").GetString()!;
                var backendName = j.TryGetProperty("backend", out var be) && be.ValueKind == JsonValueKind.String ? be.GetString()! : null;
                var labels = j.TryGetProperty("labels", out var lb) ? lb.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                int? ctx = j.TryGetProperty("context_size", out var cs) && cs.ValueKind == JsonValueKind.Number ? cs.GetInt32() : null;
                if (backendName is not null && backends.TryGetValue(backendName, out var b))
                    routes[nick] = new ModelRoute(nick, upstream, b.Name, b.Endpoint, b.KeyFile, labels, ctx);
                else
                    routes[nick] = new ModelRoute(nick, upstream, "", defaultEndpoint, defaultKey, labels, ctx); // any backend with the labels
            }
        }
        return routes;
    }
}
