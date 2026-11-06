using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace OpenMono.Win.Inference;

public sealed record DownloadResult(bool Ok, string Path, long Bytes, string? Error);

// Streams a URL to disk the way install.sh uses curl: follow redirects, fail
// on HTTP errors, reject a suspiciously small body (an HTML error page). The
// body lands in <file>.part and is renamed only once complete, so a present
// file is always a whole file; an interrupted .part resumes with a Range
// request on the next run (multi-GB GGUFs over flaky links).
public sealed class FileDownloader
{
    private readonly HttpClient _http;
    private readonly TextWriter? _progress;

    public FileDownloader(HttpClient http, TextWriter? progress = null)
    {
        _http = http;
        _progress = progress;
    }

    public async Task<DownloadResult> DownloadAsync(
        string url, string destination, long minBytes = 1, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var part = destination + ".part";
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(part);
                return await DownloadAsync(url, destination, minBytes, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult(false, destination, 0, $"{url} returned {(int)response.StatusCode}.");
            }

            var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed)
            {
                existing = 0;
            }

            var length = response.Content.Headers.ContentLength;
            long? total = length is { } l ? l + existing : null;

            await using (var body = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                var written = existing;
                var lastReport = DateTime.UtcNow;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    if (_progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
                    {
                        lastReport = DateTime.UtcNow;
                        _progress.WriteLine(total is { } t && t > 0
                            ? $"    {System.IO.Path.GetFileName(destination)}: {Gb(written)} of {Gb(t)} ({written * 100 / t}%)"
                            : $"    {System.IO.Path.GetFileName(destination)}: {Gb(written)}");
                    }
                }
            }

            var size = new FileInfo(part).Length;
            if (total is { } expected && size != expected)
            {
                return new DownloadResult(false, destination, size,
                    $"{url} ended after {size} of {expected} bytes; run the same command again to resume.");
            }

            if (size < minBytes)
            {
                File.Delete(part);
                return new DownloadResult(false, destination, size,
                    $"{url} returned only {size} bytes, which is not the file (likely an HTTP error page).");
            }

            File.Move(part, destination, overwrite: true);
            return new DownloadResult(true, destination, size, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new DownloadResult(false, destination, 0, $"{url}: {ex.Message}");
        }
    }

    public static string Gb(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
