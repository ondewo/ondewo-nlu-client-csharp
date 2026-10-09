#nullable enable
using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// A throw-away PKI built at test time, so no private key is ever committed: a CA, a server
    /// certificate for localhost / 127.0.0.1 / ::1, a client certificate, a second unrelated CA with
    /// a client certificate of its own, and a server certificate whose SAN names another host.
    /// </summary>
    internal sealed class TlsTestPki
    {
        private static readonly Lazy<TlsTestPki> Shared = new(() => new TlsTestPki());

        private TlsTestPki()
        {
            DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddDays(30);

            (Ca, CaKey) = CreateCa("CN=ONDEWO Test CA", notBefore, notAfter);
            (OtherCa, OtherCaKey) = CreateCa("CN=Unrelated Test CA", notBefore, notAfter);

            var sans = new SubjectAlternativeNameBuilder();
            sans.AddDnsName("localhost");
            sans.AddIpAddress(IPAddress.Loopback);
            sans.AddIpAddress(IPAddress.IPv6Loopback);
            Server = Issue("CN=localhost", Ca, CaKey, sans, serverAuth: true, notBefore, notAfter);

            var otherSans = new SubjectAlternativeNameBuilder();
            otherSans.AddDnsName("other.example");
            WrongNameServer = Issue("CN=other.example", Ca, CaKey, otherSans, serverAuth: true, notBefore, notAfter);

            Client = Issue("CN=ondewo-test-client", Ca, CaKey, null, serverAuth: false, notBefore, notAfter);
            RogueClient = Issue("CN=rogue-client", OtherCa, OtherCaKey, null, serverAuth: false, notBefore, notAfter);
        }

        internal static TlsTestPki Instance => Shared.Value;

        internal X509Certificate2 Ca { get; }

        internal X509Certificate2 OtherCa { get; }

        internal Identity Server { get; }

        internal Identity WrongNameServer { get; }

        internal Identity Client { get; }

        internal Identity RogueClient { get; }

        internal string CaPem => Ca.ExportCertificatePem();

        internal string OtherCaPem => OtherCa.ExportCertificatePem();

        private ECDsa CaKey { get; }

        private ECDsa OtherCaKey { get; }

        private static (X509Certificate2, ECDsa) CreateCa(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return (request.CreateSelfSigned(notBefore, notAfter), key);
        }

        private static Identity Issue(
            string subject,
            X509Certificate2 issuer,
            ECDsa issuerKey,
            SubjectAlternativeNameBuilder? sans,
            bool serverAuth,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            if (sans != null)
            {
                request.CertificateExtensions.Add(sans.Build());
            }

            byte[] serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            X509Certificate2 signed = request.Create(
                issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), notBefore, notAfter, serial);
            return new Identity(signed.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
        }

        /// <summary>A certificate and its private key, both as PEM text.</summary>
        internal sealed record Identity(string CertPem, string KeyPem)
        {
            /// <summary>The identity as a certificate with its key, in a form Kestrel accepts on every platform.</summary>
            internal X509Certificate2 ToCertificate()
            {
                using X509Certificate2 ephemeral = X509Certificate2.CreateFromPem(CertPem, KeyPem);
                return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
            }

            /// <summary>Hides the key, should a failing assertion ever print an identity.</summary>
            public override string ToString() => "Identity { CertPem = <PEM>, KeyPem = ***REDACTED*** }";
        }
    }
}
