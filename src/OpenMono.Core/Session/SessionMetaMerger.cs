namespace OpenMono.Session;

/// <summary>
/// Single source of truth for merging the mutable session <em>settings</em>
/// (plan mode, thinking, host mode, auto-approve, git profile, backend) between
/// two copies of the same session — e.g. the long-lived ACP server's in-memory
/// session and a freshly loaded on-disk copy written by a TUI that resumed the
/// same session, or vice versa.
/// </summary>
/// <remarks>
/// <para>
/// Conflict rule: <see cref="SessionMetadata.Revision"/> is monotonic and bumped
/// by <see cref="SessionSettings"/> on every mutation, so the copy with the higher
/// revision is strictly newer — last-writer-wins by revision. Equal revisions means
/// neither side mutated through the settings pipeline since the last sync, so
/// <paramref name="incoming"/> (typically the disk copy) is authoritative for
/// fields it carries, which keeps resume semantics identical to before the
/// revision existed.
/// </para>
/// <para>
/// Messages/tokens/checkpoints are deliberately NOT merged here — those are the
/// conversation's append-only history and each caller (reattach, ACP reload)
/// reconciles them with its own union/replace rules.
/// </para>
/// </remarks>
public static class SessionMetaMerger
{
    /// <summary>
    /// Merge the settings carried by <paramref name="incoming"/> into
    /// <paramref name="live"/>. Returns true when anything actually changed, so
    /// callers can decide whether to persist / broadcast.
    /// </summary>
    public static bool Merge(SessionState live, SessionState incoming)
    {
        var incomingWins = incoming.Meta.Revision >= live.Meta.Revision;
        var winner = incomingWins ? incoming.Meta : live.Meta;

        if (incomingWins)
        {
            // Model/backend live on SessionState but follow the same last-writer-wins
            // rule so a switch made in one process is visible to the other.
            live.Model = incoming.Model;
            live.ActiveBackend = incoming.ActiveBackend;
        }

        var changed = false;
        changed |= Assign(live.Meta.PlanMode, winner.PlanMode, v => live.Meta.PlanMode = v);
        changed |= Assign(live.Meta.ThinkingEnabled, winner.ThinkingEnabled, v => live.Meta.ThinkingEnabled = v);
        changed |= Assign(live.Meta.ThinkingLevel, winner.ThinkingLevel, v => live.Meta.ThinkingLevel = v);
        changed |= Assign(live.Meta.HostEnabled, winner.HostEnabled, v => live.Meta.HostEnabled = v);
        changed |= Assign(live.Meta.AutoApproveWrites, winner.AutoApproveWrites, v => live.Meta.AutoApproveWrites = v);
        changed |= Assign(live.Meta.GitProfileName, winner.GitProfileName, v => live.Meta.GitProfileName = v);
        changed |= Assign(live.Meta.GitProfileTouched, winner.GitProfileTouched, v => live.Meta.GitProfileTouched = v);

        if (live.Meta.Revision != winner.Revision)
        {
            live.Meta.Revision = winner.Revision;
            changed = true;
        }
        if (live.Meta.LastWriter != winner.LastWriter)
        {
            live.Meta.LastWriter = winner.LastWriter;
            changed = true;
        }

        // Plans + todos: the higher-revision copy wins wholesale (same rule as the
        // scalar flags) — merging interleaved plan/todo lists has no meaningful
        // semantics, so last-writer-wins is the documented behaviour.
        if (incomingWins && !SameTodos(live.Todos, incoming.Todos))
        {
            live.Todos.Clear();
            live.Todos.AddRange(incoming.Todos);
            changed = true;
        }
        if (incomingWins && !SamePlans(live.Meta.Plans, incoming.Meta.Plans))
        {
            live.Meta.Plans.Clear();
            live.Meta.Plans.AddRange(incoming.Meta.Plans);
            changed = true;
        }

        return changed;
    }

    private static bool Assign<T>(T current, T winnerValue, Action<T> set)
    {
        if (EqualityComparer<T>.Default.Equals(current, winnerValue)) return false;
        set(winnerValue);
        return true;
    }

    private static bool SameTodos(IReadOnlyList<TodoItem> a, IReadOnlyList<TodoItem> b)
        => a.Count == b.Count
           && a.Zip(b, (x, y) =>
               x.Content == y.Content && x.Status == y.Status && x.ActiveForm == y.ActiveForm)
              .All(same => same);

    private static bool SamePlans(IReadOnlyList<PlanRecord> a, IReadOnlyList<PlanRecord> b)
        => a.Count == b.Count
           && a.Zip(b, (x, y) => x.Id == y.Id && x.Status == y.Status).All(same => same);
}
