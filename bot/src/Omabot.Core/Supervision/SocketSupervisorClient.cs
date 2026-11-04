// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Omabot.Containers;

namespace Omabot.Supervision;

/// <summary>Talks to the supervisor exe over run/supervisor.sock with the supervisor token.</summary>
public sealed class SocketSupervisorClient : ISupervisorClient, IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public SocketSupervisorClient(string socketPath, string token)
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
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://supervisor/"), Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose() => _http.Dispose();

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync("ping", ct).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<string> EnsureRunningAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync("containers", spec, Json, ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ContainerEngineException("Supervisor refused: " + Common.Redactor.Shared.Redact(body), (int)r.StatusCode);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        using var r = await _http.PostAsync($"containers/{Uri.EscapeDataString(name)}/stop", null, ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ContainerEngineException("Supervisor refused stop", (int)r.StatusCode);
    }

    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        using var r = await _http.DeleteAsync($"containers/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ContainerEngineException("Supervisor refused remove", (int)r.StatusCode);
    }

    public async Task EnsureNetworksAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync("networks", null, ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ContainerEngineException("Supervisor refused: " + Common.Redactor.Shared.Redact(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), (int)r.StatusCode);
    }

    public async Task<string> EnsureServiceAsync(ServiceContainerSpec spec, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync("services", spec, Json, ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ContainerEngineException("Supervisor refused: " + Common.Redactor.Shared.Redact(body), (int)r.StatusCode);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task StopServiceAsync(string name, CancellationToken ct = default)
    {
        using var r = await _http.PostAsync($"services/{Uri.EscapeDataString(name)}/stop", null, ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ContainerEngineException("Supervisor refused stop", (int)r.StatusCode);
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<ContainerInfo>>("containers", Json, ct).ConfigureAwait(false) ?? [];

    public async Task<Routines.CommandCheckResult> RunCheckAsync(CheckContainerSpec spec, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync("checks", spec, Json, ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new ContainerEngineException("Supervisor refused the check: " + Common.Redactor.Shared.Redact(body), (int)r.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var j = doc.RootElement;
        long? exit = j.TryGetProperty("exit_code", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;
        var output = j.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
        var error = j.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
        return new Routines.CommandCheckResult(exit, output, error);
    }

    public async Task<string?> ImageIdAsync(string image, CancellationToken ct = default)
    {
        using var r = await _http.GetAsync("images/" + Uri.EscapeDataString(image), ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("id").GetString();
    }
}
