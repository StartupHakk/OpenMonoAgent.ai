using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OpenMono.Playbooks;

public sealed class PlaybookLoader
{
    private readonly List<string> _searchPaths;
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public PlaybookLoader(IEnumerable<string> searchPaths)
    {
        _searchPaths = searchPaths
            .Select(p => p.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
            .ToList();
    }

    public IReadOnlyList<PlaybookDefinition> LoadAll()
    {
        var playbooks = new List<PlaybookDefinition>();

        foreach (var basePath in _searchPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var playbookFile = Path.Combine(dir, "PLAYBOOK.md");
                if (!File.Exists(playbookFile)) continue;

                var playbook = ParsePlaybook(playbookFile, dir);
                if (playbook is not null) playbooks.Add(playbook);
            }
        }

        return playbooks;
    }

    private static PlaybookDefinition? ParsePlaybook(string filePath, string baseDir)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var parts = content.Split("---", 3, StringSplitOptions.None);

            if (parts.Length < 3)
            {

                return new PlaybookDefinition
                {
                    Name = Path.GetFileName(baseDir),
                    Description = content[..Math.Min(250, content.Length)],
                    BasePath = baseDir,
                    RoleDescription = content,
                };
            }

            var frontmatter = YamlDeserializer.Deserialize<Dictionary<string, object>>(parts[1]);
            var body = parts[2].Trim();

            return new PlaybookDefinition
            {
                Name = GetString(frontmatter, "name") ?? Path.GetFileName(baseDir),
                Version = GetString(frontmatter, "version") ?? "1.0.0",
                Description = GetString(frontmatter, "description") ?? body[..Math.Min(250, body.Length)],
                Trigger = ParseEnum<TriggerMode>(GetString(frontmatter, "trigger"), TriggerMode.Manual),
                TriggerPatterns = GetStringList(frontmatter, "trigger-patterns"),
                UserInvocable = GetBool(frontmatter, "user-invocable", true),
                ArgumentHint = GetString(frontmatter, "argument-hint"),
                AllowedTools = GetStringList(frontmatter, "allowed-tools") is { Length: > 0 } tools ? tools : ["*"],
                ContextMode = ParseEnum<ContextMode>(GetString(frontmatter, "context-mode"), ContextMode.Selective),
                MaxContextTokens = GetInt(frontmatter, "max-context-tokens", 3000),
                Tags = GetStringList(frontmatter, "tags"),
                Parameters = ParseParameters(frontmatter),
                Steps = ParseSteps(frontmatter),
                Constraints = ParseConstraints(frontmatter),
                BasePath = baseDir,
                RoleDescription = body,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(Dictionary<string, object> dict, string key) =>
        dict.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static bool GetBool(Dictionary<string, object> dict, string key, bool defaultVal) =>
        dict.TryGetValue(key, out var val) && val is bool b ? b : defaultVal;

    private static int GetInt(Dictionary<string, object> dict, string key, int defaultVal) =>
        dict.TryGetValue(key, out var val) && int.TryParse(val?.ToString(), out var i) ? i : defaultVal;

    private static T ParseEnum<T>(string? value, T defaultVal) where T : struct =>
        value is not null && System.Enum.TryParse<T>(value, ignoreCase: true, out var result)
            ? result : defaultVal;

    private static string[] GetStringList(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val is not List<object> list)
            return [];
        return [.. list.Select(o => o?.ToString() ?? "").Where(s => s.Length > 0)];
    }

    private static string? ObjStr(Dictionary<object, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static string[] ObjStrList(Dictionary<object, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is not List<object> list)
            return [];
        return [.. list.Select(o => o?.ToString() ?? "").Where(s => s.Length > 0)];
    }

    private static StepDefinition[] ParseSteps(Dictionary<string, object> frontmatter)
    {
        if (!frontmatter.TryGetValue("steps", out var raw) || raw is not List<object> list)
            return [];

        return [.. list
            .OfType<Dictionary<object, object>>()
            .Select(ParseStep)
            .OfType<StepDefinition>()];
    }

    private static StepDefinition? ParseStep(Dictionary<object, object> d)
    {
        var id = ObjStr(d, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        Dictionary<string, string>? @params = null;
        if (d.TryGetValue("params", out var pRaw) && pRaw is Dictionary<object, object> pMap)
            @params = pMap.ToDictionary(kv => kv.Key?.ToString() ?? "", kv => kv.Value?.ToString() ?? "");

        return new StepDefinition
        {
            Id = id,
            File = ObjStr(d, "file"),
            InlinePrompt = ObjStr(d, "inline-prompt"),
            Script = ObjStr(d, "script"),
            Agent = ObjStr(d, "agent"),
            Output = ObjStr(d, "output"),
            Playbook = ObjStr(d, "playbook"),
            Gate = ParseEnum<GateType>(ObjStr(d, "gate"), GateType.None),
            Requires = ObjStrList(d, "requires"),
            Params = @params,
        };
    }

    private static Dictionary<string, ParameterDefinition> ParseParameters(Dictionary<string, object> frontmatter)
    {
        if (!frontmatter.TryGetValue("parameters", out var raw) || raw is not Dictionary<object, object> map)
            return [];

        var result = new Dictionary<string, ParameterDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
        {
            var name = kv.Key?.ToString();
            if (name is null || kv.Value is not Dictionary<object, object> pd) continue;

            result[name] = new ParameterDefinition
            {
                Type = ParseEnum<ParameterType>(ObjStr(pd, "type"), ParameterType.String),
                Required = pd.TryGetValue("required", out var req) && req is bool b && b,
                Default = pd.TryGetValue("default", out var def) ? def : null,
                Hint = ObjStr(pd, "hint"),
                Enum = ObjStrList(pd, "enum") is { Length: > 0 } e ? e : null,
                Min = pd.TryGetValue("min", out var mn) && double.TryParse(mn?.ToString(), out var minV) ? minV : null,
                Max = pd.TryGetValue("max", out var mx) && double.TryParse(mx?.ToString(), out var maxV) ? maxV : null,
            };
        }
        return result;
    }

    private static ConstraintSet ParseConstraints(Dictionary<string, object> frontmatter)
    {
        if (!frontmatter.TryGetValue("constraints", out var raw) || raw is not Dictionary<object, object> map)
            return new ConstraintSet();

        string? file = map.TryGetValue("file", out var fv) ? fv?.ToString() : null;
        var inline = new List<string>();

        if (map.TryGetValue("inline", out var inlineRaw) && inlineRaw is List<object> inlineList)
            inline.AddRange(inlineList.Select(o => o?.ToString() ?? "").Where(s => s.Length > 0));

        return new ConstraintSet { File = file, Inline = inline };
    }
}
