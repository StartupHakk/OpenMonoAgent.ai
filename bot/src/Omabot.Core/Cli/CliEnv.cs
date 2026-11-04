// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Acp;
using Omabot.Common;
using Omabot.Supervision;

namespace Omabot.Cli;

/// <summary>Everything the CLI touches outside its arguments, so tests can swap it.</summary>
public sealed class CliEnv
{
    public required RosterPaths Paths { get; init; }
    public required string AppDir { get; init; }
    public IClock Clock { get; init; } = SystemClock.Instance;
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string?> ReadStdin { get; init; } = () => Console.IsInputRedirected ? Console.In.ReadToEnd() : null;
    /// <summary>Returns null when no supervisor is running.</summary>
    public Func<ISupervisorClient?> Supervisor { get; init; } = () => null;
    public Func<int, AcpClient> Acp { get; init; } = port => new AcpClient(SharedHttp, $"http://127.0.0.1:{port}");
    public LaunchOptions? Launch { get; init; }
    /// <summary>The handler the HTTPS front check uses; null means system trust and no redirects.</summary>
    public Func<HttpMessageHandler?> HttpsHandler { get; init; } = () => null;
    /// <summary>Tests turn off the per-bot shim sockets.</summary>
    public bool NoShimSockets { get; init; }

    public static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string DefaultAppDir()
    {
        var env = Environment.GetEnvironmentVariable("OMABOT_APP");
        if (!string.IsNullOrEmpty(env)) return env;
        var bin = AppContext.BaseDirectory.TrimEnd('/');
        var parent = Path.GetDirectoryName(bin) ?? bin;
        return Directory.Exists(Path.Combine(parent, "hooks")) ? parent : bin;
    }

    public static CliEnv Default()
    {
        var paths = new RosterPaths(RosterPaths.DefaultHome());
        return new CliEnv
        {
            Paths = paths,
            AppDir = DefaultAppDir(),
            Supervisor = () =>
            {
                if (!File.Exists(paths.SupervisorSocket) || !File.Exists(paths.SupervisorToken)) return null;
                return new SocketSupervisorClient(paths.SupervisorSocket, File.ReadAllText(paths.SupervisorToken).Trim());
            },
        };
    }
}
