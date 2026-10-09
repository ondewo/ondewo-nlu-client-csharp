#nullable enable
using System;
using Ondewo.Nlu.Client.Connection;
using Xunit;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// The validation, rendering and addressing rules of <see cref="OndewoClientConfig"/>, which
    /// mirror the ONDEWO Python client's <c>BaseClientConfig</c>.
    /// </summary>
    public class OndewoClientConfigTests
    {
        private static TlsTestPki Pki => TlsTestPki.Instance;

        [Fact]
        public void DefaultsToASecureChannelWithoutCertificates()
        {
            var config = new OndewoClientConfig("localhost", 50055);

            Assert.Equal("localhost", config.Host);
            Assert.Equal(50055, config.Port);
            Assert.True(config.UseSecureChannel);
            Assert.Equal(string.Empty, config.GrpcCert);
            Assert.Equal(string.Empty, config.GrpcClientCert);
            Assert.Equal(string.Empty, config.GrpcClientKey);
            Assert.False(config.HasClientIdentity);
        }

        [Fact]
        public void BothHalvesMakeAClientIdentity()
        {
            var config = new OndewoClientConfig(
                "localhost", 50055, Pki.CaPem, Pki.Client.CertPem, Pki.Client.KeyPem);

            Assert.True(config.HasClientIdentity);
            Assert.Equal(Pki.CaPem, config.GrpcCert);
            Assert.Equal(Pki.Client.CertPem, config.GrpcClientCert);
            Assert.Equal(Pki.Client.KeyPem, config.GrpcClientKey);
        }

        [Fact]
        public void EmptyStringsOnBothHalvesMeanNoClientIdentity()
        {
            var config = new OndewoClientConfig("localhost", 50055, Pki.CaPem, string.Empty, string.Empty);

            Assert.False(config.HasClientIdentity);
        }

        [Theory]
        [InlineData("localhost", "localhost:50055")]
        [InlineData("10.0.0.5", "10.0.0.5:50055")]
        [InlineData("::1", "[::1]:50055")]
        [InlineData("2001:db8::7", "[2001:db8::7]:50055")]
        [InlineData("[::1]", "[::1]:50055")]
        [InlineData("nlu.example.com", "nlu.example.com:50055")]
        public void HostAndPortBracketsOnlyABareIpv6Literal(string host, string expected)
        {
            Assert.Equal(expected, new OndewoClientConfig(host, 50055).HostAndPort);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void RefusesAnEmptyHost(string host)
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() => new OndewoClientConfig(host, 50055));

            Assert.Equal("host", exception.ParamName);
            Assert.Contains("Host", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(65536)]
        public void RefusesAPortOutOfRange(int port)
        {
            ArgumentOutOfRangeException exception =
                Assert.Throws<ArgumentOutOfRangeException>(() => new OndewoClientConfig("localhost", port));

            Assert.Equal("port", exception.ParamName);
        }

        [Fact]
        public void RefusesACertificateWithoutItsKey()
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                new OndewoClientConfig("localhost", 50055, Pki.CaPem, grpcClientCert: Pki.Client.CertPem));

            Assert.Contains("only GrpcClientCert", exception.Message, StringComparison.Ordinal);
            Assert.Contains("localhost:50055", exception.Message, StringComparison.Ordinal);
            AssertCarriesNoSecret(exception.Message);
        }

        [Fact]
        public void RefusesAKeyWithoutItsCertificate()
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                new OndewoClientConfig("localhost", 50055, Pki.CaPem, grpcClientKey: Pki.Client.KeyPem));

            Assert.Contains("only GrpcClientKey", exception.Message, StringComparison.Ordinal);
            AssertCarriesNoSecret(exception.Message);
        }

        [Fact]
        public void RefusesAKeyNextToAnEmptyCertificate()
        {
            Assert.Throws<ArgumentException>(() =>
                new OndewoClientConfig("localhost", 50055, null, "  ", Pki.Client.KeyPem));
        }

        [Fact]
        public void RefusesAClientIdentityOnAnInsecureChannel()
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(() => new OndewoClientConfig(
                "localhost", 50055, null, Pki.Client.CertPem, Pki.Client.KeyPem, useSecureChannel: false));

            Assert.Contains("UseSecureChannel", exception.Message, StringComparison.Ordinal);
            Assert.Contains("localhost:50055", exception.Message, StringComparison.Ordinal);
            AssertCarriesNoSecret(exception.Message);
        }

        [Fact]
        public void AllowsAnInsecureChannelWithoutAClientIdentity()
        {
            var config = new OndewoClientConfig("localhost", 50055, useSecureChannel: false);

            Assert.False(config.UseSecureChannel);
        }

        [Fact]
        public void ToStringRedactsTheKeyAndRendersNoPem()
        {
            var config = new OndewoClientConfig(
                "nlu.example.com", 50055, Pki.CaPem, Pki.Client.CertPem, Pki.Client.KeyPem);

            string rendered = config.ToString();

            Assert.Contains("Host = nlu.example.com", rendered, StringComparison.Ordinal);
            Assert.Contains("Port = 50055", rendered, StringComparison.Ordinal);
            Assert.Contains($"GrpcClientKey = {OndewoClientConfig.Redacted}", rendered, StringComparison.Ordinal);
            Assert.Contains($"GrpcCert = <PEM, {Pki.CaPem.Length} chars>", rendered, StringComparison.Ordinal);
            Assert.Contains("UseSecureChannel = True", rendered, StringComparison.Ordinal);
            AssertCarriesNoSecret(rendered);
        }

        [Fact]
        public void ToStringRendersAnEmptySecretAsEmpty()
        {
            string rendered = new OndewoClientConfig("localhost", 50055).ToString();

            Assert.Contains("GrpcClientKey = ,", rendered, StringComparison.Ordinal);
            Assert.Contains("GrpcCert = ,", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(OndewoClientConfig.Redacted, rendered, StringComparison.Ordinal);
        }

        /// <summary>No PEM block - certificate or key - and no base64 body of the test key.</summary>
        internal static void AssertCarriesNoSecret(string text)
        {
            Assert.DoesNotContain("-----BEGIN", text, StringComparison.Ordinal);
            string keyBody = Pki.Client.KeyPem.Split('\n')[1];
            Assert.DoesNotContain(keyBody, text, StringComparison.Ordinal);
        }
    }
}
