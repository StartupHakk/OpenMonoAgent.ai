namespace OpenMono.HostBridge;

/// <summary>
/// CLI surface for the host sub-agent. Interactive TTYs get the full-screen
/// agent TUI (<c>--classic</c> forces the plain line interface); everything
/// else mirrors <c>openmono agent</c>.
/// </summary>
public sealed class BridgeOptions
{
    public const string Version = "0.1.0";

    public string? AcpUrl { get; private set; }
    public string? WorkDir { get; private set; }
    public string? TaskText { get; private set; }
    public string? TaskFile { get; private set; }
    public string? SessionId { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? Model { get; private set; }
    public int? Port { get; private set; }
    public bool Fresh { get; private set; }
    public bool NonInteractive { get; private set; }
    public bool NoSpawn { get; private set; }
    public bool KeepContainer { get; private set; }
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }

    /// <summary>
    /// Configure run-as identity + sudo opt-in, persist, and exit.
    /// Used by the installer and by operators re-configuring the bridge.
    /// </summary>
    public bool Init { get; private set; }

    /// <summary>Run host commands as this user (a username is not a secret).</summary>
    public string? RunAs { get; private set; }

    /// <summary>Read the run-as sudo password from this file (0600, memory-only).</summary>
    public string? PasswordFile { get; private set; }

    /// <summary>Explicit session sudo choice — the only non-interactive way to allow it.</summary>
    public bool? AllowSudoOverride { get; private set; }

    /// <summary>Force the full-screen agent TUI even when it would not auto-start.</summary>
    public bool ForceTui { get; private set; }

    /// <summary>Force the plain line interface (no full-screen TUI).</summary>
    public bool Classic { get; private set; }

    /// <summary>Leave the session in Plan mode instead of defaulting to Build.</summary>
    public bool Plan { get; private set; }

    public static BridgeOptions Parse(string[] args)
    {
        var options = new BridgeOptions();
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next()
            {
                i++;
                return i < args.Length ? args[i] : null;
            }
            switch (arg)
            {
                case "--acp": options.AcpUrl = Next(); break;
                case "--workdir": options.WorkDir = Next(); break;
                case "--task": options.TaskText = Next(); break;
                case "--task-file": options.TaskFile = Next(); break;
                case "--session": options.SessionId = Next(); break;
                case "--config": options.ConfigPath = Next(); break;
                case "--model": options.Model = Next(); break;
                case "--port" when int.TryParse(Next(), out var port): options.Port = port; break;
                case "--fresh": options.Fresh = true; break;
                case "--non-interactive": options.NonInteractive = true; break;
                case "--no-spawn": options.NoSpawn = true; break;
                case "--keep": options.KeepContainer = true; break;
                case "--init": options.Init = true; break;
                case "--run-as": options.RunAs = Next(); break;
                case "--password-file": options.PasswordFile = Next(); break;
                case "--allow-sudo": options.AllowSudoOverride = true; break;
                case "--no-sudo": options.AllowSudoOverride = false; break;
                case "--tui": options.ForceTui = true; break;
                case "--classic": options.Classic = true; break;
                case "--plan": options.Plan = true; break;
                case "--verbose":
                case "-v": options.Verbose = true; break;
                case "--help":
                case "-h": options.ShowHelp = true; break;
                case "--version": options.ShowVersion = true; break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        throw new InvalidOperationException($"unknown flag: {arg} (see --help)");
                    positional.Add(arg);
                    break;
            }
        }
        if (options.TaskText is null && options.TaskFile is null && positional.Count > 0)
            options.TaskText = string.Join(' ', positional);
        if (options.RunAs is not null && string.IsNullOrWhiteSpace(options.RunAs))
            throw new InvalidOperationException("--run-as needs a username (see --help)");
        if (options.RunAs is not null && !string.IsNullOrWhiteSpace(options.RunAs) &&
            !HostIdentity.IsValidUsername(options.RunAs.Trim()))
            throw new InvalidOperationException("--run-as needs a valid username ^[a-z_][a-z0-9_-]*[$]?$ (see --help)");
        if (options.PasswordFile is not null && string.IsNullOrWhiteSpace(options.PasswordFile))
            throw new InvalidOperationException("--password-file needs a path (see --help)");
        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("host-bridge — bare-metal OpenMono operator sub-agent (.NET 10)");
        Console.WriteLine();
        Console.WriteLine("Drives this box's in-container agent over ACP (same API as VS Code) and");
        Console.WriteLine("executes approved host commands on bare metal. Model loop stays in Docker.");
        Console.WriteLine();
        Console.WriteLine("Usage: host-bridge [options] [task ...]");
        Console.WriteLine("   openmono agent --host [-- <bridge options>]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --task <text>       Run one operator task, then exit");
        Console.WriteLine("  --task-file <path>  Read the task from a file");
        Console.WriteLine("  --session <id>      Reuse an ACP session instead of creating one");
        Console.WriteLine("  --acp <url>         Attach to this ACP server (skip container spawn)");
        Console.WriteLine("  --no-spawn          Attach via .openmono/agent.lock (or :7475), never spawn");
        Console.WriteLine("  --port <n>          Loopback port to publish ACP on when spawning (default 7475)");
        Console.WriteLine("  --fresh             Replace a running bridge container instead of reusing it");
        Console.WriteLine("  --keep              Leave the bridge container running on exit");
        Console.WriteLine("  --workdir <path>    Host directory to operate on (default: cwd)");
        Console.WriteLine("  --config <path>     Bridge policy file (default ~/.openmono/host-bridge.json)");
        Console.WriteLine("  --model <name>      Model for a new ACP session (default: server default)");
        Console.WriteLine("  --non-interactive   Never prompt: ask-policies deny, input pauses exit(2)");
        Console.WriteLine("  --tui               Force the full-screen agent TUI (default on a TTY)");
        Console.WriteLine("  --classic           Force the plain line interface (no full-screen TUI)");
        Console.WriteLine("  --plan              Leave the session in Plan mode (default is Build so it can act)");
        Console.WriteLine("  --init              Configure run-as user + sudo opt-in, save, and exit");
        Console.WriteLine("  --run-as <user>     Run host commands as this user (saved to config; not a secret)");
        Console.WriteLine("  --password-file <p> Read the sudo password from this file (0600, memory-only)");
        Console.WriteLine("  --allow-sudo        Explicitly allow sudo escalation for this run");
        Console.WriteLine("  --no-sudo           Explicitly forbid sudo escalation for this run");
        Console.WriteLine("                      (env: OPENMONO_HOST_RUN_AS, OPENMONO_HOST_PASSWORD_FILE)");
        Console.WriteLine("  --verbose, -v       Print resolved ACP/config summary at startup");
        Console.WriteLine("  --help, -h          Show this help");
        Console.WriteLine("  --version           Show version");
        Console.WriteLine();
        Console.WriteLine("Host execution: the agent requests it with AskUser + a 'HOST_EXEC:' question");
        Console.WriteLine("(see docs/playbooks-examples/server-deploy). Policy lives in the config file.");
    }
}
