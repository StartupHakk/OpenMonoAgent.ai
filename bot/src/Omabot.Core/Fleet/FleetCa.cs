// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Omabot.Common;

namespace Omabot.Fleet;

/// <summary>
/// The fleet controller's private CA (plan 7.21): it signs each node's client
/// certificate from the node's own key (the key never leaves the node) and the
/// controller's server certificate. Fleet calls use mutual TLS end to end
/// inside the relay tunnel, and only certificates from this CA are accepted.
/// Kept in secrets/fleet (0600).
/// </summary>
public sealed class FleetCa
{
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public static readonly TimeSpan ClientLifetime = TimeSpan.FromDays(30);

    public X509Certificate2 Ca { get; }
    private readonly IClock _clock;

    private FleetCa(X509Certificate2 ca, IClock clock) { Ca = ca; _clock = clock; }

    public static string Dir(RosterPaths p) => Path.Combine(p.Secrets, "fleet");

    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.RawData));

    public static FleetCa LoadOrCreate(RosterPaths paths, IClock clock)
    {
        var dir = Dir(paths);
        SecureFile.EnsureDirNoClobber(dir);
        var file = Path.Combine(dir, "ca.pfx");
        if (File.Exists(file))
        {
            if (new FileInfo(file).LinkTarget is not null || !SecureFile.IsPrivate(file)) throw new InvalidOperationException($"{file} must be a private regular file (0600).");
            return new FleetCa(X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable), clock);
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=oma-bot fleet CA", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = clock.UtcNow;
        using var ca = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(3));
        SecureFile.WriteAllBytesAtomic(file, ca.Export(X509ContentType.Pfx));
        return new FleetCa(X509CertificateLoader.LoadPkcs12(ca.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable), clock);
    }

    /// <summary>The controller's TLS certificate for the given name, made once and kept beside the CA.</summary>
    public X509Certificate2 ServerCertificate(RosterPaths paths, string dnsName)
    {
        var file = Path.Combine(Dir(paths), "server.pfx");
        if (File.Exists(file))
        {
            var have = X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable);
            if (have.NotAfter > _clock.UtcNow.AddDays(7) && have.GetNameInfo(X509NameType.DnsName, false) == dnsName) return have;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + dnsName, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());
        var now = _clock.UtcNow;
        using var signed = req.Create(Ca, now.AddMinutes(-5), now.AddDays(365), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pfx);
        SecureFile.WriteAllBytesAtomic(file, pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Signs a node's certificate request. The request must be signed by its own key and name exactly this node.</summary>
    public X509Certificate2 SignNode(string node, string csrPem)
    {
        var req = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        if (req.SubjectName.Name != "CN=" + node) throw new InvalidOperationException("The certificate request does not name this node.");
        var issued = new CertificateRequest(new X500DistinguishedName("CN=" + node), req.PublicKey, HashAlgorithmName.SHA256);
        issued.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        issued.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        issued.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("omabot-node:" + node));
        issued.CertificateExtensions.Add(san.Build());
        var now = _clock.UtcNow;
        return issued.Create(Ca.SubjectName, X509SignatureGenerator.CreateForECDsa(Ca.GetECDsaPrivateKey()!), now.AddMinutes(-5), now + ClientLifetime, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>True when the certificate chains to this CA only, is in its validity window and is a client certificate.</summary>
    public bool VerifyClient(X509Certificate2? cert)
    {
        if (cert is null) return false;
        if (cert.NotBefore > _clock.UtcNow.UtcDateTime || cert.NotAfter < _clock.UtcNow.UtcDateTime) return false;
        if (!cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthOid))) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _clock.UtcNow.UtcDateTime;
        chain.ChainPolicy.VerificationTimeIgnored = false;
        return chain.Build(cert) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(Ca.RawData);
    }

    public string CaPem() => Ca.ExportCertificatePem();
}
