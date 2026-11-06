using System.Text.Json;

namespace OpenMono.Win.Foundry;

// Asks the live endpoint whether a model accepts image parts on the same
// /v1/chat/completions contract OMA already speaks (image_url parts).
// Runs at startup on the Windows machine; unit tests use a fake handler.
// Never throws: an unreachable or refusing server is a routing signal.
public sealed record VisionProbeResult(
    bool Accepted,
    int? StatusCode,
    string Model,
    string Detail);

public sealed class FoundryVisionProbe
{
    // 1x1 transparent PNG. Smallest decodable image; the probe only needs
    // the server to accept or refuse the part, not to describe anything.
    public const string TinyPngDataUrl =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly HttpClient _http;

    public FoundryVisionProbe(HttpClient http)
    {
        _http = http;
    }

    public async Task<VisionProbeResult> ProbeAsync(
        string endpointRoot,
        string model,
        CancellationToken ct = default)
    {
        var url = OmaEndpointContract.ChatCompletionsUrl(endpointRoot);
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Reply with the single word ok." },
                        new { type = "image_url", image_url = new { url = TinyPngDataUrl } },
                    },
                },
            },
            max_tokens = 8,
            stream = false,
        });

        try
        {
            using var response = await _http.PostAsync(
                url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return new VisionProbeResult(true, (int)response.StatusCode, model,
                    "Server accepted an image_url part for " + model + ".");
            }

            return new VisionProbeResult(false, (int)response.StatusCode, model,
                $"Server refused image input ({(int)response.StatusCode}): {Trim(responseBody, 200)}");
        }
        catch (HttpRequestException ex)
        {
            return new VisionProbeResult(false, null, model, "Endpoint unreachable: " + ex.Message);
        }
        catch (TaskCanceledException)
        {
            return new VisionProbeResult(false, null, model, "Probe timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            return new VisionProbeResult(false, null, model, "Probe failed: " + ex.Message);
        }
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "...");
}
