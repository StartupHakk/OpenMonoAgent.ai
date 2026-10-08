using System.Net;
using System.Text;
using OpenMono.Windows.Hardware;
using OpenMono.Windows.Models;
using OpenMono.Windows.Supervisor;

// Headless smoke test for CI (windows-latest). Starts a stub
// OpenAI-compatible server (health, props, models, chat completions with SSE),
// then exercises port allocation, health gating, model detection, tier
// selection, and the llama command builder. Exits nonzero on any failure.
int port = PortAllocator.Allocate(SupervisorConfig.DefaultLlamaPort);
string baseUrl = $"http://127.0.0.1:{port}";
using var listener = new HttpListener();
listener.Prefixes.Add(baseUrl + "/");
listener.Start();
var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
_ = Task.Run(() => ServeAsync(listener, cts.Token));

int failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}");
    if (!ok)
    {
        failures++;
    }
}

var poller = new HealthPoller();
var healthy = await poller.WaitForHealthyAsync(baseUrl, timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromSeconds(1));
Check(healthy.Healthy, "stub server becomes healthy");
Check(healthy.Model == "smoke-test-model", $"model detection via /props (got {healthy.Model ?? "null"})");

var chatOk = await PostChatAsync(baseUrl + "/v1/chat/completions", cts.Token);
Check(chatOk, "stub chat completions SSE streams");

var gpus = new List<GpuInfo> { new("NVIDIA Smoke 24GB", GpuVendor.Nvidia, 24 * ModelTierSelector.Gb, 0, "560.0") };
var selection = ModelTierSelector.Select(gpus, 64 * ModelTierSelector.Gb, cudaAvailable: true);
Check(selection.Tier == InferenceTier.Tier24Gb, "tier selection picks 24GB tier");

var registryJson = Path.Combine(AppContext.BaseDirectory, "models.json");
Check(File.Exists(registryJson), "models.json is published next to the smoke test");

var config = new SupervisorConfig { LlamaPort = port };
var tier = new ModelTier(24, "label", "model.gguf", "http://x/model.gguf", 1, "full", "mmproj.gguf", "http://x/mmproj.gguf", 1, 196608, 172032, string.Empty, string.Empty);
var spec = config.BuildLlamaCommand(tier, 8, ModelTierSelector.ServerFlavor.Cpu);
Check(spec.Args.Contains("--metrics") && spec.Args.Contains("--flash-attn"), "llama command mirrors install.sh flags");

cts.Cancel();
listener.Stop();
Console.WriteLine(failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({failures})");
return failures == 0 ? 0 : 1;

static async Task ServeAsync(HttpListener listener, CancellationToken ct)
{
    while (!ct.IsCancellationRequested && listener.IsListening)
    {
        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync();
        }
        catch
        {
            return;
        }

        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            byte[] body;
            if (path == "/health")
            {
                body = "ok"u8.ToArray();
            }
            else if (path == "/props")
            {
                body = Encoding.UTF8.GetBytes("{\"default_generation_settings\": {\"model\": \"smoke-test-model\"}}");
            }
            else if (path == "/v1/models")
            {
                body = Encoding.UTF8.GetBytes("{\"data\": [{\"id\": \"smoke-test-model\"}]}");
            }
            else if (path == "/v1/chat/completions")
            {
                ctx.Response.ContentType = "text/event-stream";
                body = Encoding.UTF8.GetBytes("data: {\"choices\": [{\"delta\": {\"content\": \"hello\"}}]}\n\ndata: [DONE]\n\n");
            }
            else
            {
                ctx.Response.StatusCode = 404;
                body = "not found"u8.ToArray();
            }

            ctx.Response.StatusCode = ctx.Response.StatusCode == 0 ? 200 : ctx.Response.StatusCode;
            if (ctx.Response.StatusCode == 0)
            {
                ctx.Response.StatusCode = 200;
            }

            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct);
        }
        catch
        {
        }
        finally
        {
            ctx.Response.Close();
        }
    }
}

static async Task<bool> PostChatAsync(string url, CancellationToken ct)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.PostAsync(
            url,
            new StringContent("{\"model\": \"smoke-test-model\", \"messages\": [{\"role\": \"user\", \"content\": \"hi\"}], \"stream\": true}", Encoding.UTF8, "application/json"),
            ct);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Contains("hello", StringComparison.Ordinal);
    }
    catch
    {
        return false;
    }
}
