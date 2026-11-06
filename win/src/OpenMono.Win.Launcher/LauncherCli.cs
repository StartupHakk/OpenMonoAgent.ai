using System.Text.Json;
using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Launcher;

// Argument handling and output for openmono-win, separated from Program so
// tests drive whole commands with fakes and captured writers.
public static class LauncherCli
{
    public static async Task<int> RunAsync(string[] args, LauncherDeps deps, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        var parsed = LauncherArgs.Parse(args);
        var orchestrator = new InferenceOrchestrator(deps);

        switch (parsed.Command)
        {
            case "inference up":
            {
                var result = await orchestrator.UpAsync(
                    new UpOptions(parsed.Value("--model"), parsed.IntValue("--port") ?? FoundryConstants.DefaultPort), ct);
                return Report(result, parsed.Flag("--json"), stdout, stderr);
            }

            case "inference down":
            {
                var down = await orchestrator.DownAsync(parsed.Flag("--force"), ct);
                stdout.WriteLine(down.Message);
                return 0;
            }

            case "inference status":
                return await StatusAsync(deps, orchestrator, parsed.Flag("--json"), stdout, ct);

            case "agent-plan":
                return await AgentPlanAsync(deps, orchestrator, parsed, stdout, stderr, ct);

            case "doctor":
                return await DoctorAsync(deps, parsed, stdout, ct);

            case "help":
                stdout.WriteLine(Usage);
                return 0;

            default:
                stderr.WriteLine($"Unknown command: {string.Join(' ', args)}");
                stderr.WriteLine(Usage);
                return 2;
        }
    }

    // Windows PowerShell 5.1 drops a bare "--" and re-quotes arguments when it
    // calls a native program, so openmono.ps1 hands the agent's arguments
    // over as a JSON array in this variable instead of on the command line.
    public const string AgentArgsEnvVar = "OPENMONO_WIN_AGENT_ARGS";

    public static IReadOnlyList<string> AgentArgsFromEnv(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList(),
                JsonValueKind.String => [doc.RootElement.GetString()!],
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public const string Usage =
        "openmono-win inference up [--model ALIAS] [--port N] [--json]\n"
        + "openmono-win inference down [--force]\n"
        + "openmono-win inference status [--json]\n"
        + "openmono-win agent-plan --workdir DIR --compose FILE [--vision] [--model ALIAS] [--no-git-auth] -- <agent args>\n"
        + "openmono-win doctor [--workdir DIR] [--json]";

    private static int Report(UpResult result, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(
                new { ok = result.Ok, error = result.Error, state = result.State, log = result.Log },
                InferenceStateStore.JsonOptions));
            return result.Ok ? 0 : 1;
        }

        foreach (var line in result.Log)
        {
            stdout.WriteLine("  " + line);
        }

        if (!result.Ok)
        {
            stderr.WriteLine(result.Error);
            return 1;
        }

        var s = result.State!;
        stdout.WriteLine($"Inference ready: {s.Endpoint} (model {s.ServedModelId ?? s.ModelAlias ?? s.AgentModel ?? "auto-detect"}, "
            + $"vision {(s.VisionEnabled == true ? "on" : "off")}).");
        return 0;
    }

    private static async Task<int> StatusAsync(LauncherDeps deps, InferenceOrchestrator orchestrator, bool json, TextWriter stdout, CancellationToken ct)
    {
        var state = orchestrator.Store.Load();
        var endpoint = state?.Endpoint ?? FoundryConstants.DefaultLoopbackEndpoint;
        var health = await new FoundryHealthChecker(deps.Http).CheckAsync(OmaEndpointContract.TrimRoot(endpoint), ct);

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(
                new { endpoint, reachable = health.Reachable, models = health.Models, state },
                InferenceStateStore.JsonOptions));
            return 0;
        }

        stdout.WriteLine(health.Reachable
            ? $"Inference: {endpoint} up; models: {(health.Models.Count > 0 ? string.Join(", ", health.Models) : "none loaded")}."
            : $"Inference: {endpoint} down ({health.Error}).");
        if (state is not null)
        {
            stdout.WriteLine($"  backend {state.Backend}, alias {state.ModelAlias ?? "-"}, context {state.ContextTokens?.ToString() ?? "-"}, "
                + $"vision {(state.VisionEnabled == true ? "on" : "off")}, started by OpenMono: {(state.StartedByUs ? "yes" : "no")}.");
        }

        return 0;
    }

    private static async Task<int> AgentPlanAsync(
        LauncherDeps deps, InferenceOrchestrator orchestrator, LauncherArgs parsed, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var workDir = parsed.Value("--workdir");
        var compose = parsed.Value("--compose");
        if (workDir is null || compose is null)
        {
            stderr.WriteLine("agent-plan needs --workdir and --compose.");
            return 2;
        }

        var agentArgs = parsed.Passthrough.Count > 0
            ? parsed.Passthrough
            : AgentArgsFromEnv(deps.GetEnv(AgentArgsEnvVar));

        var vision = parsed.Flag("--vision");
        var up = await orchestrator.UpAsync(
            new UpOptions(parsed.Value("--model"), parsed.IntValue("--port") ?? FoundryConstants.DefaultPort, vision), ct);
        if (!up.Ok)
        {
            foreach (var line in up.Log)
            {
                stderr.WriteLine("  " + line);
            }

            stderr.WriteLine(up.Error);
            return 1;
        }

        var state = up.State!;
        var settings = HostSettings.Load(HostSettings.PathFor(deps.HomeDir));
        var plan = WindowsContainerStack.Build(new AgentRunRequest(
            WorkDir: workDir,
            HomeDir: deps.HomeDir,
            ComposeFile: compose,
            AgentArgs: agentArgs,
            Endpoint: state.Endpoint,
            Model: state.AgentModel,
            ContextTokens: state.ContextTokens,
            VisionEnabled: settings.VisionEnabled.HasValue ? null : state.VisionEnabled,
            WebGateway: settings.WebGateway,
            WebSearch: settings.WebSearch,
            WebScrape: settings.WebScrape,
            NoGitAuth: parsed.Flag("--no-git-auth") || deps.GetEnv("OPENMONO_NO_GIT_AUTH") == "1"));

        stdout.WriteLine(JsonSerializer.Serialize(
            new
            {
                environment = plan.Environment,
                docker_args = plan.DockerArgs,
                acp_port = plan.AcpHostPort,
                notes = up.Log.Where(IsUserNote).Concat(plan.Notes).ToList(),
                inference = state,
            },
            InferenceStateStore.JsonOptions));
        return 0;
    }

    // Only the lines a user acts on reach the agent launch banner; the full
    // log stays available through `openmono start`.
    private static bool IsUserNote(string line) =>
        line.StartsWith("Images:", StringComparison.Ordinal)
        || line.StartsWith("Vision session:", StringComparison.Ordinal)
        || line.Contains("adopting it", StringComparison.Ordinal)
        || line.Contains("below the 24 GB floor", StringComparison.Ordinal);

    private static async Task<int> DoctorAsync(LauncherDeps deps, LauncherArgs parsed, TextWriter stdout, CancellationToken ct)
    {
        var workDir = parsed.Value("--workdir") ?? Directory.GetCurrentDirectory();
        var pathDirs = (deps.GetEnv("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var checks = await new DoctorRunner(deps).RunAsync(new DoctorInput(
            workDir,
            OperatingSystem.IsWindows(),
            pathDirs,
            File.Exists,
            dir => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(),
            dir =>
            {
                try
                {
                    return Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().ToList() : [];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return [];
                }
            }), ct);

        if (parsed.Flag("--json"))
        {
            stdout.WriteLine(JsonSerializer.Serialize(
                checks.Select(c => new { name = c.Name, level = c.Level.ToString().ToLowerInvariant(), detail = c.Detail, fix = c.Fix }),
                InferenceStateStore.JsonOptions));
            return DoctorRunner.ExitCode(checks);
        }

        foreach (var check in checks)
        {
            var tag = check.Level switch
            {
                CheckLevel.Ok => "[ ok ]",
                CheckLevel.Info => "[info]",
                CheckLevel.Warn => "[warn]",
                _ => "[FAIL]",
            };
            stdout.WriteLine($"{tag} {check.Name}: {check.Detail}");
            if (check.Fix is not null && check.Level is CheckLevel.Warn or CheckLevel.Fail)
            {
                stdout.WriteLine($"       fix: {check.Fix}");
            }
        }

        return DoctorRunner.ExitCode(checks);
    }
}

// Tiny parser: "inference up" style two-word commands, --flag, --key value,
// and everything after a bare "--" passed through untouched to the agent.
public sealed class LauncherArgs
{
    private static readonly HashSet<string> ValueKeys = new(StringComparer.Ordinal)
    {
        "--model", "--port", "--workdir", "--compose",
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public string Command { get; private set; } = "help";

    public IReadOnlyList<string> Passthrough { get; private set; } = [];

    public static LauncherArgs Parse(string[] args)
    {
        var parsed = new LauncherArgs();
        var split = Array.IndexOf(args, "--");
        var own = split >= 0 ? args[..split] : args;
        parsed.Passthrough = split >= 0 ? args[(split + 1)..] : [];

        var words = new List<string>();
        for (var i = 0; i < own.Length; i++)
        {
            var arg = own[i];
            if (ValueKeys.Contains(arg) && i + 1 < own.Length)
            {
                parsed._values[arg] = own[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                parsed._flags.Add(arg);
            }
            else
            {
                words.Add(arg);
            }
        }

        if (words.Count > 0)
        {
            parsed.Command = words[0] == "inference" && words.Count > 1
                ? $"inference {words[1]}"
                : words[0];
        }

        return parsed;
    }

    public string? Value(string key) => _values.GetValueOrDefault(key);

    public int? IntValue(string key) => int.TryParse(Value(key), out var v) ? v : null;

    public bool Flag(string key) => _flags.Contains(key);
}
