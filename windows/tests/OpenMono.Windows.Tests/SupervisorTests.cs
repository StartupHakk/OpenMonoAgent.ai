using System.Net;
using System.Text;
using OpenMono.Windows.AgentHost;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.Tests;

public sealed class SupervisorTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private string _healthBody = "ok";
    private string _propsBody = "{\"default_generation_settings\": {\"model\": \"test-model\"}}";

    public SupervisorTests()
    {
        int port = FindFreePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl + "/");
        _listener.Start();
        Task.Run(ServeAsync);
    }

    public void Dispose() => _listener.Stop();

    [Fact]
    public void SelectPort_Prefers_Preferred_Then_Fallbacks()
    {
        Assert.Equal(7474, PortAllocator.SelectPort(7474, _ => true));
        int selected = PortAllocator.SelectPort(7474, p => p != 7474 && p != 8081);
        Assert.Equal(8082, selected);
    }

    [Fact]
    public async Task Health_Gating_Detects_Model_Via_Props()
    {
        var poller = new HealthPoller();
        Assert.True(await poller.IsHealthyAsync(_baseUrl));
        var result = await poller.WaitForHealthyAsync(_baseUrl, timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromSeconds(1));
        Assert.True(result.Healthy);
        Assert.Equal("test-model", result.Model);
    }

    [Fact]
    public async Task Health_Gating_Reports_Unhealthy_On_Timeout()
    {
        _healthBody = "error";
        var poller = new HealthPoller();
        var result = await poller.WaitForHealthyAsync(_baseUrl, timeout: TimeSpan.FromSeconds(2), interval: TimeSpan.FromSeconds(1));
        Assert.False(result.Healthy);
        _healthBody = "ok";
    }

    [Fact]
    public void Docker_Version_Parses()
    {
        Assert.Equal("28.1.1", DockerDetector.ParseDockerVersion("28.1.1\n"));
        Assert.Null(DockerDetector.ParseDockerVersion("not a version"));
    }

    [Fact]
    public void WindowsShellTool_Wins_Over_Oma_Bash()
    {
        var registry = AgentHostFactory.CreateRegistry();
        var bash = registry.Resolve("Bash");
        Assert.NotNull(bash);
        Assert.IsType<WindowsShellTool>(bash);
    }

    [Fact]
    public void Permission_Defaults_Contain_Windows_Rules()
    {
        var defaults = WindowsPermissionDefaults.Defaults();
        Assert.True(defaults.ContainsKey("Bash"));
        Assert.Contains("reg delete*", defaults["Bash"].Deny);
        Assert.Contains("*runas*", defaults["Bash"].Ask);
    }

    [Fact]
    public void Guardrails_Block_Destructive_Windows_Commands()
    {
        Assert.True(WindowsGuardrails.IsDestructive("format E: /FS:NTFS"));
        Assert.True(WindowsGuardrails.IsDestructive("reg delete HKLM\\Software\\Test /f"));
        Assert.True(WindowsGuardrails.NeedsAsk("Start-Process setup.exe -Verb RunAs"));
        Assert.False(WindowsGuardrails.IsDestructive("git status"));
    }

    [Fact]
    public void Shell_Process_Build_Targets_PowerShell_First()
    {
        var psi = WindowsShellTool.BuildProcess(HookShellSelector.ShellKind.PowerShell7, "git status", "/tmp");
        Assert.Equal("pwsh.exe", psi.FileName);
        Assert.Contains("-NoProfile", psi.Arguments, StringComparison.Ordinal);
        var cmd = WindowsShellTool.BuildProcess(HookShellSelector.ShellKind.Cmd, "dir", "/tmp");
        Assert.Equal("cmd.exe", cmd.FileName);
    }

    [Fact]
    public void Slash_Palette_Filters()
    {
        Assert.True(SlashCommands.IsSlashCommand("/model"));
        Assert.Contains(SlashCommands.Filter("/mo"), e => e.Command == "/model");
    }

    [Fact]
    public void Lsp_Mapper_Round_Trips_File_Uris()
    {
        var uri = LspPathMapper.ToFileUri("/tmp/repo/file.cs");
        Assert.StartsWith("file://", uri, StringComparison.Ordinal);
        Assert.Equal("/tmp/repo/file.cs", LspPathMapper.FromFileUri(uri));
    }

    [Fact]
    public void Mcp_Helper_Wraps_Cmd_Shims()
    {
        var (command, args, _) = McpLaunchHelper.Resolve("server.cmd", ["--port", "8080"], null);
        Assert.Equal("cmd.exe", command);
        Assert.Contains("/d", args);
    }

    [Fact]
    public void Llama_Command_Mirrors_InstallSh_Flags()
    {
        var config = new SupervisorConfig { LlamaPort = 7474 };
        var tier = new Models.ModelTier(24, "label", "model.gguf", "http://x/model.gguf", 1, "full", "mmproj.gguf", "http://x/mmproj.gguf", 1, 196608, 172032, string.Empty, string.Empty);
        var spec = config.BuildLlamaCommand(tier, 8, Hardware.ModelTierSelector.ServerFlavor.Cuda);
        Assert.Contains("--n-gpu-layers", spec.Args);
        Assert.Contains("--flash-attn", spec.Args);
        Assert.Contains("--mmproj", spec.Args);
        Assert.Contains("--metrics", spec.Args);
        Assert.Contains("196608", spec.Args);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            try
            {
                byte[] body;
                ctx.Response.StatusCode = 200;
                if (ctx.Request.Url?.AbsolutePath == "/health")
                {
                    body = Encoding.UTF8.GetBytes(_healthBody);
                    if (_healthBody != "ok")
                    {
                        ctx.Response.StatusCode = 500;
                    }
                }
                else if (ctx.Request.Url?.AbsolutePath == "/props")
                {
                    body = Encoding.UTF8.GetBytes(_propsBody);
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("{\"data\": [{\"id\": \"fallback-model\"}]}");
                }

                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
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

    private static int FindFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
