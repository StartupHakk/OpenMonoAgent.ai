using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

// Deterministic ICliRunner for tests: queue exact outputs per command.
public sealed class FakeCliRunner : ICliRunner
{
    private readonly Dictionary<string, CliResult> _responses = new(StringComparer.OrdinalIgnoreCase);

    public readonly record struct Call(string FileName, string Arguments);

    public List<Call> Calls { get; } = [];

    public void Respond(string fileName, string arguments, CliResult result) =>
        _responses[fileName + " " + arguments] = result;

    public void Respond(string fileName, string arguments, int exitCode, string stdout, string stderr = "") =>
        Respond(fileName, arguments, new CliResult(exitCode, stdout, stderr));

    public Task<CliResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        Calls.Add(new Call(fileName, arguments));
        if (_responses.TryGetValue(fileName + " " + arguments, out var result))
        {
            return Task.FromResult(result);
        }

        throw new InvalidOperationException($"No fake response for '{fileName} {arguments}'.");
    }
}

// In-memory HTTP handler: maps request paths to canned responses.
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, HttpResponseMessage> _responses = new(StringComparer.OrdinalIgnoreCase);

    public void Respond(string pathAndQuery, HttpResponseMessage response) =>
        _responses[pathAndQuery] = response;

    public static HttpResponseMessage Json(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri?.PathAndQuery ?? "/";
        if (_responses.TryGetValue(key, out var response))
        {
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
