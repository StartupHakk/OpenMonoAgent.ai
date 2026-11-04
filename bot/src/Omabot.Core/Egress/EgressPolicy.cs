// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omabot.Common;

namespace Omabot.Egress;

/// <summary>One gate client: a bot agent, a bot's command checks, or a desktop. Only the token's hash is in the file.</summary>
public sealed class EgressClient
{
    public string Name { get; set; } = "";
    /// <summary>agent, check or desktop.</summary>
    public string Kind { get; set; } = "agent";
    public string TokenSha256 { get; set; } = "";
    public List<string> Allow { get; set; } = [];
}

/// <summary>
/// The gate's policy file, written by the roster and mounted read only into
/// the gate. It holds token hashes, allowlists, blocked hosts and ports; no
/// secret. Facebook stays blocked whatever the file says (plan SEC-11).
/// </summary>
public sealed class EgressPolicy
{
    public static readonly string[] AlwaysBlocked = ["facebook.com", "fb.com", "messenger.com", "fbcdn.net"];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly JsonSerializerOptions LineJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public int Version { get; set; } = 1;
    public List<int> Ports { get; set; } = [80, 443];
    public List<string> BlockedHosts { get; set; } = [];
    public List<EgressClient> Clients { get; set; } = [];

    public static EgressPolicy Parse(string json) => JsonSerializer.Deserialize<EgressPolicy>(json, Json) ?? new EgressPolicy();

    public string ToJson() => JsonSerializer.Serialize(this, Json) + "\n";

    /// <summary>The client whose name and token both match (fixed-time compare), or null.</summary>
    public EgressClient? Find(string name, string token)
    {
        var hash = SecureFile.Sha256Hex(token);
        EgressClient? found = null;
        foreach (var c in Clients)
            if (SecureFile.FixedTimeEquals(c.TokenSha256, hash) & string.Equals(c.Name, name, StringComparison.Ordinal)) found = c;
        return found;
    }

    public bool Blocked(string host) => AddressPolicy.HostBlocked(host, AlwaysBlocked.Concat(BlockedHosts));

    /// <summary>The checks before any name lookup. Null when the gate may resolve and connect.</summary>
    public string? Refusal(EgressClient client, string host, int port)
    {
        if (!Ports.Contains(port)) return $"port {port} is not allowed";
        var h = AddressPolicy.Normalize(host);
        if (h.Length is 0 or > 253) return "not a host name";
        if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return "IP addresses are never on an allowlist";
        if (!System.Text.RegularExpressions.Regex.IsMatch(h, "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")) return "not a host name";
        if (Blocked(h)) return "blocked host";
        if (!AddressPolicy.HostAllowed(h, client.Allow)) return $"not on {client.Name}'s allowlist";
        return null;
    }
}

/// <summary>Opens the outbound connection to the exact address the gate checked. Swappable in tests.</summary>
public interface IConnector
{
    Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct);
}

public sealed class SocketConnector : IConnector
{
    public static readonly SocketConnector Instance = new();

    public async Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new System.Net.Sockets.Socket(endpoint.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>One gate decision, as a JSON line: who, where, allowed or refused, why. Never a token or a URL path.</summary>
public sealed record EgressLogEntry(string At, string Client, string Kind, string Host, int Port, string Decision, string Reason, string? Address);

/// <summary>The gate's decision log, appended as JSON lines and rotated at a size limit.</summary>
public sealed class EgressLog(string path, IClock clock, long maxBytes = 10 * 1024 * 1024)
{
    private readonly Lock _lock = new();

    public string Path => path;

    public void Write(string client, string kind, string host, int port, bool allowed, string reason, IPAddress? address = null)
    {
        var entry = new EgressLogEntry(clock.UtcNow.ToString("O"), client, kind, host, port, allowed ? "allow" : "refuse", Redactor.Shared.Redact(reason), address?.ToString());
        var line = JsonSerializer.Serialize(entry, EgressPolicy.LineJson) + "\n";
        lock (_lock)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
            }
            catch (IOException)
            {
                // A full disk must not stop refusals; the decision itself still holds.
            }
        }
    }

    public static IEnumerable<EgressLogEntry> Read(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        {
            EgressLogEntry? e = null;
            try { e = JsonSerializer.Deserialize<EgressLogEntry>(line, EgressPolicy.LineJson); }
            catch (JsonException) { }
            if (e is not null) yield return e;
        }
    }
}
