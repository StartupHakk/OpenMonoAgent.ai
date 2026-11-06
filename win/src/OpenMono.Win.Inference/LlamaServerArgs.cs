using System.Globalization;
using System.Text;

namespace OpenMono.Win.Inference;

public sealed record LlamaServerConfig(
    string ModelPath,
    string? MmprojPath,
    string Alias,
    int Port,
    int ContextTokens,
    string Kv,
    Accel Accel,
    int Threads,
    string LogFile,
    string? ApiKey = null);

// llama-server flags, the same set scripts/install.sh writes into the Linux
// compose override (GPU and CPU variants), with the host bound to loopback:
// Docker Desktop forwards host.docker.internal to it for the agent container.
public static class LlamaServerArgs
{
    public static IReadOnlyList<string> Build(LlamaServerConfig c)
    {
        var args = new List<string>
        {
            "--model", c.ModelPath,
            "--alias", c.Alias,
            "--host", "127.0.0.1",
            "--port", c.Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", c.ContextTokens.ToString(CultureInfo.InvariantCulture),
            "--threads", c.Threads.ToString(CultureInfo.InvariantCulture),
        };

        args.AddRange(c.Accel == Accel.Cpu
            ? ["--threads-batch", c.Threads.ToString(CultureInfo.InvariantCulture)]
            : ["--n-gpu-layers", "99"]);

        args.AddRange(
        [
            "--flash-attn", "on",
            "--cache-type-k", c.Kv,
            "--cache-type-v", c.Kv,
            "--batch-size", "2048",
            "--ubatch-size", "1024",
            "--parallel", "1",
            "--jinja",
            "--reasoning", "off",
            "--metrics",
        ]);

        if (c.MmprojPath is not null)
        {
            args.AddRange(["--mmproj", c.MmprojPath, "--image-min-tokens", "1024", "--image-max-tokens", "1280"]);
        }

        args.AddRange(["--log-file", c.LogFile]);

        if (!string.IsNullOrEmpty(c.ApiKey))
        {
            args.AddRange(["--api-key", c.ApiKey]);
        }

        return args;
    }

    // install.sh: 14 threads with a GPU, physical cores on CPU (SMT hurts
    // llama.cpp throughput); logical/2 stands in for physical cores here.
    // LLAMA_THREADS overrides, as on Linux.
    public static int DefaultThreads(Func<string, string?> getEnv, Accel accel)
    {
        if (int.TryParse(getEnv(LlamaCppConstants.ThreadsEnvVar), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            return n;
        }

        var physical = Math.Max(1, Environment.ProcessorCount / 2);
        return accel == Accel.Cpu ? physical : Math.Min(14, Environment.ProcessorCount);
    }

    // Identifies a launch (build and flags) without the secret, so a restart is
    // needed exactly when it changes and no key reaches the state file.
    public static string Fingerprint(string buildId, LlamaServerConfig c) =>
        buildId + " " + string.Join(' ', Build(c with { ApiKey = string.IsNullOrEmpty(c.ApiKey) ? null : "set", LogFile = "" }));

    // One command line for CreateProcess, quoted per the MSVC argv rules
    // llama-server parses with.
    public static string ToCommandLine(IEnumerable<string> args) =>
        string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
