using System.Net;
using System.Text;
using OpenMono.Windows.Models;

namespace OpenMono.Windows.Tests;

public sealed class DownloaderTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _prefix;
    private readonly string _tempDir;
    private byte[] _payload = "hello-openmono"u8.ToArray();

    public DownloaderTests()
    {
        int port = FindFreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _tempDir = Path.Combine(Path.GetTempPath(), "oma-win-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _listener.Stop();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Downloads_File_And_Verifies_Checksum()
    {
        using var http = new HttpClient();
        var downloader = new ModelDownloader(http);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_payload)).ToLowerInvariant();
        var result = await downloader.DownloadAsync(
            _prefix + "model.gguf",
            Path.Combine(_tempDir, "model.gguf"),
            sha);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(_payload.Length, result.Bytes);
        Assert.False(result.ChecksumSkipped);
    }

    [Fact]
    public async Task Resumes_Partial_Download_With_Range()
    {
        using var http = new HttpClient();
        var downloader = new ModelDownloader(http);
        var dest = Path.Combine(_tempDir, "resume.gguf");
        await File.WriteAllBytesAsync(dest + ".part", _payload[..5]);
        var result = await downloader.DownloadAsync(_prefix + "model.gguf", dest);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(result.Path));
        Assert.True(result.Resumed);
        Assert.True(result.ChecksumSkipped);
    }

    [Fact]
    public async Task Retries_Once_On_Checksum_Mismatch_Then_Fails()
    {
        using var http = new HttpClient();
        var downloader = new ModelDownloader(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(_prefix + "model.gguf", Path.Combine(_tempDir, "bad.gguf"), new string('0', 64)));
    }

    [Fact]
    public void BuildRequest_Sets_Range_Header_For_Resume()
    {
        using var resume = ModelDownloader.BuildRequest("http://127.0.0.1:9/x", 5);
        Assert.Equal(5, resume.Headers.Range?.Ranges.First().From);
        using var fresh = ModelDownloader.BuildRequest("http://127.0.0.1:9/x", 0);
        Assert.Null(fresh.Headers.Range);
    }

    [Fact]
    public void Registry_Mirrors_InstallSh_Tiers()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models.json"));
        var registry = ModelRegistry.LoadFromJson(json);
        Assert.Equal(4, registry.Tiers.Count);
        var tier24 = registry.ForTier(24);
        Assert.Equal("Qwen3.8-27B-UD-Q4_K_M.gguf", tier24.ModelName);
        Assert.Equal("full", tier24.Accuracy);
        Assert.Equal(196608, tier24.CtxSize);
        Assert.Equal(172032, tier24.EffectiveCtx(visionEnabled: true));
        var tier16 = registry.ForTier(16);
        Assert.Equal(180224, tier16.CtxSize);
        Assert.Equal(98304, tier16.EffectiveCtx(visionEnabled: true));
        var cpu = registry.ForTier(0);
        Assert.Equal("standard", cpu.Accuracy);
        Assert.Equal(tier24.Mmproj, tier16.Mmproj);
    }

    [Fact]
    public void Mirror_Preserves_Path_And_Filename()
    {
        var mirrored = ModelRegistry.ApplyMirror(
            "https://huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/Qwen3.8-27B-UD-Q4_K_M.gguf",
            "http://192.168.1.10:8080");
        Assert.Equal("http://192.168.1.10:8080/unsloth/Qwen3.8-27B-GGUF/resolve/main/Qwen3.8-27B-UD-Q4_K_M.gguf", mirrored);
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
                var range = ctx.Request.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var from = long.Parse(range["bytes:".Length..].Split('-')[0]);
                    var rest = _payload[from..];
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {from}-{_payload.Length - 1}/{_payload.Length}");
                    ctx.Response.ContentLength64 = rest.Length;
                    await ctx.Response.OutputStream.WriteAsync(rest);
                }
                else
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = _payload.Length;
                    await ctx.Response.OutputStream.WriteAsync(_payload);
                }
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
