using System.Text.Json;
using OpenMono.Win.AgentHost;
using OpenMono.Win.Inference;

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
                    new UpOptions(
                        parsed.Value("--model"),
                        parsed.IntValue("--port") ?? LlamaCppConstants.DefaultPort,
                        Update: parsed.Flag("--update")), ct);
                return Report(result, parsed.Flag("--json"), stdout, stderr);
            }

            case "inference down":
            {
                var down = await orchestrator.DownAsync(parsed.Flag("--force"));
                stdout.WriteLine(down.Message);
                return 0;
            }

            case "inference status":
                return await StatusAsync(deps, orchestrator, parsed.Flag("--json"), stdout, ct);

            case "agent-plan":
                return await AgentPlanAsync(deps, orchestrator, parsed, stdout, stderr, ct);

            case "doctor":
                return await DoctorAsync(deps, parsed, stdout, ct);

            case "diag":
                return await DiagAsync(deps, parsed, stdout, ct);

            case "models":
                return await ModelsAsync(deps, orchestrator, parsed.Flag("--json"), stdout, stderr, ct);

            case "models check":
                return await ModelsCheckAsync(deps, stdout, stderr, ct);

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
        "openmono-win inference up [--model ALIAS] [--port N] [--update] [--json]\n"
        + "openmono-win inference down [--force]\n"
        + "openmono-win inference status [--json]\n"
        + "openmono-win agent-plan --workdir DIR --compose FILE [--vision] [--model ALIAS] [--no-git-auth] -- <agent args>\n"
        + "openmono-win doctor [--workdir DIR] [--json]\n"
        + "openmono-win diag [--out FILE.zip] [--workdir DIR]\n"
        + "openmono-win models [--json]\n"
        + "openmono-win models check";

    // The Windows ladder from scripts/models.json and what this machine gets.
    // Reads the GPU and the catalog only; nothing is downloaded or started.
    private static async Task<int> ModelsAsync(
        LauncherDeps deps, InferenceOrchestrator orchestrator, bool json, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var catalog = ModelLadder.Locate(deps.GetEnv);
        if (catalog is null)
        {
            stderr.WriteLine("scripts\\models.json was not found; run from the OpenMono git checkout.");
            return 1;
        }

        var ladder = ModelLadder.Load(catalog);
        var plan = await orchestrator.PlanAsync(ladder, ct);
        var modelsDir = ModelStore.ModelsDirFor(catalog, deps.GetEnv);
        var installed = LlamaCppInstaller.ListInstalled(orchestrator.InstallRoot);
        var pick = plan.RungFor(plan.Accels[0]);

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                source = catalog,
                models_dir = modelsDir,
                ladder = ladder.All.Select(r => new
                {
                    tier = r.Key,
                    min_vram_gib = r.MinVramGiB,
                    alias = r.Alias,
                    file = r.File,
                    url = r.Url,
                    mmproj = r.Mmproj,
                    ctx = r.Context,
                    ctx_vision = r.VisionContext,
                    kv = r.Kv,
                    present = File.Exists(Path.Combine(modelsDir, r.File)),
                }),
                gpu = plan.Gpu?.Name,
                vram_bytes = plan.Gpu?.DedicatedVramBytes,
                driver_cuda = plan.DriverCuda?.ToString(),
                backends = plan.Accels.Select(a => a.ToString().ToLowerInvariant()),
                pick = pick.Alias,
                installed = installed.Select(b => b.Id),
            }, InferenceStateStore.JsonOptions));
            return 0;
        }

        stdout.WriteLine($"Windows model ladder ({catalog}), GGUF files in {modelsDir}:");
        foreach (var r in ladder.All)
        {
            var tier = r.IsCpu ? "no NVIDIA GPU" : $"NVIDIA {r.MinVramGiB:0}+ GiB";
            var present = File.Exists(Path.Combine(modelsDir, r.File)) ? "downloaded" : "not downloaded";
            stdout.WriteLine($"  {r.Key,-4} {tier,-18} {r.Label,-58} ctx {r.Context,-7} kv {r.Kv,-5} {present}");
        }

        var gpu = plan.Gpu is { } g ? $"{g.Name}, {g.DedicatedVramGiB:F0} GB" : "no NVIDIA GPU, CPU-only";
        stdout.WriteLine($"This machine ({gpu}): {pick.Alias}, llama.cpp backends in order: "
            + string.Join(", then ", plan.Accels.Select(LlamaCppRelease.Describe)) + ".");
        stdout.WriteLine(installed.Count > 0
            ? $"Installed llama.cpp builds: {string.Join(", ", installed.Select(b => b.Id))}."
            : "No llama.cpp build installed yet; .\\win\\openmono.ps1 setup installs one.");
        return 0;
    }

    // Live check for CI: the newest llama.cpp release still ships every
    // Windows flavor OpenMono installs, and every ladder download answers.
    private static async Task<int> ModelsCheckAsync(LauncherDeps deps, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var catalog = ModelLadder.Locate(deps.GetEnv);
        if (catalog is null)
        {
            stderr.WriteLine("scripts\\models.json was not found.");
            return 1;
        }

        var failed = false;
        var url = deps.GetEnv(LlamaCppConstants.ReleasesUrlEnvVar) is { Length: > 0 } custom ? custom : LlamaCppConstants.ReleasesApiUrl;
        IReadOnlyList<LlamaRelease> releases = [];
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("openmono-win");
            using var response = await deps.Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                releases = LlamaCppRelease.ParseReleases(await response.Content.ReadAsStringAsync(ct));
            }
            else
            {
                stdout.WriteLine($"[FAIL] {(int)response.StatusCode} {url}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            stdout.WriteLine($"[FAIL] {url}: {ex.Message}");
        }

        foreach (var (accel, cuda) in new[] { (Accel.Cuda, new Version(12, 4)), (Accel.Vulkan, (Version?)null), (Accel.Cpu, null) })
        {
            var choice = LlamaCppRelease.Pick(releases, accel, cuda);
            failed |= choice is null;
            stdout.WriteLine(choice is null
                ? $"[FAIL] llama.cpp {LlamaCppRelease.Describe(accel)}: no Windows x64 build in {url}"
                : $"[ ok ] llama.cpp {LlamaCppRelease.Describe(accel)}: {choice.Server.Name}{(choice.Cudart is null ? "" : " + " + choice.Cudart.Name)}");
        }

        foreach (var download in ModelLadder.Load(catalog).All
            .SelectMany(r => new[] { r.Url, r.MmprojUrl }.OfType<string>()).Distinct())
        {
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, download);
                using var answer = await deps.Http.SendAsync(head, ct);
                failed |= !answer.IsSuccessStatusCode;
                stdout.WriteLine($"[{(answer.IsSuccessStatusCode ? " ok " : "FAIL")}] {(int)answer.StatusCode} {download}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failed = true;
                stdout.WriteLine($"[FAIL] {download}: {ex.Message}");
            }
        }

        return failed ? 1 : 0;
    }

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
        stdout.WriteLine($"Inference ready: {s.Endpoint} (model {s.ModelAlias ?? s.AgentModel ?? "auto-detect"}"
            + (s.Accel is null ? "" : $", llama.cpp {s.LlamaBuild}")
            + $", vision {(s.VisionEnabled == true ? "on" : "off")}).");
        return 0;
    }

    private static async Task<int> StatusAsync(LauncherDeps deps, InferenceOrchestrator orchestrator, bool json, TextWriter stdout, CancellationToken ct)
    {
        var state = orchestrator.Store.Load();
        var endpoint = state?.Endpoint ?? LlamaCppConstants.DefaultLoopbackEndpoint;
        var health = await new ServerHealthChecker(deps.Http).CheckAsync(OmaEndpointContract.TrimRoot(endpoint), ct);

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(
                new { endpoint, reachable = health.Reachable, models = health.Models, state },
                InferenceStateStore.JsonOptions));
            return 0;
        }

        stdout.WriteLine(health.Ready
            ? $"Inference: {endpoint} up; models: {(health.Models.Count > 0 ? string.Join(", ", health.Models) : "none listed")}."
            : health.Loading
                ? $"Inference: {endpoint} is loading the model."
                : $"Inference: {endpoint} down ({health.Error}).");
        if (state is not null)
        {
            stdout.WriteLine($"  backend {state.Backend}{(state.LlamaBuild is null ? "" : $" (llama.cpp {state.LlamaBuild}, pid {state.ServerPid?.ToString() ?? "-"})")}, "
                + $"model {state.ModelAlias ?? "-"}, context {state.ContextTokens?.ToString() ?? "-"}, "
                + $"vision {(state.VisionEnabled == true ? "on" : "off")}, started by OpenMono: {(state.StartedByUs ? "yes" : "no")}.");
            if (state.LogFile is not null)
            {
                stdout.WriteLine($"  log: {state.LogFile}");
            }
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
            new UpOptions(parsed.Value("--model"), parsed.IntValue("--port") ?? LlamaCppConstants.DefaultPort, vision), ct);
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
        line.StartsWith("Vision off:", StringComparison.Ordinal)
        || line.StartsWith("Vision session:", StringComparison.Ordinal)
        || line.StartsWith("WARNING:", StringComparison.Ordinal)
        || line.Contains("adopting it", StringComparison.Ordinal)
        || line.StartsWith("Restarting llama-server", StringComparison.Ordinal);

    private static async Task<int> DoctorAsync(LauncherDeps deps, LauncherArgs parsed, TextWriter stdout, CancellationToken ct)
    {
        var input = BuildDoctorInput(deps, parsed.Value("--workdir") ?? Directory.GetCurrentDirectory());
        var checks = await new DoctorRunner(deps).RunAsync(input, ct);

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
    private static async Task<int> DiagAsync(LauncherDeps deps, LauncherArgs parsed, TextWriter stdout, CancellationToken ct)
    {
        var input = BuildDoctorInput(deps, parsed.Value("--workdir") ?? Directory.GetCurrentDirectory());
        var checks = await new DoctorRunner(deps).RunAsync(input, ct);
        var files = await new DiagnosticsBundle(deps).CollectAsync(checks, ct);

        var outPath = parsed.Value("--out")
            ?? Path.Combine(deps.HomeDir, ".openmono", "win",
                $"openmono-diag-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        DiagnosticsBundle.WriteZip(outPath, files);
        stdout.WriteLine($"Diagnostics bundle (secrets and home path redacted): {outPath}");
        stdout.WriteLine("Contents: " + string.Join(", ", files.Keys.Order(StringComparer.Ordinal)));
        return 0;
    }

    public static DoctorInput BuildDoctorInput(LauncherDeps deps, string workDir)
    {
        var pathDirs = (deps.GetEnv("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new DoctorInput(
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
            });
    }
}

// Tiny parser: "inference up" style two-word commands, --flag, --key value,
// and everything after a bare "--" passed through untouched to the agent.
public sealed class LauncherArgs
{
    private static readonly HashSet<string> ValueKeys = new(StringComparer.Ordinal)
    {
        "--model", "--port", "--workdir", "--compose", "--out",
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
            parsed.Command = words[0] is "inference" or "models" && words.Count > 1
                ? $"{words[0]} {words[1]}"
                : words[0];
        }

        return parsed;
    }

    public string? Value(string key) => _values.GetValueOrDefault(key);

    public int? IntValue(string key) => int.TryParse(Value(key), out var v) ? v : null;

    public bool Flag(string key) => _flags.Contains(key);
}
