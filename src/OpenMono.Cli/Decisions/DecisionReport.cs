using System.Text;
using OpenMono.Utils;

namespace OpenMono.Decisions;

public sealed record DecisionReport(
    string Task,
    bool DryRun,
    string Authorization,
    IReadOnlyList<DecisionItem> AllDecisions,
    IReadOnlyList<DecisionItem> ExecutionOrder,
    IReadOnlyList<DecisionItem> Skipped,
    IReadOnlyDictionary<string, long> TimingsMs,
    IReadOnlyList<string> Warnings)
{
    public string ToRedactedMarkdown()
    {
        var output = new StringBuilder();
        output.Append("Decision plan: ");
        output.Append(ExecutionOrder.Count);
        output.Append(" steps, ");
        output.Append(Skipped.Count);
        output.AppendLine(" skipped.");
        output.Append("Authorization: ");
        output.AppendLine(Authorization);
        foreach (var item in ExecutionOrder)
            AppendItem(output, item);
        if (Warnings.Count > 0)
        {
            output.AppendLine("Warnings:");
            foreach (var warning in Warnings)
            {
                output.Append("- ");
                output.AppendLine(SecretScanner.Redact(Truncate(warning, 200)));
            }
        }
        return output.ToString();
    }

    public override string ToString() =>
        $"DecisionReport task={Task} dryRun={DryRun} steps={ExecutionOrder.Count} skipped={Skipped.Count}";

    private static void AppendItem(StringBuilder output, DecisionItem item)
    {
        output.Append("- ");
        output.Append(item.Id);
        output.Append(" [");
        output.Append(item.Action);
        output.Append("] p=");
        output.Append(item.Probability.ToString("F2"));
        if (!string.IsNullOrEmpty(item.Detail))
        {
            output.Append(' ');
            output.Append(SecretScanner.Redact(Truncate(item.Detail, 120)));
        }
        output.AppendLine();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
