using System.IO.Compression;
using System.Text.Json;

namespace OpenMono.Win.Inference;

public sealed record InstalledBuild(string Tag, Accel Accel, Version? CudaVersion, string Directory, string ServerPath)
{
    public string Flavor => LlamaCppRelease.Flavor(Accel, CudaVersion);

    public string Id => $"{Tag}-{Flavor}";
}

public sealed record InstallResult(InstalledBuild? Build, IReadOnlyList<string> Log, string? Error);

// Installs official llama.cpp Windows release builds (the same project and
// server the Linux ghcr.io/ggml-org/llama.cpp:server images ship) under
// ~/.openmono/win/llama.cpp/<tag>-<flavor>/ (OPENMONO_LLAMA_CPP_DIR overrides). A CUDA build gets its cudart zip
// extracted next to llama-server.exe, so no CUDA toolkit is needed; only the
// NVIDIA driver. Installed builds are reused; `update` fetches a newer one.
public sealed class LlamaCppInstaller
{
    public const string MarkerFile = "openmono-build.json";

    private readonly HttpClient _http;
    private readonly FileDownloader _downloader;
    private readonly Func<string, string?> _getEnv;

    public LlamaCppInstaller(HttpClient http, string homeDir, Func<string, string?> getEnv, TextWriter? progress = null)
    {
        _http = http;
        _downloader = new FileDownloader(http, progress);
        _getEnv = getEnv;
        Root = RootFor(homeDir, getEnv);
    }

    public string Root { get; }

    public static string RootFor(string homeDir, Func<string, string?> getEnv) =>
        getEnv(LlamaCppConstants.InstallDirEnvVar) is { Length: > 0 } dir
            ? dir
            : Path.Combine(homeDir, ".openmono", "win", "llama.cpp");

    public static IReadOnlyList<InstalledBuild> ListInstalled(string root)
    {
        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }

        var builds = new List<InstalledBuild>();
        foreach (var dir in System.IO.Directory.GetDirectories(root))
        {
            var marker = Path.Combine(dir, MarkerFile);
            var server = File.Exists(marker) ? FindServer(dir) : null;
            if (server is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                var r = doc.RootElement;
                var tag = r.GetProperty("tag").GetString()!;
                var accel = Enum.Parse<Accel>(r.GetProperty("accel").GetString()!, ignoreCase: true);
                Version? cuda = r.TryGetProperty("cuda", out var c) && c.ValueKind == JsonValueKind.String
                    && Version.TryParse(c.GetString(), out var v) ? v : null;
                builds.Add(new InstalledBuild(tag, accel, cuda, dir, server));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or IOException or InvalidOperationException)
            {
                // A half-written marker is treated as not installed.
            }
        }

        return builds.OrderByDescending(b => LlamaCppRelease.BuildNumber(b.Tag) ?? 0).ToList();
    }

    // The newest installed build of this flavor the driver can run.
    public static InstalledBuild? FindInstalled(string root, Accel accel, Version? driverCuda, string? pinnedTag = null) =>
        ListInstalled(root).FirstOrDefault(b =>
            b.Accel == accel
            && (pinnedTag is null || string.Equals(b.Tag, pinnedTag, StringComparison.OrdinalIgnoreCase))
            && (accel != Accel.Cuda || (driverCuda is not null && b.CudaVersion is not null && b.CudaVersion <= driverCuda)));

    public async Task<InstallResult> EnsureAsync(Accel accel, Version? driverCuda, bool update, CancellationToken ct = default)
    {
        var log = new List<string>();
        var pinned = _getEnv(LlamaCppConstants.ReleaseTagEnvVar);
        pinned = string.IsNullOrWhiteSpace(pinned) ? null : pinned.Trim();
        var installed = FindInstalled(Root, accel, driverCuda, pinned);
        if (installed is not null && !update)
        {
            log.Add($"llama.cpp {installed.Id} is installed.");
            return new InstallResult(installed, log, null);
        }

        var url = _getEnv(LlamaCppConstants.ReleasesUrlEnvVar) is { Length: > 0 } custom ? custom : LlamaCppConstants.ReleasesApiUrl;
        IReadOnlyList<LlamaRelease> releases;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("openmono-win");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct);
            releases = response.IsSuccessStatusCode
                ? LlamaCppRelease.ParseReleases(await response.Content.ReadAsStringAsync(ct))
                : [];
            if (!response.IsSuccessStatusCode)
            {
                log.Add($"{url} returned {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.Add($"{url} unreachable: {ex.Message}");
            releases = [];
        }

        var choice = LlamaCppRelease.Pick(releases, accel, driverCuda, pinned);
        if (choice is null)
        {
            if (installed is not null)
            {
                log.Add($"Could not check for a newer llama.cpp build; keeping {installed.Id}.");
                return new InstallResult(installed, log, null);
            }

            var what = accel == Accel.Cuda
                ? $"a CUDA build for driver CUDA {driverCuda?.ToString() ?? "(unknown)"}"
                : $"a {LlamaCppRelease.Describe(accel)} build";
            return new InstallResult(null, log,
                $"No llama.cpp release{(pinned is null ? "" : " " + pinned)} with {what} for Windows x64 was found at {url}. "
                + $"Check the network, or see {LlamaCppConstants.ReleasesPageUrl}.");
        }

        if (installed is not null && string.Equals(installed.Id, choice.Id, StringComparison.OrdinalIgnoreCase))
        {
            log.Add($"llama.cpp {installed.Id} is installed and current.");
            return new InstallResult(installed, log, null);
        }

        var target = Path.Combine(Root, choice.Id);
        var downloads = Path.Combine(Root, "downloads");
        log.Add($"Installing llama.cpp {choice.Id} from {LlamaCppConstants.ReleasesPageUrl}/tag/{choice.Tag}.");
        foreach (var asset in new[] { choice.Server, choice.Cudart }.OfType<ReleaseAsset>())
        {
            var zip = Path.Combine(downloads, asset.Name);
            var got = await _downloader.DownloadAsync(asset.Url, zip, minBytes: 22, ct);
            if (!got.Ok)
            {
                return new InstallResult(installed, log, $"Downloading {asset.Name} failed: {got.Error}");
            }

            try
            {
                System.IO.Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new InstallResult(installed, log, $"Extracting {asset.Name} failed: {ex.Message}");
            }
            finally
            {
                TryDelete(zip);
            }
        }

        var server = FindServer(target);
        if (server is null)
        {
            return new InstallResult(installed, log, $"{choice.Server.Name} has no {LlamaCppConstants.ServerExecutableName}.");
        }

        // Some archives nest the binaries in a folder; the DLLs must sit next
        // to llama-server, so the cudart files are copied there too.
        var serverDir = Path.GetDirectoryName(server)!;
        if (!string.Equals(Path.GetFullPath(serverDir), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var dll in System.IO.Directory.GetFiles(target, "*.dll"))
            {
                File.Copy(dll, Path.Combine(serverDir, Path.GetFileName(dll)), overwrite: true);
            }
        }

        File.WriteAllText(Path.Combine(target, MarkerFile), JsonSerializer.Serialize(new
        {
            tag = choice.Tag,
            accel = choice.Accel.ToString().ToLowerInvariant(),
            cuda = choice.CudaVersion?.ToString(),
            server = choice.Server.Name,
            cudart = choice.Cudart?.Name,
        }));

        var build = new InstalledBuild(choice.Tag, choice.Accel, choice.CudaVersion, serverDir, server);
        log.Add($"llama.cpp {build.Id} installed in {serverDir}.");
        return new InstallResult(build, log, null);
    }

    private static string? FindServer(string dir) =>
        System.IO.Directory.Exists(dir)
            ? System.IO.Directory.GetFiles(dir, LlamaCppConstants.ServerExecutableName, SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault()
            : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover zip only costs disk space.
        }
    }
}
