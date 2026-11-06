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
            Console.WriteLine("""[{"alias":"qwen3.8-27b"},{"alias":"qwen3-4b"},{"alias":"qwen2.5-vl-7b"}]""");
            return 0;
        default:
            Console.WriteLine("ok");
            return 0;
    }
}

static int Docker(string[] args)
{
    if (args.Length > 0 && args[0] == "info")
    {
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

// Minimal OpenAI-compatible stand-in: lists one served model and refuses
// image parts, like a text-only build.
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
        ? (200, """{"data":[{"id":"qwen3.8-27b-instruct-cuda-gpu:1"}]}""")
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
