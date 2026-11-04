// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Db;
using Omabot.Team;

namespace Omabot.Memory;

public static class MemoryStores
{
    public static string WorkspaceOf(RosterPaths paths, BotSpec spec) => spec.Workspace == "auto" ? paths.Bot(spec.Slug).DefaultWorkspace : spec.Workspace;

    /// <summary>The bot's memory store, or null when memory is off for the bot.</summary>
    public static MemoryStore? For(RosterDb db, IClock clock, RosterPaths paths, BotSpec spec)
    {
        if (!spec.MemoryEnabled) return null;
        var bp = paths.Bot(spec.Slug);
        return new MemoryStore(db, clock, Redactor.Shared, spec.Slug, WorkspaceOf(paths, spec), bp.Journal,
            new MemoryLimits(spec.MemoryMaxLines, spec.MemoryMaxBytes), spec.Timezone);
    }

    public static string TurnsFile(RosterPaths paths, string slug) => Path.Combine(paths.Bot(slug).Journal, "turns.jsonl");
}
