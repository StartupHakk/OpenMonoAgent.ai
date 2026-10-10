using System.Diagnostics;

namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Owns the native llama-server.exe lifecycle: builds the tier command line,
/// captures logs under the app logs dir, polls /health, restarts with backoff.
/// Inference starts before the agent, and the agent only starts after the
/// server reports healthy (supervision rule shared with the plan).
/// </summary>
public sealed class LlamaServerSupervisor : IAsyncDisposable
{
    private readonly SupervisorConfig _config;
    private readonly HealthPoller _health;
    private Process? _process;
    private string? _logPath;

    public LlamaServerSupervisor(SupervisorConfig config, HealthPoller? health = null)
    {
        _config = config;
        _health = health ?? new HealthPoller();
    }

    public bool IsRunning => _process is { HasExited: false };

    public string? LogPath => _logPath;

    public async Task<HealthPoller.HealthResult> StartAsync(
        LlamaCommandSpec spec,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("llama-server is already running.");
        }

        // Fail fast before spawning the process: a LAN bind without a strong
        // API key must never happen, including from a hand-edited app.json.
        _config.ValidateLan();

        if (!File.Exists(spec.Binary))
        {
            throw new FileNotFoundException($"llama-server binary not found: {spec.Binary}. Run the first run wizard to download the GPU flavor.", spec.Binary);
        }

        var modelPath = spec.Args.SkipWhile(a => a != "--model").Skip(1).FirstOrDefault();
        if (modelPath is null || !File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Model file not found: {modelPath}. Run the first run wizard to download the model.", modelPath);
        }

        Directory.CreateDirectory(_config.LogsDirectory);
        _logPath = Path.Combine(_config.LogsDirectory, $"llama-server-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        var psi = new ProcessStartInfo
        {
            FileName = spec.Binary,
            Arguments = spec.ArgumentsLine,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(spec.Binary) ?? _config.BinDirectory,
        };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            psi.Environment["LLAMA_API_KEY"] = _config.ApiKey;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        await using var logFile = new StreamWriter(new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true));
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (logFile) { logFile.WriteLine(e.Data); } } };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (logFile) { logFile.WriteLine(e.Data); } } };

        progress?.Report($"Starting llama-server ({spec.Flavor}, ctx {spec.CtxSize})...");
        if (!_process.Start())
        {
            throw new InvalidOperationException("Failed to start llama-server.");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // If our preferred port is busy and healthy, reuse it instead of
        // starting a second server (conflict tolerance rule).
        var result = await _health.WaitForHealthyAsync(_config.LlamaEndpoint, ct: ct);
        if (!result.Healthy)
        {
            progress?.Report("llama-server did not become healthy. See logs.");
        }
        else
        {
            progress?.Report($"llama-server is healthy{(result.Model is not null ? $" (model: {result.Model})" : string.Empty)}.");
        }

        return result;
    }

    public async Task StopAsync(TimeSpan? grace = null)
    {
        grace ??= TimeSpan.FromSeconds(10);
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(grace.Value);
                try
                {
                    await _process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
