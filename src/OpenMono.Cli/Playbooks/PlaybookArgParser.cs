using OpenMono.Playbooks;

namespace OpenMono.Playbooks;

/// <summary>
/// Shared CLI-style argument parsing for playbook invocation, used by both the
/// <c>Playbook</c> tool (LLM-mediated) and the deterministic <c>/playbook</c>
/// slash-command path (ACP + REPL). Supports <c>--key=value</c>,
/// <c>--key value</c>, and a single positional value mapped to the first
/// required parameter (or <c>_positional</c> when there is none).
/// </summary>
public static class PlaybookArgParser
{
    public static Dictionary<string, object> ParseArguments(string args, PlaybookDefinition playbook)
    {
        var result = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(args)) return result;

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("--") && parts[i].Contains('='))
            {
                var kv = parts[i][2..].Split('=', 2);
                result[kv[0]] = kv[1];
            }
            else if (parts[i].StartsWith("--") && i + 1 < parts.Length)
            {
                result[parts[i][2..]] = parts[i + 1];
                i++;
            }
            else if (!result.ContainsKey("_positional"))
            {
                var firstParam = playbook.Parameters.FirstOrDefault(p => p.Value.Required);
                if (firstParam.Key is not null)
                    result[firstParam.Key] = parts[i];
                else
                    result["_positional"] = parts[i];
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a <c>/playbook &lt;name&gt; [args]</c> tail into the playbook name,
    /// the raw arguments string, and whether <c>--resume</c> was requested.
    /// </summary>
    public static (string Name, string Arguments, bool Resume) SplitCommandTail(string tail)
    {
        var trimmed = tail.Trim();
        if (string.IsNullOrEmpty(trimmed)) return ("", "", false);

        var space = trimmed.IndexOf(' ');
        var name = space < 0 ? trimmed : trimmed[..space];
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();

        var resume = false;
        if (rest.Contains("--resume"))
        {
            resume = true;
            rest = rest.Replace("--resume", "", StringComparison.Ordinal).Trim();
            while (rest.Contains("  ", StringComparison.Ordinal))
                rest = rest.Replace("  ", " ", StringComparison.Ordinal);
        }

        return (name, rest, resume);
    }
}
