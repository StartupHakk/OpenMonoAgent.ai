// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Omabot.Gateway;

public sealed record BenchTask(string Id, string Prompt, string Tool, Func<JsonObject, bool> Check);

public sealed record TaskScore(string TaskId, int Slots, bool ValidBefore, bool ValidAfter, bool Unreadable, bool NoCall, string? Problem = null);

public sealed record ToolBenchLevel(int Slots, int Tasks, double ValidBefore, double ValidAfter, double Unreadable, double NoCall);

/// <summary>
/// The fixed tool task set (plan 7.8 measurement, phase 8 task 1): 40 tasks
/// over the tools roster bots use, scored on the raw reply (valid before
/// repair) and on the repaired reply (valid after repair). A call is valid
/// when it names the expected tool, its arguments are a JSON object with the
/// required fields in the schema's types, and they do what the task asked.
/// </summary>
public static class ToolBench
{
    private static JsonObject Tool(string name, string description, params (string Name, string Type, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var (n, t, _) in props) properties[n] = new JsonObject { ["type"] = t };
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray([.. props.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!)]),
                },
            },
        };
    }

    public static JsonArray Tools() =>
    [
        Tool("FileRead", "Read a file in the workspace", ("file_path", "string", true), ("offset", "integer", false), ("limit", "integer", false)),
        Tool("Grep", "Search file contents with a regular expression", ("pattern", "string", true), ("path", "string", false), ("max_results", "integer", false)),
        Tool("ListDirectory", "List a folder", ("path", "string", true)),
        Tool("Glob", "Find files by pattern", ("pattern", "string", true)),
        Tool("FileEdit", "Replace text in a file", ("file_path", "string", true), ("old_string", "string", true), ("new_string", "string", true)),
        Tool("Bash", "Run a shell command", ("command", "string", true), ("timeout_ms", "integer", false)),
        Tool("mcp__roster__assign", "Hand work to another bot", ("to", "string", true), ("brief", "string", true)),
        Tool("mcp__roster__consult", "Ask another bot a question", ("to", "string", true), ("brief", "string", true)),
    ];

    private static string S(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static long? I(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    public static IReadOnlyList<BenchTask> Tasks()
    {
        var list = new List<BenchTask>();
        string[] files = ["README.md", "src/Program.cs", "docs/setup.md", "tests/UnitTests.cs", "notes/todo.txt"];
        foreach (var (f, i) in files.Select((f, i) => (f, i)))
            list.Add(new($"read-{i + 1}", $"Read the file {f}.", "FileRead", a => S(a, "file_path").EndsWith(f, StringComparison.Ordinal)));
        string[] patterns = ["TODO", "FIXME", "HttpClient", "async Task", "password"];
        foreach (var (p, i) in patterns.Select((p, i) => (p, i)))
            list.Add(new($"grep-{i + 1}", $"Search the src folder for \"{p}\".", "Grep", a => S(a, "pattern").Contains(p, StringComparison.Ordinal)));
        string[] dirs = ["src", "docs", "tests", "scripts", "."];
        foreach (var (d, i) in dirs.Select((d, i) => (d, i)))
            list.Add(i < 3
                ? new($"list-{i + 1}", $"List the {d} folder.", "ListDirectory", a => S(a, "path").TrimEnd('/').EndsWith(d, StringComparison.Ordinal))
                : new($"glob-{i - 2}", i == 3 ? "Find every .sh file." : "Find every .csproj file.", "Glob", a => S(a, "pattern").Contains(i == 3 ? ".sh" : ".csproj", StringComparison.Ordinal)));
        (string File, string Old, string New)[] edits =
        [
            ("README.md", "Version 1", "Version 2"), ("src/Program.cs", "Hello", "Hi"), ("docs/setup.md", "step one", "first step"),
            ("notes/todo.txt", "[ ]", "[x]"), ("config.json", "\"debug\": true", "\"debug\": false"),
        ];
        foreach (var (e, i) in edits.Select((e, i) => (e, i)))
            list.Add(new($"edit-{i + 1}", $"In {e.File}, replace \"{e.Old}\" with \"{e.New}\".", "FileEdit",
                a => S(a, "file_path").EndsWith(e.File, StringComparison.Ordinal) && S(a, "old_string") == e.Old && S(a, "new_string") == e.New));
        string[] commands = ["dotnet test", "dotnet build", "git status", "ls -la", "npm test"];
        foreach (var (c, i) in commands.Select((c, i) => (c, i)))
            list.Add(new($"run-{i + 1}", $"Run `{c}`.", "Bash", a => S(a, "command").Trim().StartsWith(c, StringComparison.Ordinal)));
        (string To, string What)[] work = [("Builder", "fix the failing test"), ("Editor", "proofread the README"), ("Scout", "find the API docs"), ("Builder", "update the package versions"), ("Editor", "shorten the release notes")];
        foreach (var (w, i) in work.Select((w, i) => (w, i)))
            list.Add(new($"assign-{i + 1}", $"Hand this work to {w.To}: {w.What}.", "mcp__roster__assign",
                a => S(a, "to").Equals(w.To, StringComparison.OrdinalIgnoreCase) && S(a, "brief").Length > 0));
        (string To, string Q)[] questions = [("Scout", "which branch is current"), ("Builder", "does the build pass"), ("Editor", "is the tone right"), ("Scout", "where are the logs"), ("Builder", "which test is flaky")];
        foreach (var (q, i) in questions.Select((q, i) => (q, i)))
            list.Add(new($"consult-{i + 1}", $"Ask {q.To}: {q.Q}?", "mcp__roster__consult",
                a => S(a, "to").Equals(q.To, StringComparison.OrdinalIgnoreCase) && S(a, "brief").Length > 0));
        list.Add(new("typed-1", "Run `dotnet test` with a timeout of 60000 milliseconds.", "Bash", a => S(a, "command").Contains("dotnet test", StringComparison.Ordinal) && I(a, "timeout_ms") == 60000));
        list.Add(new("typed-2", "Search for \"TODO\" and show at most 5 results.", "Grep", a => S(a, "pattern").Contains("TODO", StringComparison.Ordinal) && I(a, "max_results") == 5));
        list.Add(new("typed-3", "Read README.md starting at line 10, 20 lines.", "FileRead", a => S(a, "file_path").EndsWith("README.md", StringComparison.Ordinal) && I(a, "offset") == 10 && I(a, "limit") == 20));
        list.Add(new("typed-4", "Run `git log -5` with a timeout of 5000 milliseconds.", "Bash", a => S(a, "command").Contains("git log", StringComparison.Ordinal) && I(a, "timeout_ms") == 5000));
        list.Add(new("typed-5", "Search the tests folder for \"Skip\", at most 10 results.", "Grep", a => S(a, "pattern").Contains("Skip", StringComparison.Ordinal) && I(a, "max_results") == 10));
        return list;
    }

    /// <summary>True when the arguments have every required field in the schema's type.</summary>
    public static bool FitsSchema(JsonObject args, JsonObject? schema)
    {
        if (schema is null) return true;
        foreach (var req in (schema["required"] as JsonArray ?? []).OfType<JsonValue>())
            if (req.TryGetValue<string>(out var r) && !args.ContainsKey(r)) return false;
        if (schema["properties"] is not JsonObject props) return true;
        foreach (var (key, value) in args)
        {
            if (props[key] is not JsonObject ps || ps["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type)) continue;
            var kind = value?.GetValueKind();
            var ok = type switch
            {
                "string" => kind == JsonValueKind.String,
                "integer" => kind == JsonValueKind.Number && value!.AsValue().TryGetValue<long>(out _),
                "number" => kind == JsonValueKind.Number,
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "object" => kind == JsonValueKind.Object,
                "array" => kind == JsonValueKind.Array,
                _ => true,
            };
            if (!ok) return false;
        }
        return true;
    }

    private static bool Valid(AssembledReply reply, BenchTask task, IReadOnlyDictionary<string, JsonObject?> offered)
    {
        var call = reply.Calls.FirstOrDefault(c => c.Name == task.Tool);
        if (call is null) return false;
        JsonObject? args;
        try { args = JsonNode.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments) as JsonObject; }
        catch (JsonException) { return false; }
        return args is not null && FitsSchema(args, offered.GetValueOrDefault(task.Tool)) && task.Check(args);
    }

    public static TaskScore Score(BenchTask task, int slots, JsonObject raw)
    {
        var offered = ToolCallRepair.OfferedTools(new JsonObject { ["tools"] = Tools() });
        var before = ToolCallRepair.FromJson(raw);
        var validBefore = Valid(before, task, offered);
        var after = ToolCallRepair.FromJson(raw);
        var (_, unreadable) = ToolCallRepair.Repair(after, offered);
        return new TaskScore(task.Id, slots, validBefore, Valid(after, task, offered), unreadable.Count > 0, before.Calls.Count == 0 && after.Calls.Count == 0);
    }

    public static IReadOnlyList<ToolBenchLevel> Summarize(IEnumerable<TaskScore> scores)
        => [.. scores.GroupBy(s => s.Slots).OrderBy(g => g.Key).Select(g =>
        {
            var n = g.Count();
            double Rate(Func<TaskScore, bool> f) => n == 0 ? 0 : Math.Round(100.0 * g.Count(f) / n, 1);
            return new ToolBenchLevel(g.Key, n, Rate(s => s.ValidBefore), Rate(s => s.ValidAfter), Rate(s => s.Unreadable), Rate(s => s.NoCall));
        })];

    public static JsonObject Request(BenchTask task, string model) => new()
    {
        ["model"] = model,
        ["stream"] = false,
        ["temperature"] = 0.4,
        ["max_tokens"] = 512,
        ["tools"] = Tools(),
        ["tool_choice"] = "auto",
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "You are a coding agent in a workspace. Do the task with exactly one tool call. Do not explain." },
            new JsonObject { ["role"] = "user", ["content"] = task.Prompt }),
    };

    /// <summary>Runs every task at each parallel level against an OpenAI-compatible endpoint (the measurement on Spencer's server).</summary>
    public static async Task<IReadOnlyList<TaskScore>> RunAsync(HttpClient http, string endpoint, string? key, string model, IReadOnlyList<int> levels, CancellationToken ct)
    {
        var scores = new List<TaskScore>();
        foreach (var level in levels)
        {
            using var gate = new SemaphoreSlim(level);
            var runs = Tasks().Select(async task =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/chat/completions") { Content = JsonContent.Create(Request(task, model)) };
                    if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                    using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode || JsonNode.Parse(text) is not JsonObject raw) return new TaskScore(task.Id, level, false, false, false, true, $"status {(int)resp.StatusCode}");
                    return Score(task, level, raw);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    return new TaskScore(task.Id, level, false, false, false, true, Common.Redactor.Shared.Redact(ex.Message));
                }
                finally
                {
                    gate.Release();
                }
            });
            scores.AddRange(await Task.WhenAll(runs).ConfigureAwait(false));
        }
        return scores;
    }

    public static string Report(IReadOnlyList<TaskScore> scores, string model, DateTimeOffset at)
    {
        var sb = new StringBuilder();
        sb.Append($"# Tool task set, {model}, {at:yyyy-MM-dd HH:mm} UTC\n\n");
        sb.Append("| Parallel requests | Tasks | Valid before repair | Valid after repair | Unreadable | No call |\n|---|---|---|---|---|---|\n");
        foreach (var l in Summarize(scores))
            sb.Append(CultureInfo.InvariantCulture, $"| {l.Slots} | {l.Tasks} | {l.ValidBefore}% | {l.ValidAfter}% | {l.Unreadable}% | {l.NoCall}% |\n");
        var failing = scores.Where(s => !s.ValidAfter).GroupBy(s => s.TaskId).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (failing.Count > 0)
        {
            sb.Append("\nTasks not valid after repair: ");
            sb.Append(string.Join(", ", failing.Select(g => $"{g.Key} ({string.Join("/", g.Select(s => s.Slots))})"))).Append('\n');
        }
        return sb.ToString();
    }
}
