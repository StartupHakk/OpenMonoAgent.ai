// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Omabot.Common;

namespace Omabot.Fleet;

/// <summary>A node's own identity: its key and certificate (key made on the node and never sent), the fleet CA, and its slot.</summary>
public sealed record NodeIdentity(string Name, X509Certificate2 Certificate, X509Certificate2 Ca, int Slot);

public sealed record StepContext(string JobId, string StepId, bool Outward, JsonObject Spec, string? SecretsDir);

public sealed record StepOutcome(bool Ok, string? ExternalId = null, string? Text = null, IReadOnlyList<(string Name, byte[] Data)>? Artifacts = null, string? Error = null);

/// <summary>Runs one step of a job on the node.</summary>
public interface IStepRunner
{
    Task<StepOutcome> RunAsync(StepContext step, CancellationToken ct);
}

/// <summary>
/// The default runner: read-only jobs through the node's own OMA agent over
/// ACP on loopback. Outward steps are refused until they are built and
/// reviewed (plan phase 12 starts with read-only jobs).
/// </summary>
public sealed class ReadOnlyStepRunner(Func<Acp.AcpClient> agent) : IStepRunner
{
    public async Task<StepOutcome> RunAsync(StepContext step, CancellationToken ct)
    {
        if (step.Outward) return new(false, Error: "Outward steps are not enabled on this node.");
        var kind = step.Spec["kind"]?.GetValue<string>();
        if (kind != "readonly_query") return new(false, Error: $"This node runs readonly_query jobs only, not {kind}.");
        var prompt = step.Spec["inputs"]?["prompt"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(prompt)) return new(false, Error: "inputs.prompt is required.");
        var acp = agent();
        var session = await acp.CreateSessionAsync(null, ct).ConfigureAwait(false);
        var turn = await acp.SendAsync(session, "Read only. Do not change any file or run anything that changes the system.\n\n" + prompt, null, ct).ConfigureAwait(false);
        return turn.Pause is not null ? new(false, Error: "The agent asked for a permission a read-only job never grants.") : new(true, Text: turn.Text);
    }
}

/// <summary>
/// The fleet node agent (plan 7.21): pulls work from the controller over
/// mutual TLS through the relay, holds a lease with its fencing token, keeps
/// the intent and done ledger for every step, writes per-job secrets only to
/// 0600 files that are removed after the job, and uploads artifacts with their
/// SHA-256. It never approves anything and never sets approved_by.
/// </summary>
public sealed class NodeAgent(NodeIdentity identity, Uri controller, IStepRunner runner, string home, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null, IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    /// <summary>TLS that trusts only the fleet CA and presents this node's certificate.</summary>
    public static SocketsHttpHandler Handler(X509Certificate2? clientCert, Func<X509Certificate2, X509Chain?, bool> trust) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            ClientCertificates = clientCert is null ? null : new X509CertificateCollection { clientCert },
            LocalCertificateSelectionCallback = clientCert is null ? null : (_, _, _, _, _) => clientCert,
            RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                cert is not null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0 && trust(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()), chain),
        },
    };

    public static bool ChainsTo(X509Certificate2 leaf, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    /// <summary>
    /// Enrolls with a one-time code. First it fetches the controller's CA (a
    /// public certificate; nothing secret is sent on that connection) and goes on
    /// only if its fingerprint is the one the operator gave out with the code and
    /// the controller's own certificate chains to it. The code and the request
    /// then go over a connection that trusts that CA alone.
    /// </summary>
    public static async Task<NodeIdentity> EnrollAsync(Uri enrollUrl, string caFingerprint, string node, string code, string home, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null, CancellationToken ct = default)
    {
        var pin = caFingerprint.Trim().ToLowerInvariant();
        X509Certificate2? seen = null;
        var look = Handler(null, (leaf, _) => { seen = leaf; return true; });
        X509Certificate2 ca;
        using (var probe = new HttpClient(wrap?.Invoke(look) ?? look) { Timeout = TimeSpan.FromSeconds(30) })
        {
            var body = await probe.GetFromJsonAsync<JsonObject>(new Uri(enrollUrl, "/fleet/v1/ca"), ct).ConfigureAwait(false);
            ca = X509Certificate2.CreateFromPem(body?["ca"]?.GetValue<string>() ?? throw new FleetException("The controller sent no CA."));
        }
        if (!SecureFile.FixedTimeEquals(FleetCa.Fingerprint(ca), pin) || seen is null || !ChainsTo(seen, ca))
            throw new FleetException("The controller's CA is not the pinned one; nothing was sent.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=" + node, key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var handler = Handler(null, (leaf, _) => ChainsTo(leaf, ca));
        using var http = new HttpClient(wrap?.Invoke(handler) ?? handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var res = await http.PostAsJsonAsync(enrollUrl, new { code, node, csr }, ct).ConfigureAwait(false);
        var reply = await res.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new FleetException("Enrollment refused: " + (reply?["message"]?.GetValue<string>() ?? res.StatusCode.ToString()));
        var cert = X509Certificate2.CreateFromPem(reply!["certificate"]!.GetValue<string>()).CopyWithPrivateKey(key);
        if (!ChainsTo(cert, ca)) throw new FleetException("The controller signed the certificate with another CA.");
        SecureFile.EnsureDir(home);
        SecureFile.WriteAllBytesAtomic(Path.Combine(home, "node.pfx"), cert.Export(X509ContentType.Pfx));
        SecureFile.WriteAllTextAtomic(Path.Combine(home, "ca.pem"), ca.ExportCertificatePem());
        var slot = reply["slot"]!.GetValue<int>();
        SecureFile.WriteAllTextAtomic(Path.Combine(home, "node.json"), new JsonObject { ["name"] = node, ["slot"] = slot }.ToJsonString());
        return new NodeIdentity(node, X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable), ca, slot);
    }

    public static NodeIdentity Load(string home)
    {
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "node.json")))!;
        var pfx = Path.Combine(home, "node.pfx");
        if (!SecureFile.IsPrivate(pfx)) throw new FleetException($"{pfx} must be 0600.");
        return new NodeIdentity(meta["name"]!.GetValue<string>(), X509CertificateLoader.LoadPkcs12FromFile(pfx, null, X509KeyStorageFlags.Exportable),
            X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(home, "ca.pem"))), meta["slot"]!.GetValue<int>());
    }

    private HttpClient Client()
    {
        var handler = Handler(identity.Certificate, (leaf, _) => ChainsTo(leaf, identity.Ca));
        return new HttpClient(wrap?.Invoke(handler) ?? handler) { BaseAddress = controller, Timeout = TimeSpan.FromSeconds(60) };
    }

    private static async Task<(HttpStatusCode Status, JsonObject Body)> Post(HttpClient http, string path, object body, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync(path, body, ct).ConfigureAwait(false);
        var json = res.Content.Headers.ContentLength == 0 || res.StatusCode == HttpStatusCode.NoContent ? [] : await res.Content.ReadFromJsonAsync<JsonObject>(ct).ConfigureAwait(false) ?? [];
        return (res.StatusCode, json);
    }

    private string LedgerFile => Path.Combine(home, "ledger.jsonl");

    /// <summary>Steps this node finished, with their external ids, kept for a person checking a lost job.</summary>
    public IReadOnlyList<string> Ledger() => File.Exists(LedgerFile) ? File.ReadAllLines(LedgerFile) : [];

    /// <summary>One heartbeat and at most one job. Returns the job id it worked on, or null.</summary>
    public async Task<string?> RunOnceAsync(CancellationToken ct = default)
    {
        using var http = Client();
        var (_, beat) = await Post(http, "/fleet/v1/heartbeat", new { version = typeof(NodeAgent).Assembly.GetName().Version?.ToString(), capacity = 1 }, ct).ConfigureAwait(false);
        if (beat["gateway_token"] is JsonValue tv && tv.GetValue<string>() is { Length: > 0 } token)
        {
            // The node's OMA agent reads it as OPENMONO_API_KEY to reach the roster gateway through the relay.
            Redactor.Shared.AddKnownSecret(token);
            var file = Path.Combine(home, "gateway.token");
            if (!File.Exists(file) || File.ReadAllText(file).Trim() != token) SecureFile.WriteAllTextAtomic(file, token + "\n");
        }
        var (status, lease) = await Post(http, "/fleet/v1/lease", new { }, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) return null;
        var jobId = lease["job_id"]!.GetValue<string>();
        var fencing = lease["fencing"]!.GetValue<long>();
        var spec = lease["spec"] as JsonObject ?? [];
        string? secrets = null;
        using var renew = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewer = Task.Run(async () =>
        {
            while (!renew.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), renew.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                await Post(http, $"/fleet/v1/jobs/{jobId}/renew", new { fencing }, CancellationToken.None).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try
        {
            if (spec["secrets"] is JsonArray { Count: > 0 })
            {
                var (s, body) = await Post(http, $"/fleet/v1/jobs/{jobId}/secrets", new { fencing }, ct).ConfigureAwait(false);
                if (s != HttpStatusCode.OK) return jobId;
                secrets = Path.Combine(home, "jobs", jobId, "secrets");
                SecureFile.EnsureDir(secrets);
                foreach (var (name, value) in body["files"] as JsonObject ?? [])
                {
                    var text = value!.GetValue<string>();
                    Redactor.Shared.AddKnownSecret(text);
                    SecureFile.WriteAllTextAtomic(Path.Combine(secrets, name), text);
                }
            }
            var steps = spec["steps"] is JsonArray a && a.Count > 0 ? a.OfType<JsonObject>().Select(x => (Id: x["id"]!.GetValue<string>(), Outward: x["outward"]?.GetValue<bool>() ?? false)).ToList() : [("run", false)];
            var texts = new JsonObject();
            var artifacts = new List<(string Name, byte[] Data)>();
            foreach (var (stepId, outward) in steps)
            {
                var (si, intent) = await Post(http, $"/fleet/v1/jobs/{jobId}/steps/{stepId}", new { phase = "intent", fencing }, ct).ConfigureAwait(false);
                if (si != HttpStatusCode.OK) return jobId;
                if (intent["done"] is JsonValue) continue;
                var outcome = await runner.RunAsync(new StepContext(jobId, stepId, outward, spec, secrets), ct).ConfigureAwait(false);
                if (!outcome.Ok) return jobId;
                File.AppendAllText(LedgerFile, new JsonObject { ["job"] = jobId, ["step"] = stepId, ["external_id"] = outcome.ExternalId, ["at"] = _clock.UtcNow.ToString("O") }.ToJsonString() + "\n");
                SecureFile.TrySetMode(LedgerFile);
                var (sd, _) = await Post(http, $"/fleet/v1/jobs/{jobId}/steps/{stepId}", new { phase = "done", fencing, external_id = outcome.ExternalId }, ct).ConfigureAwait(false);
                if (sd != HttpStatusCode.OK) return jobId;
                if (outcome.Text is not null) texts[stepId] = Redactor.Shared.Redact(outcome.Text);
                if (outcome.Artifacts is not null) artifacts.AddRange(outcome.Artifacts);
            }
            foreach (var (name, data) in artifacts)
            {
                using var content = new ByteArrayContent(data);
                content.Headers.Add("X-Sha256", Convert.ToHexStringLower(SHA256.HashData(data)));
                using var res = await http.PostAsync($"/fleet/v1/jobs/{jobId}/artifacts/{name}?fencing={fencing}", content, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return jobId;
            }
            await Post(http, $"/fleet/v1/jobs/{jobId}/complete", new { fencing, result = new JsonObject { ["steps"] = texts } }, ct).ConfigureAwait(false);
            return jobId;
        }
        finally
        {
            await renew.CancelAsync().ConfigureAwait(false);
            await renewer.ConfigureAwait(false);
            if (secrets is not null && Directory.Exists(secrets)) Directory.Delete(Path.GetDirectoryName(secrets)!, recursive: true);
        }
    }
}
