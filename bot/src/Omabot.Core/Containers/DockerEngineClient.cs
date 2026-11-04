// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Omabot.Containers;

/// <summary>
/// Docker Engine API over the local unix socket. Only the supervisor process
/// uses this; the roster service and bots never hold the docker socket.
/// </summary>
public sealed class DockerEngineClient : IContainerEngine, IDisposable
{
    private const string Api = "http://docker/v1.43";
    private readonly HttpClient _http;

    public DockerEngineClient(string socketPath = "/var/run/docker.sock")
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose() => _http.Dispose();

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync(Api + "/_ping", ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<ContainerInfo?> InspectAsync(string name, CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"{Api}/containers/{Uri.EscapeDataString(name)}/json", ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        await Ensure(r, ct).ConfigureAwait(false);
        var j = (await r.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false))!;
        var state = j["State"]!;
        return new ContainerInfo(
            j["Id"]!.GetValue<string>(),
            j["Name"]!.GetValue<string>().TrimStart('/'),
            state["Status"]?.GetValue<string>() ?? "unknown",
            state["Running"]?.GetValue<bool>() ?? false,
            Labels(j["Config"]?["Labels"]),
            j["Image"]?.GetValue<string>());
    }

    public async Task<string> CreateAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        var problems = spec.SafetyProblems();
        if (problems.Count > 0) throw new ContainerEngineException("Refused unsafe container spec: " + string.Join("; ", problems));
        using var content = new StringContent(spec.ToEngineJson().ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var r = await _http.PostAsync($"{Api}/containers/create?name={Uri.EscapeDataString(spec.Name)}", content, ct).ConfigureAwait(false);
        await Ensure(r, ct).ConfigureAwait(false);
        var j = (await r.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false))!;
        return j["Id"]!.GetValue<string>();
    }

    public async Task StartAsync(string idOrName, CancellationToken ct = default)
    {
        using var r = await _http.PostAsync($"{Api}/containers/{Uri.EscapeDataString(idOrName)}/start", null, ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotModified) return;
        await Ensure(r, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(string idOrName, int timeoutSeconds = 20, CancellationToken ct = default)
    {
        using var r = await _http.PostAsync($"{Api}/containers/{Uri.EscapeDataString(idOrName)}/stop?t={timeoutSeconds}", null, ct).ConfigureAwait(false);
        if (r.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound) return;
        await Ensure(r, ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string idOrName, CancellationToken ct = default)
    {
        using var r = await _http.DeleteAsync($"{Api}/containers/{Uri.EscapeDataString(idOrName)}?force=true", ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return;
        await Ensure(r, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(string labelFilter, CancellationToken ct = default)
    {
        var filters = JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = [labelFilter] });
        using var r = await _http.GetAsync($"{Api}/containers/json?all=true&filters={Uri.EscapeDataString(filters)}", ct).ConfigureAwait(false);
        await Ensure(r, ct).ConfigureAwait(false);
        var arr = (await r.Content.ReadFromJsonAsync<JsonArray>(ct).ConfigureAwait(false))!;
        return [.. arr.Select(j => new ContainerInfo(
            j!["Id"]!.GetValue<string>(),
            (j["Names"]?[0]?.GetValue<string>() ?? "").TrimStart('/'),
            j["State"]?.GetValue<string>() ?? "unknown",
            j["State"]?.GetValue<string>() == "running",
            Labels(j["Labels"]),
            j["ImageID"]?.GetValue<string>()))];
    }

    public async Task<string?> ImageIdAsync(string image, CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"{Api}/images/{Uri.EscapeDataString(image)}/json", ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        await Ensure(r, ct).ConfigureAwait(false);
        var j = (await r.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false))!;
        return j["Id"]?.GetValue<string>();
    }

    public async Task EnsureNetworkAsync(string name, CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"{Api}/networks/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        if (r.IsSuccessStatusCode) return;
        var body = new JsonObject { ["Name"] = name, ["Driver"] = "bridge", ["CheckDuplicate"] = true };
        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var c = await _http.PostAsync($"{Api}/networks/create", content, ct).ConfigureAwait(false);
        if (c.StatusCode == HttpStatusCode.Conflict) return;
        await Ensure(c, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> Labels(JsonNode? node)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject o)
            foreach (var (k, v) in o) d[k] = v?.GetValue<string>() ?? "";
        return d;
    }

    private static async Task Ensure(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ContainerEngineException($"Docker said {(int)r.StatusCode}: {Common.Redactor.Shared.Redact(text.Trim())}", (int)r.StatusCode);
    }
}
