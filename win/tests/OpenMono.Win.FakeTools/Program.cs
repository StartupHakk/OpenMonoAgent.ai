using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// One binary, several personalities, chosen by the executable's file name
// (the smoke script copies FakeTool.exe to llama-server.exe, docker.exe, ...).
// Every invocation is appended to FAKE_LOG; docker compose run writes its argv
// and the compose interpolation variables to FAKE_DOCKER_OUT as JSON.
var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "FakeTool").ToLowerInvariant();
if (name == "faketool" && args.Length > 0)
{
    name = args[0];
    args = args[1..];
}

Log($"{name} {string.Join(' ', args)}");

switch (name)
{
    case "llama-server":
        await LlamaServer(args);
        return 0;
    case "nvidia-smi":
        // FAKE_NO_GPU=1: a machine without an NVIDIA GPU (driver present but no device).
        if (Environment.GetEnvironmentVariable("FAKE_NO_GPU") == "1")
        {
            Console.Error.WriteLine("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.");
            return 9;
        }

        // Bare `nvidia-smi` prints the banner with the driver's CUDA version
        // (FAKE_CUDA overrides 12.8); the query prints one name, MiB row.
        if (args.All(string.IsNullOrEmpty))
        {
            var cuda = Environment.GetEnvironmentVariable("FAKE_CUDA") is { Length: > 0 } v ? v : "12.8";
            Console.WriteLine($"| NVIDIA-SMI 572.16    Driver Version: 572.16    CUDA Version: {cuda}     |");
            return 0;
        }

        Console.WriteLine("NVIDIA GeForce RTX 4090, 24564");
        return 0;
    case "git":
        Console.WriteLine("git version 2.51.0.windows.1");
        return 0;
    case "docker":
        return Docker(args);
    case "docker desktop":
        return DockerDesktop();
    case "winget":
        return Winget(args);
    case "wsl":
        return Wsl(args);
    case "server":
        Port = int.Parse(args.Length > 0 ? args[0] : "7480", System.Globalization.CultureInfo.InvariantCulture);
        await Server(Port);
        return 0;
    default:
        Console.Error.WriteLine($"FakeTool: unknown personality '{name}'.");
        return 64;
}

static int Docker(string[] args)
{
    // FAKE_DOCKER_ENGINE names a file that exists while the engine runs;
    // unset means always running.
    if (args.Length > 0 && args[0] == "info")
    {
        var engine = Environment.GetEnvironmentVariable("FAKE_DOCKER_ENGINE");
        if (!string.IsNullOrEmpty(engine) && !File.Exists(engine))
        {
            Console.Error.WriteLine("error during connect: open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.");
            return 1;
        }

        if (args.Contains("--format"))
        {
            Console.WriteLine("linux");
        }

        return 0;
    }

    if (args.Length > 0 && args[0] == "--version")
    {
        Console.WriteLine("Docker version 27.5.1, build fake");
        return 0;
    }

    if (args.Length > 0 && args[0] == "version")
    {
        Console.WriteLine("27.5.1");
        return 0;
    }

    // FAKE_IMAGE_CRLF names a file that exists while the agent image was built
    // from CRLF files; `compose build agent` rebuilds it with LF.
    var crlf = Environment.GetEnvironmentVariable("FAKE_IMAGE_CRLF");
    var crlfImage = !string.IsNullOrEmpty(crlf) && File.Exists(crlf);
    if (args.Length > 1 && args[0] == "image" && args[1] == "inspect")
    {
        Console.WriteLine(crlfImage ? "sha256:fake-agent-crlf" : "sha256:fake-agent-lf");
        return 0;
    }

    if (args.Length > 2 && args[0] == "run" && args.Contains("od"))
    {
        Console.WriteLine("   #   !   /   u   s   r   /   b   i   n   /   e   n   v       b   a   s   h" + (crlfImage ? "  \\r" : string.Empty) + "  \\n");
        return 0;
    }

    if (crlfImage && args.Length > 0 && args[0] == "compose" && args.Contains("build"))
    {
        File.Delete(crlf!);
    }

    // Only the agent launch is captured; status and stop also call docker
    // (ps, rm) and must not overwrite it.
    var output = Environment.GetEnvironmentVariable("FAKE_DOCKER_OUT");
    if (!string.IsNullOrEmpty(output) && args.Length > 0 && args[0] == "compose" && args.Contains("run"))
    {
        var env = new Dictionary<string, string?>();
        foreach (var key in new[] { "WORKSPACE", "HOME", "OPENMONO_ENDPOINT", "CTX_SIZE", "OPENMONO_VISION_ENABLED", "OPENMONO_API_KEY" })
        {
            env[key] = Environment.GetEnvironmentVariable(key);
        }

        File.WriteAllText(output, JsonSerializer.Serialize(new { args, env }));
    }

    return 0;
}

// Brings the fake engine up after a delay, so callers must wait and re-check.
static int DockerDesktop()
{
    var engine = Environment.GetEnvironmentVariable("FAKE_DOCKER_ENGINE");
    if (!string.IsNullOrEmpty(engine))
    {
        Thread.Sleep(3000);
        File.WriteAllText(engine, "running");
    }

    return 0;
}

// `winget install --id Docker.DockerDesktop` lays out the real install shape
// under OPENMONO_DOCKER_DESKTOP_DIR: Docker Desktop.exe and resources\bin\docker.exe.
// Git.Git stages git.exe in FAKE_GIT_DIR, and the .NET SDK points at the real
// SDK in FAKE_DOTNET_DIR; both land only on the persisted PATH stand-in
// (OPENMONO_PATH_REGISTRY_FILE), never on the caller's PATH, as with an installer.
static int Winget(string[] args)
{
    var registry = Environment.GetEnvironmentVariable("OPENMONO_PATH_REGISTRY_FILE");
    if (args.Length > 0 && args[0] == "install" && (args.Contains("Git.Git") || args.Contains("Microsoft.DotNet.SDK.10")))
    {
        var dir = Environment.GetEnvironmentVariable(args.Contains("Git.Git") ? "FAKE_GIT_DIR" : "FAKE_DOTNET_DIR");
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(registry))
        {
            Console.Error.WriteLine("FakeTool: FAKE_GIT_DIR / FAKE_DOTNET_DIR and OPENMONO_PATH_REGISTRY_FILE must be set.");
            return 1;
        }

        if (args.Contains("Git.Git"))
        {
            StageAs(dir, "git");
        }

        File.AppendAllText(registry, dir + Environment.NewLine);
        Console.WriteLine("Successfully installed");
        return 0;
    }

    if (args.Length > 0 && args[0] == "install" && args.Contains("Docker.DockerDesktop"))
    {
        var dir = Environment.GetEnvironmentVariable("OPENMONO_DOCKER_DESKTOP_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            Console.Error.WriteLine("FakeTool: OPENMONO_DOCKER_DESKTOP_DIR is not set.");
            return 1;
        }

        StageAs(dir, "Docker Desktop");
        StageAs(Path.Combine(dir, "resources", "bin"), "docker");
        Console.WriteLine("Successfully installed");
    }

    return 0;
}

static void StageAs(string dir, string name)
{
    Directory.CreateDirectory(dir);
    foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "FakeTool.*"))
    {
        File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
    }

    var self = Environment.ProcessPath!;
    File.Copy(self, Path.Combine(dir, name + Path.GetExtension(self)), overwrite: true);
}

// FAKE_WSL_STATE names a file that exists once WSL is installed; unset means installed.
static int Wsl(string[] args)
{
    var state = Environment.GetEnvironmentVariable("FAKE_WSL_STATE");
    if (args.Contains("--install"))
    {
        if (!string.IsNullOrEmpty(state))
        {
            File.WriteAllText(state, "installed");
        }

        return 0;
    }

    return string.IsNullOrEmpty(state) || File.Exists(state) ? 0 : 1;
}

// Download server for the no-winget installers, the llama.cpp release feed
// and its Windows zips, and GGUF files (OPENMONO_MODEL_MIRROR layout).
static async Task Server(int port)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        try
        {
            await Respond(client.GetStream());
        }
        catch (IOException)
        {
            // A client that hangs up mid-request is not the server's problem.
        }
    }
}

// A llama-server stand-in: serves /health, /v1/models and /props for the
// --alias it was started with until it is killed, and writes --log-file.
static async Task LlamaServer(string[] args)
{
    string? Arg(string flag) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == flag && x.i + 1 < args.Length).Select(x => args[x.i + 1]).FirstOrDefault();

    var port = int.Parse(Arg("--port") ?? "7474", System.Globalization.CultureInfo.InvariantCulture);
    var alias = Arg("--alias") ?? "model";
    var vision = Arg("--mmproj") is not null;
    var build = Path.GetFileName(AppContext.BaseDirectory.TrimEnd('\\', '/'));

    // FAKE_LLAMA_NO_DEVICE=<text>: builds whose folder name contains it list
    // no GPU, as a CUDA build whose ggml-cuda.dll cannot load does.
    if (args.Contains("--list-devices"))
    {
        Log("llama-server list-devices " + build);
        var blind = Environment.GetEnvironmentVariable("FAKE_LLAMA_NO_DEVICE") is { Length: > 0 } nd
            && build.Contains(nd, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine("Available devices:");
        Console.WriteLine(blind ? "  (none)"
            : build.Contains("-cuda-", StringComparison.OrdinalIgnoreCase) ? "  CUDA0: Fake RTX 4090 (24563 MiB, 23012 MiB free)"
            : build.Contains("-vulkan", StringComparison.OrdinalIgnoreCase) ? "  Vulkan0: Fake RTX 4090 (24563 MiB, 23012 MiB free)"
            : "  (none)");
        return;
    }

    Log("llama-server build " + build);

    // FAKE_LLAMA_CRASH=<text>: builds whose folder name contains it exit while
    // loading, as a CUDA build does on a driver it cannot use.
    if (Environment.GetEnvironmentVariable("FAKE_LLAMA_CRASH") is { Length: > 0 } crash
        && build.Contains(crash, StringComparison.OrdinalIgnoreCase))
    {
        if (Arg("--log-file") is { } crashLog)
        {
            File.AppendAllText(crashLog, $"ggml_cuda_init: failed to initialize CUDA: forward compatibility was attempted on non supported HW ({build}){Environment.NewLine}");
        }

        Environment.Exit(1);
    }

    if (Arg("--log-file") is { } logFile)
    {
        File.AppendAllText(logFile, $"main: server is listening on http://127.0.0.1:{port} - model {alias}, build {build}{Environment.NewLine}");
    }

    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        try
        {
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream);
            var (status, body) = request.StartsWith("GET /health", StringComparison.Ordinal)
                ? (200, """{"status":"ok"}""")
                : request.StartsWith("GET /v1/models", StringComparison.Ordinal)
                    ? (200, JsonSerializer.Serialize(new { data = new[] { new { id = alias } } }))
                : request.StartsWith("GET /props", StringComparison.Ordinal)
                    ? (200, JsonSerializer.Serialize(new { model_alias = alias, modalities = new { vision } }))
                : request.StartsWith("POST /v1/chat/completions", StringComparison.Ordinal)
                    ? (200, """{"choices":[{"message":{"content":"ok"}}]}""")
                    : (404, "{}");
            await Send(stream, status, "application/json", Encoding.UTF8.GetBytes(body));
        }
        catch (IOException)
        {
            // Same as the download server.
        }
    }
}

static async Task Respond(NetworkStream stream)
{
    var request = await ReadRequestAsync(stream);
    if (Download(request) is { } file)
    {
        await Send(stream, 200, file.ContentType, file.Body);
        return;
    }

    // GGUF downloads: zeros of a plausible size (the launcher rejects bodies
    // under 64 MB for a model and 16 MB for a projector as error pages).
    if (request.StartsWith("GET /models/", StringComparison.Ordinal))
    {
        Log("download " + request.Split(' ')[1]);
        if (Control("models") is { } status)
        {
            await Send(stream, int.Parse(status, System.Globalization.CultureInfo.InvariantCulture), "text/html", Encoding.UTF8.GetBytes("<html>error</html>"));
            return;
        }

        var mmproj = request.StartsWith("GET /models/mmproj", StringComparison.Ordinal);
        await SendZeros(stream, (mmproj ? 16L : 64L) * 1024 * 1024 + 1);
        return;
    }

    await Send(stream, 404, "application/json", Encoding.UTF8.GetBytes("{}"));
}

static async Task Send(NetworkStream stream, int status, string contentType, byte[] payload)
{
    var head = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status} X\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(head);
    await stream.WriteAsync(payload);
}

static async Task SendZeros(NetworkStream stream, long length)
{
    var head = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 200 X\r\nContent-Type: application/octet-stream\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(head);
    var chunk = new byte[1 << 20];
    for (var left = length; left > 0; left -= chunk.Length)
    {
        await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, left)));
    }
}

// The no-winget installers: a Git for Windows release listing (only the
// plain MinGit zip for the machine's architecture may be picked), the
// FAKE_MINGIT_ZIP archive, and a dotnet-install.ps1 that links -InstallDir
// to the real SDK in FAKE_DOTNET_DIR. The llama.cpp release feed lists a
// newest tag without Windows binaries, then b9000 with the real Windows
// asset names; its zips are FAKE_LLAMA_ZIP (llama-server) and
// FAKE_CUDART_ZIP (CUDA runtime DLLs).
static (string ContentType, byte[] Body)? Download(string request)
{
    if (request.StartsWith("GET /git-release", StringComparison.Ordinal))
    {
        object Asset(string name) => new { name, browser_download_url = $"http://127.0.0.1:{Port}/mingit/" + name };
        return ("application/json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = "v2.51.0.windows.1",
            assets = new[]
            {
                Asset("Git-2.51.0-64-bit.exe"),
                Asset("MinGit-2.51.0-busybox-64-bit.zip"),
                Asset("MinGit-2.51.0-64-bit.zip"),
                Asset("MinGit-2.51.0-arm64.zip"),
            },
        }));
    }

    if (request.StartsWith("GET /mingit/MinGit-2.51.0-64-bit.zip", StringComparison.Ordinal)
        || request.StartsWith("GET /mingit/MinGit-2.51.0-arm64.zip", StringComparison.Ordinal))
    {
        return ("application/zip", File.ReadAllBytes(Environment.GetEnvironmentVariable("FAKE_MINGIT_ZIP")!));
    }

    if (request.StartsWith("GET /llama-releases", StringComparison.Ordinal))
    {
        object Asset(string name) => new { name, browser_download_url = $"http://127.0.0.1:{Port}/llama/" + name, size = 1 };
        return ("application/json", JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            new { tag_name = "v0.9.0", assets = Array.Empty<object>() },
            new
            {
                tag_name = "b9000",
                assets = new[]
                {
                    Asset("llama-b9000-bin-win-cuda-12.4-x64.zip"),
                    Asset("llama-b9000-bin-win-cuda-13.5-x64.zip"),
                    Asset("cudart-llama-bin-win-cuda-12.4-x64.zip"),
                    Asset("cudart-llama-bin-win-cuda-13.5-x64.zip"),
                    Asset("llama-b9000-bin-win-vulkan-x64.zip"),
                    Asset("llama-b9000-bin-win-cpu-x64.zip"),
                    Asset("llama-b9000-bin-win-sycl-x64.zip"),
                },
            },
        }));
    }

    if (request.StartsWith("GET /llama/", StringComparison.Ordinal))
    {
        var asset = request.Split(' ')[1]["/llama/".Length..];
        Log("download " + asset);
        var zip = Environment.GetEnvironmentVariable(asset.StartsWith("cudart", StringComparison.Ordinal) ? "FAKE_CUDART_ZIP" : "FAKE_LLAMA_ZIP");
        return ("application/zip", File.ReadAllBytes(zip!));
    }

    if (request.StartsWith("GET /dotnet-install.ps1", StringComparison.Ordinal))
    {
        return ("text/plain", Encoding.UTF8.GetBytes("""
            param([string]$Channel, [string]$InstallDir, [switch]$NoPath)
            Add-Content -Path $env:FAKE_LOG -Value "dotnet-install -Channel $Channel -InstallDir $InstallDir -NoPath:$NoPath"
            if ($Channel -ne '10.0') { exit 3 }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $InstallDir) | Out-Null
            New-Item -ItemType Junction -Path $InstallDir -Target $env:FAKE_DOTNET_DIR | Out-Null
            exit 0
            """));
    }

    return null;
}

static async Task<string> ReadRequestAsync(NetworkStream stream)
{
    var buffer = new byte[65536];
    var text = new StringBuilder();
    var headerEnd = -1;
    var contentLength = 0;
    while (true)
    {
        var read = await stream.ReadAsync(buffer);
        if (read == 0)
        {
            break;
        }

        text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        var all = text.ToString();
        if (headerEnd < 0)
        {
            headerEnd = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0)
            {
                foreach (var line in all[..headerEnd].Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        if (headerEnd >= 0 && Encoding.UTF8.GetByteCount(all) >= headerEnd + 4 + contentLength)
        {
            break;
        }
    }

    return text.ToString();
}

// The download server outlives the scenarios, so per-scenario behavior comes
// from FAKE_SERVER_CONTROL, a file of key=value lines read on every request
// (models=404 fails every GGUF download).
static string? Control(string key)
{
    var path = Environment.GetEnvironmentVariable("FAKE_SERVER_CONTROL");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
    {
        return null;
    }

    return File.ReadAllLines(path)
        .Select(l => l.Split('=', 2))
        .Where(p => p.Length == 2 && p[0].Trim() == key)
        .Select(p => p[1].Trim())
        .FirstOrDefault();
}

static void Log(string line)
{
    var path = Environment.GetEnvironmentVariable("FAKE_LOG");
    if (!string.IsNullOrEmpty(path))
    {
        File.AppendAllText(path, line + Environment.NewLine);
    }
}

static partial class Program
{
    public static int Port { get; set; } = 7480;
}
