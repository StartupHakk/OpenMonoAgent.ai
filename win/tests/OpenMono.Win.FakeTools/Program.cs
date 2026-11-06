using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// One binary, several personalities, chosen by the executable's file name
// (the smoke script copies FakeTool.exe to foundry.exe, docker.exe, ...).
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
    case "foundry":
        return Foundry(string.Join(' ', args));
    case "nvidia-smi":
        Console.WriteLine("NVIDIA GeForce RTX 4090, 24564");
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
        await Server(int.Parse(args.Length > 0 ? args[0] : "7474", System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    default:
        Console.Error.WriteLine($"FakeTool: unknown personality '{name}'.");
        return 64;
}

static int Foundry(string command)
{
    switch (command)
    {
        case "--version":
            Console.WriteLine("foundry 0.13.0");
            return 0;
        case "server status":
            Console.WriteLine("Status: Running");
            Console.WriteLine("Local Endpoint: http://127.0.0.1:7474");
            return 0;
        case "model list --output json":
            Console.WriteLine("""[{"alias":"qwen3.8-27b"},{"alias":"qwen3.5-9b"},{"alias":"qwen3.5-4b"},{"alias":"qwen3.5-2b"},{"alias":"qwen3.5-0.8b"}]""");
            return 0;
        default:
            Console.WriteLine("ok");
            return 0;
    }
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
static int Winget(string[] args)
{
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

// Minimal OpenAI-compatible stand-in: lists one served model and refuses
// image parts, like a text-only build. Also answers the Foundry catalog API
// (POST /api/models) in two pages linked by continuationToken, and appends
// each catalog request body to FAKE_API_LOG.
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

static async Task Respond(NetworkStream stream)
{
    var request = await ReadRequestAsync(stream);
    var (status, body) = request.StartsWith("GET /v1/models", StringComparison.Ordinal)
        ? (200, """{"data":[{"id":"qwen3.8-27b-cuda-gpu:1"}]}""")
        : request.StartsWith("POST /api/models", StringComparison.Ordinal)
            ? (200, CatalogPage(request))
        : request.StartsWith("POST /v1/chat/completions", StringComparison.Ordinal)
            ? (request.Contains("image_url", StringComparison.Ordinal)
                ? (400, """{"error":"image input not supported"}""")
                : (200, """{"choices":[{"message":{"content":"ok"}}]}"""))
            : (404, "{}");
    var payload = Encoding.UTF8.GetBytes(body);
    var head = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(head);
    await stream.WriteAsync(payload);
}

static string CatalogPage(string request)
{
    var apiLog = Environment.GetEnvironmentVariable("FAKE_API_LOG");
    var requestBody = request[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
    if (!string.IsNullOrEmpty(apiLog))
    {
        File.AppendAllText(apiLog, requestBody + Environment.NewLine);
    }

    static object Model(string alias, string variant, string task, string inputs, long bytes) => new
    {
        annotations = new { tags = new { alias, task, inputModalities = inputs, supportsToolCalling = "true", contextLength = "262144" } },
        properties = new
        {
            id = variant,
            variantInfo = new { variantMetadata = new { device = "gpu", executionProvider = "CUDAExecutionProvider", fileSizeBytes = bytes, vRamFootprintBytes = bytes } },
        },
    };

    var page = requestBody.Contains("\"continuationToken\":\"fake-page-2\"", StringComparison.Ordinal)
        ? new
        {
            value = new[]
            {
                Model("qwen3.5-4b", "qwen3.5-4b-cuda-gpu:4", "vision-language-chat", "text,image", 4402341478),
                Model("qwen3.5-2b", "qwen3.5-2b-cuda-gpu:4", "vision-language-chat", "text,image", 3113851289),
                Model("qwen3.5-0.8b", "qwen3.5-0.8b-cuda-gpu:4", "vision-language-chat", "text,image", 1503238553),
            },
            continuationToken = (string?)null,
        }
        : new
        {
            value = new[]
            {
                Model("qwen3.8-27b", "qwen3.8-27b-cuda-gpu:1", "coding", "text", 19219982336),
                Model("qwen3.5-9b", "qwen3.5-9b-cuda-gpu:4", "vision-language-chat", "text,image", 7516192768),
            },
            continuationToken = (string?)"fake-page-2",
        };
    return System.Text.Json.JsonSerializer.Serialize(page);
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

static void Log(string line)
{
    var path = Environment.GetEnvironmentVariable("FAKE_LOG");
    if (!string.IsNullOrEmpty(path))
    {
        File.AppendAllText(path, line + Environment.NewLine);
    }
}
