// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omabot.Common;
using Omabot.Config;

namespace Omabot.HostExec;

/// <summary>A pairing invite for one bot container, minted by agent-host.</summary>
public sealed record HostInvite(string DaemonUrl, string Invite, DateTimeOffset ExpiresAt, string HostWorkspace);

public interface IHostInviteSource
{
    /// <summary>A fresh invite, or the plain reason none could be minted.</summary>
    Task<(HostInvite? Invite, string? Problem)> MintAsync(string botSlug, string hostWorkspace, CancellationToken ct);
}

/// <summary>
/// Mints agent-host invites for bots whose team entry sets host_exec (plan
/// 7.6 "Host exec"). The roster never runs host commands itself and never
/// hands a bot the pair secret: it reads pair_secret from agent-host's own
/// 0600 config on this host, asks agent-host on loopback for a time-limited
/// invite, and passes only the invite into the container
/// (OPENMONO_HOST_INVITE), as OMA's own launcher does. agent-host keeps its
/// deny list, sudo policy and audit trail.
/// </summary>
public sealed class HostBridgeClient(RosterConfig config, IClock clock, Func<HttpClient>? http = null) : IHostInviteSource
{
    private sealed record InviteBody([property: JsonPropertyName("ttl_seconds")] int TtlSeconds);
    private sealed record InviteReply([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("invite")] string? Invite, [property: JsonPropertyName("error")] string? Error);

    public string BridgeConfigPath => config.HostExec.BridgeConfig is { Length: > 0 } p
        ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openmono", "host-bridge.json");

    public string BotUrl => config.HostExec.BotUrl is { Length: > 0 } u ? u : $"http://{config.Gateway.BindAddress}:7476";

    /// <summary>pair_secret from the environment or agent-host's config, which must be a regular 0600 file owned by this user.</summary>
    public (string? Secret, string? Problem) ReadSecret()
    {
        var env = Environment.GetEnvironmentVariable("OPENMONO_HOST_PAIR_SECRET");
        if (!string.IsNullOrWhiteSpace(env)) return (env.Trim(), null);
        var path = BridgeConfigPath;
        if (!File.Exists(path)) return (null, $"agent-host is not set up ({path} is missing); run it once on this host");
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) return (null, $"{path} is a symlink; refusing to read it");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            return (null, $"{path} must be readable by its owner only (chmod 600)");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var secret = doc.RootElement.TryGetProperty("pair_secret", out var s) ? s.GetString() : null;
            return string.IsNullOrWhiteSpace(secret) ? (null, $"{path} has no pair_secret") : (secret, null);
        }
        catch (JsonException)
        {
            return (null, $"{path} is not valid JSON");
        }
    }

    public async Task<(HostInvite? Invite, string? Problem)> MintAsync(string botSlug, string hostWorkspace, CancellationToken ct)
    {
        var (secret, problem) = ReadSecret();
        if (secret is null) return (null, problem);
        Redactor.Shared.AddKnownSecret(secret);
        using var client = http?.Invoke() ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Post, config.HostExec.AdminUrl.TrimEnd('/') + "/admin/invite")
        {
            Content = JsonContent.Create(new InviteBody(config.HostExec.InviteTtlSeconds)),
        };
        req.Headers.Add("X-Pair-Secret", secret);
        try
        {
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            var reply = await resp.Content.ReadFromJsonAsync<InviteReply>(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || reply is not { Ok: true, Invite: { Length: > 0 } invite })
                return (null, $"agent-host refused the invite ({(int)resp.StatusCode}{(reply?.Error is { } e ? ": " + Redactor.Shared.Redact(e) : "")})");
            Redactor.Shared.AddKnownSecret(invite);
            return (new HostInvite(BotUrl, invite, clock.UtcNow.AddSeconds(config.HostExec.InviteTtlSeconds), hostWorkspace), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (null, "agent-host is not reachable on " + config.HostExec.AdminUrl + ": " + Redactor.Shared.Redact(ex.Message));
        }
    }
}
