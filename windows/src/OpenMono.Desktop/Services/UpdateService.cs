using System.Text.Json;

namespace OpenMono.Windows.Desktop.Services;

/// <summary>
/// Auto update checker. Default is a lightweight GitHub releases check with a
/// user prompt (no silent restarts mid session). Velopack delta updates can
/// replace the download step later; the integration point is ApplyUpdateAsync.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed record UpdateInfo(string Version, string Notes, string DownloadUrl);

    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/StartupHakk/OpenMonoAgent.ai/releases/latest");
            request.Headers.UserAgent.ParseAdd("OpenMono-Windows/1.0");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(tag) || tag.TrimStart('v').Equals(currentVersion.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var notes = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            return new UpdateInfo(tag, notes, url);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Velopack integration point: when Velopack is adopted, replace the body
    /// with VelopackManager-based delta download and apply on restart.
    /// </summary>
    public Task ApplyUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        // 1.0 behavior: open the release page and let the installer update.
        // No silent update, never force a restart mid session.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = info.DownloadUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
        }

        return Task.CompletedTask;
    }
}
