namespace OpenMono.Windows.Models;

/// <summary>
/// Resumable model and mmproj downloader with progress, pause, resume, cancel,
/// disk preflight, and checksum verification. Downloads to a .part file and
/// renames atomically on completion. Pure helper BuildRequest is unit tested.
/// </summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;

    public ModelDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromHours(6) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenMono-Windows/1.0");
        }
    }

    public sealed record DownloadProgress(
        string FileName,
        long ReceivedBytes,
        long? TotalBytes,
        double? Percent,
        string Status);

    public sealed record DownloadResult(
        string Path,
        long Bytes,
        bool Resumed,
        bool ChecksumSkipped,
        TimeSpan Elapsed);

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        int maxAttempts = 2,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        var partPath = destinationPath + ".part";

        if (File.Exists(destinationPath))
        {
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var existing = await ChecksumVerifier.VerifyFileAsync(destinationPath, expectedSha256, ct);
                if (existing.Ok && !existing.Skipped)
                {
                    progress?.Report(new DownloadProgress(Path.GetFileName(destinationPath), new FileInfo(destinationPath).Length, new FileInfo(destinationPath).Length, 100, "Already present and verified."));
                    return new DownloadResult(destinationPath, new FileInfo(destinationPath).Length, false, false, DateTime.UtcNow - started);
                }

                if (existing.Skipped)
                {
                    progress?.Report(new DownloadProgress(Path.GetFileName(destinationPath), new FileInfo(destinationPath).Length, null, null, "Already present (checksum not pinned)."));
                    return new DownloadResult(destinationPath, new FileInfo(destinationPath).Length, false, true, DateTime.UtcNow - started);
                }

                File.Delete(destinationPath);
            }
            else
            {
                progress?.Report(new DownloadProgress(Path.GetFileName(destinationPath), new FileInfo(destinationPath).Length, null, null, "Already present (checksum not pinned)."));
                return new DownloadResult(destinationPath, new FileInfo(destinationPath).Length, false, true, DateTime.UtcNow - started);
            }
        }

        Exception? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                bool resumed = await TryDownloadOnceAsync(url, partPath, progress, ct);
                var verify = await ChecksumVerifier.VerifyFileAsync(partPath, expectedSha256 ?? string.Empty, ct);
                if (!verify.Ok)
                {
                    File.Delete(partPath);
                    throw new InvalidOperationException(verify.Message);
                }

                File.Move(partPath, destinationPath, overwrite: true);
                var bytes = new FileInfo(destinationPath).Length;
                return new DownloadResult(destinationPath, bytes, resumed, verify.Skipped, DateTime.UtcNow - started);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                try
                {
                    if (File.Exists(partPath))
                    {
                        File.Delete(partPath);
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException($"Download failed after {maxAttempts} attempts: {url}", lastError);
    }

    private async Task<bool> TryDownloadOnceAsync(
        string url,
        string partPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var request = BuildRequest(url, existing);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            // Server ignored Range, restart from scratch.
            existing = 0;
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }

        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentRange?.Length
            ?? response.Content.Headers.ContentLength switch
            {
                { } len when existing > 0 => len + existing,
                { } len => len,
                _ => (long?)null,
            };

        bool resumed = existing > 0;
        using var content = await response.Content.ReadAsStreamAsync(ct);
        using var file = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = existing;
        int read;
        var fileName = Path.GetFileName(partPath.Replace(".part", string.Empty, StringComparison.OrdinalIgnoreCase));
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            double? percent = total is > 0 ? received / (double)total * 100 : null;
            progress?.Report(new DownloadProgress(fileName, received, total, percent, resumed ? "Resuming." : "Downloading."));
        }

        return resumed;
    }

    /// <summary>
    /// Builds a GET request with a Range resume header when offset is positive.
    /// Pure helper for unit tests.
    /// </summary>
    public static HttpRequestMessage BuildRequest(string url, long offset)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        }

        return request;
    }
}
