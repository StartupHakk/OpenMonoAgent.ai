using System.Text;
using OpenMono.Session;

namespace OpenMono.Commands;

/// <summary>
/// Lists every plan presented this session (id, status, timestamp) and lets the
/// user re-open one — the plan markdown renders into the conversation. Plans
/// survive reload via SessionMetadata.Plans, so history works across resume.
/// </summary>
public sealed class PlansCommand : ICommand
{
    private const int PreviewChars = 70;

    public string Name => "plans";
    public string Description => "List this session's plans; pick one to re-read it";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var plans = context.Session.Meta.Plans;
        if (plans.Count == 0)
        {
            context.Renderer.WriteInfo("No plans yet this session — use /plan [task] to get one.");
            return;
        }

        // Newest first for display; digit selection maps back to this order.
        var ordered = plans.OrderByDescending(p => p.CreatedAtUtc).ToList();

        context.Renderer.WriteInfo($"Plans this session ({ordered.Count}), newest first:");
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var marker = p.Id == context.Session.Meta.LastPlanId ? "->" : "  ";
            context.Renderer.WriteInfo($"  {marker} {i + 1}. [{StatusLabel(p)}] {Title(p)}");
        }

        var labels = ordered.Select(Label).ToList();
        var picked = await context.Renderer.SelectOptionAsync("Open a plan", labels, 0, ct);
        if (picked is null)
            return;

        var idx = labels.IndexOf(picked);
        if (idx < 0) return;

        var plan = ordered[idx];
        context.Renderer.WriteInfo($"— plan {plan.Id} · {plan.CreatedAtUtc.ToLocalTime():g} · {StatusLabel(plan)} —");
        context.Renderer.WriteMarkdown(plan.Content);
        if (plan.Path is not null)
            context.Renderer.WriteInfo($"saved to: {plan.Path}");
    }

    private static string Label(PlanRecord p)
    {
        var when = p.CreatedAtUtc.ToLocalTime();
        var head = Title(p);
        if (head.Length > PreviewChars) head = head[..(PreviewChars - 1)] + "…";
        return $"{StatusLabel(p)}  {when:M/d HH:mm}  {head}";
    }

    private static string StatusLabel(PlanRecord p) => p.Status switch
    {
        PlanStatus.Pending => "pending",
        PlanStatus.Implementing => "implementing",
        PlanStatus.KeptPlanning => "kept planning",
        PlanStatus.Superseded => "superseded",
        PlanStatus.Completed => "completed",
        PlanStatus.Abandoned => "abandoned",
        _ => p.Status.ToString().ToLowerInvariant(),
    };

    private static string Title(PlanRecord p)
    {
        foreach (var line in p.Content.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            return t.TrimStart('#', ' ');
        }
        return "(untitled plan)";
    }
}
