using OpenMono.Win.Inference;

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

    // Runs a side effect when the command is invoked.
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

    public List<string> Requests { get; } = [];

    public void Respond(string pathAndQuery, HttpResponseMessage response) =>
        _responses[pathAndQuery] = () => response;

    // Built per request; throwing HttpRequestException models a refused connection.
    public void Route(string pathAndQuery, Func<HttpResponseMessage> response) =>
        _responses[pathAndQuery] = response;

    public void RespondBytes(string pathAndQuery, Func<byte[]> body) =>
        _responses[pathAndQuery] = () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body()) };

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
        Requests.Add(request.Method.Method + " " + hostKey);
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

// Stands in for llama-server processes: Start records the launch and makes the
// FakeLlamaServer answer with that alias; Crash(...) makes launches of a
// build die while loading.
public sealed class FakeServerHost : IServerProcessHost
{
    private readonly FakeLlamaServer _server;
    private readonly HashSet<int> _running = [];
    private int _nextPid = 4100;

    public FakeServerHost(FakeLlamaServer server)
    {
        _server = server;
    }

    public sealed record Launch(int Pid, string Executable, IReadOnlyList<string> Arguments)
    {
        public string? Value(string flag) =>
            Arguments.Select((a, i) => (a, i)).Where(x => x.a == flag && x.i + 1 < Arguments.Count)
                .Select(x => Arguments[x.i + 1]).FirstOrDefault();
    }

    public List<Launch> Launches { get; } = [];

    public List<int> Stopped { get; } = [];

    public Func<string, bool> Crashes { get; set; } = _ => false;

    public string? OwnRoot { get; set; }

    public int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var pid = _nextPid++;
        Launches.Add(new Launch(pid, executable, arguments));
        if (Crashes(executable))
        {
            return pid;
        }

        _running.Add(pid);
        var launch = Launches[^1];
        _server.Serve(launch.Value("--alias")!, pid);
        return pid;
    }

    public bool IsRunning(int pid) => _running.Contains(pid);

    public void Stop(int pid)
    {
        Stopped.Add(pid);
        if (_running.Remove(pid) && _server.Pid == pid)
        {
            _server.Down();
        }
    }

    public IReadOnlyList<int> FindUnder(string directory) =>
        _running.Where(pid => Launches.Any(l => l.Pid == pid
            && l.Executable.StartsWith(directory, StringComparison.OrdinalIgnoreCase))).ToList();
}

// The HTTP side of a llama-server on 127.0.0.1:<port>: refused while down,
// /health 200 and /v1/models listing the alias while up.
public sealed class FakeLlamaServer
{
    public FakeLlamaServer(FakeHttpHandler http, int port = 7474)
    {
        var host = $"127.0.0.1:{port}";
        http.Route(host + "/health", () => Alias is null
            ? throw new HttpRequestException("No connection could be made because the target machine actively refused it.")
            : FakeHttpHandler.Json("""{"status":"ok"}"""));
        http.Route(host + "/v1/models", () => Alias is null
            ? throw new HttpRequestException("refused")
            : FakeHttpHandler.Json(System.Text.Json.JsonSerializer.Serialize(new { data = new[] { new { id = Alias } } })));
    }

    public string? Alias { get; private set; }

    public int? Pid { get; private set; }

    public void Serve(string alias, int? pid = null)
    {
        Alias = alias;
        Pid = pid;
    }

    public void Down()
    {
        Alias = null;
        Pid = null;
    }
}
