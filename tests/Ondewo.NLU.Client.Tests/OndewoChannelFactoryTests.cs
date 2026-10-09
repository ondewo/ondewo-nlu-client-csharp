#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Ondewo.Nlu.Client.Auth;
using Ondewo.Nlu.Client.Connection;
using Xunit;
using Xunit.Abstractions;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// <see cref="OndewoChannelFactory"/> against real TLS and mutual-TLS handshakes with an
    /// in-process Kestrel server (<see cref="TlsTestServer"/>) and a PKI built at test time
    /// (<see cref="TlsTestPki"/>). A call answered UNIMPLEMENTED reached the server; a failed
    /// handshake surfaces as an <see cref="RpcException"/> the server never saw, never as a crash.
    /// </summary>
    public class OndewoChannelFactoryTests
    {
        private readonly ITestOutputHelper output;

        public OndewoChannelFactoryTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static TlsTestPki Pki => TlsTestPki.Instance;

        // ----------------------------------------------------------------- real handshakes

        [Fact]
        public async Task TlsWithACustomCaReachesTheServer()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, Pki.Server);
            using GrpcChannel channel = OndewoChannelFactory.Create(new OndewoClientConfig("localhost", server.Port, Pki.CaPem));

            await AssertReachesAsync(server, channel);
            Assert.Null(server.LastClientSubject);
        }

        [Fact]
        public async Task TlsByIpAddressReachesTheServer()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, Pki.Server);
            using GrpcChannel channel = OndewoChannelFactory.Create(new OndewoClientConfig("127.0.0.1", server.Port, Pki.CaPem));

            await AssertReachesAsync(server, channel);
        }

        [Fact]
        public async Task EmptyClientIdentityIsPlainTls()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, Pki.Server);
            var config = new OndewoClientConfig("localhost", server.Port, Pki.CaPem, string.Empty, string.Empty);
            using GrpcChannel channel = OndewoChannelFactory.Create(config);

            await AssertReachesAsync(server, channel);
            Assert.Null(server.LastClientSubject);
        }

        [Fact]
        public async Task MutualTlsReachesTheServerWithTheClientIdentity()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(
                IPAddress.Loopback, Pki.Server, requireClientCertificate: true);
            using GrpcChannel channel = OndewoChannelFactory.Create(MutualTlsConfig("localhost", server.Port));

            await AssertReachesAsync(server, channel);
            Assert.Equal("CN=ondewo-test-client", server.LastClientSubject);
        }

        [Fact]
        public async Task MutualTlsCarriesBearerCallCredentials()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(
                IPAddress.Loopback, Pki.Server, requireClientCertificate: true);
            using GrpcChannel channel = OndewoChannelFactory.Create(
                MutualTlsConfig("localhost", server.Port),
                options => options.Credentials = ChannelCredentials.Create(
                    ChannelCredentials.SecureSsl, OndewoAuth.CreateBearerCredentials("a-token")));

            await AssertReachesAsync(server, channel);
            Assert.Equal("Bearer a-token", server.LastAuthorization);
        }

        [Fact]
        public async Task MutualTlsSendsTheChainGivenAfterTheLeaf()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(
                IPAddress.Loopback, Pki.Server, requireClientCertificate: true);
            var config = new OndewoClientConfig(
                "localhost", server.Port, Pki.CaPem, Pki.Client.CertPem + "\n" + Pki.CaPem, Pki.Client.KeyPem);
            using GrpcChannel channel = OndewoChannelFactory.Create(config);

            await AssertReachesAsync(server, channel);
            Assert.Equal("CN=ondewo-test-client", server.LastClientSubject);
        }

        [Fact]
        public async Task MutualTlsWorksWithCrlfPems()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(
                IPAddress.Loopback, Pki.Server, requireClientCertificate: true);
            var config = new OndewoClientConfig(
                "localhost",
                server.Port,
                ToCrlf(Pki.CaPem),
                ToCrlf(Pki.Client.CertPem),
                ToCrlf(Pki.Client.KeyPem));
            Assert.Contains("\r\n", config.GrpcClientKey, StringComparison.Ordinal);
            using GrpcChannel channel = OndewoChannelFactory.Create(config);

            await AssertReachesAsync(server, channel);
        }

        [Fact]
        public async Task MutualTlsOverIpv6Loopback()
        {
            if (!Socket.OSSupportsIPv6)
            {
                output.WriteLine("SKIPPED: this environment has no IPv6 support");
                return;
            }

            TlsTestServer server;
            try
            {
                server = await TlsTestServer.StartAsync(IPAddress.IPv6Loopback, Pki.Server, requireClientCertificate: true);
            }
            catch (IOException exception)
            {
                output.WriteLine($"SKIPPED: cannot listen on [::1]: {exception.Message}");
                return;
            }

            await using (server)
            {
                using GrpcChannel channel = OndewoChannelFactory.Create(MutualTlsConfig("::1", server.Port));

                Assert.Equal($"https://[::1]:{server.Port}/", OndewoChannelFactory.GetAddress(MutualTlsConfig("::1", server.Port)).ToString());
                await AssertReachesAsync(server, channel);
            }
        }

        [Fact]
        public async Task AServerRequiringClientCertificatesRejectsAClientWithout()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(
                IPAddress.Loopback, Pki.Server, requireClientCertificate: true);
            using GrpcChannel channel = OndewoChannelFactory.Create(new OndewoClientConfig("localhost", server.Port, Pki.CaPem));

            await AssertRejectedAsync(server, channel);
        }

        [Fact]
        public async Task AClientIdentityFromAnUnrelatedCaIsRejected()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(
                IPAddress.Loopback, Pki.Server, requireClientCertificate: true);
            var config = new OndewoClientConfig(
                "localhost", server.Port, Pki.CaPem, Pki.RogueClient.CertPem, Pki.RogueClient.KeyPem);
            using GrpcChannel channel = OndewoChannelFactory.Create(config);

            await AssertRejectedAsync(server, channel);
        }

        [Fact]
        public async Task TheWrongCaFailsTheHandshake()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, Pki.Server);
            using GrpcChannel channel = OndewoChannelFactory.Create(new OndewoClientConfig("localhost", server.Port, Pki.OtherCaPem));

            RpcException exception = await AssertRejectedAsync(server, channel);
            AssertClientRefusedTheServerCertificate(exception);
        }

        [Fact]
        public async Task WithoutGrpcCertTheSystemTrustStoreIsUsed()
        {
            // The test CA is in no system trust store, so trusting the platform's roots must fail.
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, Pki.Server);
            using GrpcChannel channel = OndewoChannelFactory.Create(new OndewoClientConfig("localhost", server.Port));

            RpcException exception = await AssertRejectedAsync(server, channel);
            AssertClientRefusedTheServerCertificate(exception);
        }

        [Fact]
        public async Task AServerCertificateForAnotherHostIsRejected()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, Pki.WrongNameServer);
            using GrpcChannel channel = OndewoChannelFactory.Create(new OndewoClientConfig("localhost", server.Port, Pki.CaPem));

            RpcException exception = await AssertRejectedAsync(server, channel);
            AssertClientRefusedTheServerCertificate(exception);
        }

        [Fact]
        public async Task AnInsecureChannelReachesAPlaintextServer()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, null);
            using GrpcChannel channel = OndewoChannelFactory.Create(
                new OndewoClientConfig("localhost", server.Port, useSecureChannel: false));

            await AssertReachesAsync(server, channel);
        }

        // ----------------------------------------------------------------- PEM loading errors

        [Fact]
        public void AFilePathInGrpcCertIsRefusedNamingTheField()
        {
            var config = new OndewoClientConfig("localhost", 50055, "certs/ca.pem");

            ArgumentException exception = Assert.Throws<ArgumentException>(() => OndewoChannelFactory.Create(config));

            Assert.Contains("GrpcCert", exception.Message, StringComparison.Ordinal);
            Assert.Contains("not a file path", exception.Message, StringComparison.Ordinal);
            Assert.Contains("localhost:50055", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMalformedPemCertificateIsRefusedWithoutRenderingIt()
        {
            string broken = "-----BEGIN CERTIFICATE-----\nbm90IGEgY2VydGlmaWNhdGU=\n-----END CERTIFICATE-----\n";
            var config = new OndewoClientConfig("localhost", 50055, broken);

            ArgumentException exception = Assert.Throws<ArgumentException>(() => OndewoChannelFactory.Create(config));

            Assert.Contains("malformed PEM certificate", exception.Message, StringComparison.Ordinal);
            Assert.IsType<CryptographicException>(exception.InnerException);
            OndewoClientConfigTests.AssertCarriesNoSecret(exception.ToString());
        }

        [Fact]
        public void AKeyThatDoesNotMatchTheCertificateIsRefusedWithoutRenderingIt()
        {
            var config = new OndewoClientConfig(
                "localhost", 50055, Pki.CaPem, Pki.Client.CertPem, Pki.RogueClient.KeyPem);

            ArgumentException exception = Assert.Throws<ArgumentException>(() => OndewoChannelFactory.Create(config));

            Assert.Contains("GrpcClientCert / GrpcClientKey", exception.Message, StringComparison.Ordinal);
            Assert.Contains("localhost:50055", exception.Message, StringComparison.Ordinal);
            Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
            OndewoClientConfigTests.AssertCarriesNoSecret(exception.ToString());
            Assert.DoesNotContain(Pki.RogueClient.KeyPem.Split('\n')[1], exception.ToString(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("not a key")]
        [InlineData("encrypted")]
        public void AMalformedOrEncryptedKeyIsRefusedWithoutRenderingIt(string kind)
        {
            string key = kind == "encrypted"
                ? EncryptedKeyPem()
                : kind;
            var config = new OndewoClientConfig("localhost", 50055, Pki.CaPem, Pki.Client.CertPem, key);

            ArgumentException exception = Assert.Throws<ArgumentException>(() => OndewoChannelFactory.Create(config));

            Assert.Contains("GrpcClientCert / GrpcClientKey", exception.Message, StringComparison.Ordinal);
            Assert.IsType<CryptographicException>(exception.InnerException);
            OndewoClientConfigTests.AssertCarriesNoSecret(exception.ToString());
        }

        [Fact]
        public void AClientCertificateFieldWithoutACertificateIsRefused()
        {
            var config = new OndewoClientConfig("localhost", 50055, Pki.CaPem, "client.pem", Pki.Client.KeyPem);

            ArgumentException exception = Assert.Throws<ArgumentException>(() => OndewoChannelFactory.Create(config));

            Assert.Contains("GrpcClientCert", exception.Message, StringComparison.Ordinal);
            Assert.Contains("not a file path", exception.Message, StringComparison.Ordinal);
            OndewoClientConfigTests.AssertCarriesNoSecret(exception.ToString());
        }

        // ----------------------------------------------------------------- channel and handler options

        [Fact]
        public void TheHandlerCarriesThePythonKeepaliveDefaults()
        {
            using SocketsHttpHandler handler = OndewoChannelFactory.CreateHttpHandler(new OndewoClientConfig("localhost", 50055));

            Assert.Equal(TimeSpan.FromSeconds(30), handler.KeepAlivePingDelay);
            Assert.Equal(TimeSpan.FromSeconds(20), handler.KeepAlivePingTimeout);
            Assert.Equal(HttpKeepAlivePingPolicy.WithActiveRequests, handler.KeepAlivePingPolicy);
            Assert.True(handler.EnableMultipleHttp2Connections);
            Assert.Equal(Timeout.InfiniteTimeSpan, handler.PooledConnectionIdleTimeout);
        }

        [Fact]
        public void TheHandlerTrustsOnlyTheGivenCaAndPresentsTheIdentity()
        {
            using SocketsHttpHandler handler = OndewoChannelFactory.CreateHttpHandler(MutualTlsConfig("localhost", 50055));

            Assert.NotNull(handler.SslOptions.CertificateChainPolicy);
            Assert.Equal(
                System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust,
                handler.SslOptions.CertificateChainPolicy!.TrustMode);
            Assert.Single(handler.SslOptions.CertificateChainPolicy.CustomTrustStore);
            Assert.NotNull(handler.SslOptions.ClientCertificateContext);
            Assert.Equal("CN=ondewo-test-client", handler.SslOptions.ClientCertificateContext!.TargetCertificate.Subject);
        }

        [Fact]
        public void WithoutGrpcCertTheHandlerKeepsThePlatformTrust()
        {
            using SocketsHttpHandler handler = OndewoChannelFactory.CreateHttpHandler(new OndewoClientConfig("localhost", 50055));

            Assert.Null(handler.SslOptions.CertificateChainPolicy);
            Assert.Null(handler.SslOptions.ClientCertificateContext);
        }

        [Fact]
        public void AnInsecureHandlerHasNoTlsSettings()
        {
            using SocketsHttpHandler handler = OndewoChannelFactory.CreateHttpHandler(
                new OndewoClientConfig("localhost", 50055, Pki.CaPem, useSecureChannel: false));

            Assert.Null(handler.SslOptions.CertificateChainPolicy);
        }

        [Fact]
        public void CreateAppliesTheDefaultsBeforeConfigure()
        {
            GrpcChannelOptions? seen = null;

            using GrpcChannel channel = OndewoChannelFactory.Create(
                new OndewoClientConfig("localhost", 50055), options => seen = options);

            Assert.NotNull(seen);
            Assert.Equal(int.MaxValue, seen!.MaxReceiveMessageSize);
            Assert.Equal(int.MaxValue, seen.MaxSendMessageSize);
            Assert.Equal(TimeSpan.FromSeconds(5), seen.MaxReconnectBackoff);
            Assert.IsType<SocketsHttpHandler>(seen.HttpHandler);
            Assert.True(seen.DisposeHttpClient);
            Assert.Equal("localhost:50055", channel.Target);
        }

        [Fact]
        public void AFailingConfigureCallbackPropagates()
        {
            var failure = new InvalidOperationException("configure failed");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                OndewoChannelFactory.Create(new OndewoClientConfig("localhost", 50055), _ => throw failure));

            Assert.Same(failure, exception);
        }

        [Theory]
        [InlineData(true, "https://localhost:50055/")]
        [InlineData(false, "http://localhost:50055/")]
        public void GetAddressPicksTheSchemeFromUseSecureChannel(bool secure, string expected)
        {
            var config = new OndewoClientConfig("localhost", 50055, useSecureChannel: secure);

            Assert.Equal(expected, OndewoChannelFactory.GetAddress(config).ToString());
        }

        [Fact]
        public void AHostThatFormsNoAddressIsRefused()
        {
            var config = new OndewoClientConfig("not a host", 50055);

            ArgumentException exception = Assert.Throws<ArgumentException>(() => OndewoChannelFactory.Create(config));

            Assert.Contains("Host", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void NullConfigIsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => OndewoChannelFactory.Create(null!));
            Assert.Throws<ArgumentNullException>(() => OndewoChannelFactory.GetAddress(null!));
            Assert.Throws<ArgumentNullException>(() => OndewoChannelFactory.CreateHttpHandler(null!));
        }

        // ----------------------------------------------------------------- logging

        [Fact]
        public void AnInsecureChannelLogsAWarningNamingHostAndPort()
        {
            var loggerFactory = new RecordingLoggerFactory();

            using GrpcChannel channel = OndewoChannelFactory.Create(
                new OndewoClientConfig("10.0.0.5", 50055, useSecureChannel: false),
                options => options.LoggerFactory = loggerFactory);

            (string category, LogLevel level, string message) = Assert.Single(
                loggerFactory.Entries, entry => entry.Category == typeof(OndewoChannelFactory).FullName);
            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains("INSECURE", message, StringComparison.Ordinal);
            Assert.Contains("10.0.0.5:50055", message, StringComparison.Ordinal);
            Assert.NotNull(category);
        }

        [Fact]
        public void AnInsecureChannelWithoutALoggerFactoryStillOpens()
        {
            using GrpcChannel channel = OndewoChannelFactory.Create(
                new OndewoClientConfig("localhost", 50055, useSecureChannel: false));

            Assert.Equal("localhost:50055", channel.Target);
        }

        [Fact]
        public void ASecureChannelLogsNoInsecureWarning()
        {
            var loggerFactory = new RecordingLoggerFactory();

            using GrpcChannel channel = OndewoChannelFactory.Create(
                new OndewoClientConfig("localhost", 50055, Pki.CaPem),
                options => options.LoggerFactory = loggerFactory);

            Assert.DoesNotContain(loggerFactory.Entries, entry => entry.Category == typeof(OndewoChannelFactory).FullName);
        }

        // ----------------------------------------------------------------- helpers

        private static OndewoClientConfig MutualTlsConfig(string host, int port) =>
            new(host, port, Pki.CaPem, Pki.Client.CertPem, Pki.Client.KeyPem);

        private static string ToCrlf(string pem) => pem.Replace("\r\n", "\n").Replace("\n", "\r\n");

        private static async Task AssertReachesAsync(TlsTestServer server, GrpcChannel channel)
        {
            RpcException exception = await TlsTestServer.CallAsync(channel);

            Assert.True(
                exception.StatusCode == StatusCode.Unimplemented,
                $"expected the call to reach the server (UNIMPLEMENTED), got {exception.Status}");
            Assert.Equal(1, server.Hits);
        }

        private static string EncryptedKeyPem()
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return key.ExportEncryptedPkcs8PrivateKeyPem(
                "password", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
        }

        /// <summary>
        /// Grpc.Net.Client reports a certificate the client refused as INTERNAL (grpc-core says
        /// UNAVAILABLE); either way the cause is an <see cref="AuthenticationException"/>.
        /// </summary>
        private static void AssertClientRefusedTheServerCertificate(RpcException exception)
        {
            Assert.Contains(exception.StatusCode, new[] { StatusCode.Internal, StatusCode.Unavailable });
            Exception? cause = exception.Status.DebugException;
            while (cause != null && cause is not AuthenticationException)
            {
                cause = cause.InnerException;
            }

            Assert.True(cause != null, $"expected an AuthenticationException behind {exception.Status}");
        }

        private static async Task<RpcException> AssertRejectedAsync(TlsTestServer server, GrpcChannel channel)
        {
            RpcException exception = await TlsTestServer.CallAsync(channel);

            Assert.NotEqual(StatusCode.Unimplemented, exception.StatusCode);
            Assert.Equal(0, server.Hits);
            return exception;
        }

        private sealed class RecordingLoggerFactory : ILoggerFactory
        {
            internal List<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

            public void AddProvider(ILoggerProvider provider)
            {
            }

            public void Dispose()
            {
            }

            private sealed class RecordingLogger : ILogger
            {
                private readonly RecordingLoggerFactory factory;
                private readonly string category;

                internal RecordingLogger(RecordingLoggerFactory factory, string category)
                {
                    this.factory = factory;
                    this.category = category;
                }

                public IDisposable? BeginScope<TState>(TState state)
                    where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    lock (factory.Entries)
                    {
                        factory.Entries.Add((category, logLevel, formatter(state, exception)));
                    }
                }
            }
        }
    }
}
