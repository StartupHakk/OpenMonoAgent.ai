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

    // Runs a side effect when the command is invoked (for example, flip a fake
    // server to "serving" once `server start` runs).
    public void OnCall(string fileName, string arguments, Action effect) =>
        _effects[fileName + " " + arguments] = effect;

    private readonly Dictionary<string, Action> _effects = new(StringComparer.OrdinalIgnoreCase);

    public Task<CliResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        Calls.Add(new Call(fileName, arguments));
        if (_effects.TryGetValue(fileName + " " + arguments, out var effect))
        {
            effect();
        }

        if (_responses.TryGetValue(fileName + " " + arguments, out var result))
        {
            return Task.FromResult(result);
        }

        throw new InvalidOperationException($"No fake response for '{fileName} {arguments}'.");
    }
}

// Simulates a dead endpoint: every request throws like a refused connection.
public sealed class UnreachableHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
}

// In-memory HTTP handler: maps request paths to canned responses. Keys may be
// a bare path ("/v1/models") or authority plus path ("127.0.0.1:9000/v1/models")
// when a test needs two servers to answer differently.
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);

    public void Respond(string pathAndQuery, HttpResponseMessage response) =>
        _responses[pathAndQuery] = () => response;

    // Body is rebuilt per request so the same route can be read repeatedly.
    public void RespondJson(string pathAndQuery, Func<string> json) =>
        _responses[pathAndQuery] = () => Json(json());

    public static HttpResponseMessage Json(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var key = uri?.PathAndQuery ?? "/";
        var hostKey = uri is null ? key : uri.Authority + key;
        if (_responses.TryGetValue(hostKey, out var hostResponse))
        {
            return Task.FromResult(hostResponse());
        }

        if (_responses.TryGetValue(key, out var response))
        {
            return Task.FromResult(response());
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
