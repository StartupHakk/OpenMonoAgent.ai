// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;

namespace Omabot.Tests.Support;

/// <summary>An in-memory upstream: answers by URL, records requests and the peak number in flight.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    private int _inFlight;
    private readonly Lock _lock = new();
    public int Peak { get; private set; }
    public List<(string Url, string Body, string? Auth)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_lock)
        {
            Seen.Add((request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            _inFlight++;
            if (_inFlight > Peak) Peak = _inFlight;
        }
        try
        {
            return await answer(request, body);
        }
        finally
        {
            lock (_lock) _inFlight--;
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(params string[] dataLines)
        => new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(dataLines.Select(d => "data: " + d + "\n\n")), Encoding.UTF8, "text/event-stream") };

    /// <summary>A streamed chat answer with a usage block, like Strata and llama-server send.</summary>
    public static HttpResponseMessage ChatStream(int chunks = 3, bool usage = true)
    {
        var lines = new List<string>();
        for (var i = 0; i < chunks; i++) lines.Add("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"w" + i + " \"}}]}");
        if (usage) lines.Add("{\"choices\":[],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":" + chunks + "}}");
        lines.Add("[DONE]");
        return Sse([.. lines]);
    }
}
