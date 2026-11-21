#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Ondewo.Nlu.Client.Auth;
using Ondewo.Nlu.Client.Connection;
using Xunit;
using static Ondewo.Nlu.Client.Tests.KeycloakTestSupport;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// The Keycloak half of <see cref="OndewoClientConfig"/> (validation, redaction: T55-T62), the shared
    /// <see cref="KeycloakTokenProviderRegistry"/> (T48-T54), the real HTTP transport against an in-process
    /// token endpoint, and the bearer token <see cref="OndewoChannelFactory"/> attaches to every call.
    /// </summary>
    public class KeycloakConfigAndRegistryTests
    {
        private static OndewoClientConfig Config(
            string? username = Username,
            string? password = Password,
            string? refreshToken = null,
            string url = KeycloakTestSupport.Url,
            string realm = KeycloakTestSupport.Realm,
            int? tokenExpirationInS = null,
            bool verifySsl = true,
            string host = "localhost",
            int port = 50055,
            bool useSecureChannel = true,
            string? grpcCert = null)
        {
            return new OndewoClientConfig(
                host, port, grpcCert, useSecureChannel: useSecureChannel, keycloakUrl: url, realm: realm,
                clientId: ClientId, username: username, password: password, tokenExpirationInS: tokenExpirationInS,
                verifySsl: verifySsl, refreshToken: refreshToken);
        }

        private static string TokenBody(string accessToken, double expiresIn = 300) =>
            $"{{\"access_token\":\"{accessToken}\",\"expires_in\":{expiresIn},\"refresh_token\":\"off-1\"}}";

        // ---------------------------------------------------------------- K. config

        [Fact]
        public void T55_RefreshTokenIsTheLastParameterOfTheConfigConstructor()
        {
            System.Reflection.ConstructorInfo constructor = Assert.Single(typeof(OndewoClientConfig).GetConstructors());
            string[] names = constructor.GetParameters().Select(parameter => parameter.Name!).ToArray();

            Assert.Equal(
                new[]
                {
                    "host", "port", "grpcCert", "grpcClientCert", "grpcClientKey", "useSecureChannel", "keycloakUrl", "realm",
                    "clientId", "username", "password", "tokenExpirationInS", "verifySsl", "refreshToken",
                },
                names);
        }

        [Fact]
        public void T56_ATokenOnlyConfigIsKeycloakConfigured()
        {
            OndewoClientConfig config = Config(password: null, refreshToken: "off-handed-0");

            Assert.True(config.KeycloakConfigured);
            Assert.Equal(string.Empty, config.Password);
            Assert.Equal("off-handed-0", config.RefreshToken);
        }

        [Fact]
        public void T57_KeycloakWithNeitherPasswordNorTokenIsRefused()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => Config(password: "", refreshToken: ""));

            Assert.Contains("password", error.Message, StringComparison.Ordinal);
            Assert.Contains("refresh_token", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void T58_APasswordConfigIsUnchanged()
        {
            OndewoClientConfig config = Config(tokenExpirationInS: 600, verifySsl: false);

            Assert.True(config.KeycloakConfigured);
            Assert.Equal(KeycloakTestSupport.Url, config.KeycloakUrl);
            Assert.Equal(KeycloakTestSupport.Realm, config.Realm);
            Assert.Equal(ClientId, config.ClientId);
            Assert.Equal(Username, config.Username);
            Assert.Equal(Password, config.Password);
            Assert.Equal(600, config.TokenExpirationInS);
            Assert.False(config.VerifySsl);
            Assert.Equal(string.Empty, config.RefreshToken);
        }

        [Fact]
        public void T59_RenderingRedactsTheSecretsAndShowsAnEmptyOneAsEmpty()
        {
            string both = Config(refreshToken: "off-secret-1").ToString();
            string tokenOnly = Config(password: null, refreshToken: "off-secret-1").ToString();
            string passwordOnly = Config().ToString();

            Assert.Contains("Password = ***REDACTED***", both, StringComparison.Ordinal);
            Assert.Contains("RefreshToken = ***REDACTED***", both, StringComparison.Ordinal);
            Assert.DoesNotContain("off-secret-1", both, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, both, StringComparison.Ordinal);
            Assert.Contains("Password = , ", tokenOnly, StringComparison.Ordinal);
            Assert.EndsWith("RefreshToken =  }", passwordOnly, StringComparison.Ordinal);
            Assert.Contains($"Username = {Username}", passwordOnly, StringComparison.Ordinal);
        }

        [Fact]
        public void T60_ATokenOnlyConfigReachesTheProviderFactoryWithAnEmptyPassword()
        {
            var seen = new List<OndewoClientConfig>();
            var registry = new KeycloakTokenProviderRegistry((config, logger) =>
            {
                seen.Add(config);
                return Provider(new FakeTransport(Step.Token("acc-1")), new FakeClock(), refreshToken: config.RefreshToken, password: config.Password);
            });

            registry.GetOrCreate(Config(password: null, refreshToken: "off-handed-0"));

            OndewoClientConfig reached = Assert.Single(seen);
            Assert.Equal("off-handed-0", reached.RefreshToken);
            Assert.Equal(string.Empty, reached.Password);
        }

        [Theory]
        [InlineData("u", null, null)]
        [InlineData(null, "r", null)]
        [InlineData(null, null, "c")]
        [InlineData("u", "r", null)]
        [InlineData("u", null, "c")]
        [InlineData(null, "r", "c")]
        public void T61_APartialKeycloakTripleIsRefused(string? url, string? realm, string? clientId)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => new OndewoClientConfig(
                "localhost", 50055, keycloakUrl: url, realm: realm, clientId: clientId, password: Password));

            Assert.Contains("`keycloak_url`, `realm`, and `client_id` must be provided together", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void T62_WithoutTheTripleThereIsNoKeycloak()
        {
            var config = new OndewoClientConfig("localhost", 50055, username: "someone");

            Assert.False(config.KeycloakConfigured);
            Assert.Throws<ArgumentException>(() => new KeycloakTokenProviderRegistry().GetOrCreate(config));
        }

        [Fact]
        public async Task T62_AChannelWithoutKeycloakSendsNoAuthorization()
        {
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, TlsTestPki.Instance.Server);
            using GrpcChannel channel = OndewoChannelFactory.Create(
                new OndewoClientConfig("localhost", server.Port, TlsTestPki.Instance.CaPem));

            await TlsTestServer.CallAsync(channel);

            Assert.Equal(string.Empty, server.LastAuthorization);
        }

        // ---------------------------------------------------------------- J. registry

        private static (KeycloakTokenProviderRegistry Registry, List<OndewoClientConfig> Built) CountingRegistry()
        {
            var built = new List<OndewoClientConfig>();
            var registry = new KeycloakTokenProviderRegistry((config, logger) =>
            {
                built.Add(config);
                return Provider(new FakeTransport(Step.Token("acc-1")), new FakeClock(), refreshToken: config.RefreshToken);
            });
            return (registry, built);
        }

        [Fact]
        public void T48_TheSameCredentialsShareOneProvider()
        {
            (KeycloakTokenProviderRegistry registry, List<OndewoClientConfig> built) = CountingRegistry();

            KeycloakTokenProvider first = registry.GetOrCreate(Config());
            KeycloakTokenProvider second = registry.GetOrCreate(Config(host: "other-host", port: 1234));

            Assert.Same(first, second);
            Assert.Single(built);
        }

        public static IEnumerable<object[]> DifferingCredentials()
        {
            yield return new object[] { Config(username: "another-user") };
            yield return new object[] { Config(password: "another-password") };
            yield return new object[] { Config(realm: "another-realm") };
            yield return new object[] { Config(url: "https://another.example/auth") };
            yield return new object[] { Config(tokenExpirationInS: 60) };
            yield return new object[] { Config(verifySsl: false) };
            yield return new object[] { Config(refreshToken: "off-1") };
        }

        [Theory]
        [MemberData(nameof(DifferingCredentials))]
        public void T49_DifferentCredentialsGetDifferentProviders(OndewoClientConfig other)
        {
            (KeycloakTokenProviderRegistry registry, List<OndewoClientConfig> built) = CountingRegistry();

            Assert.NotSame(registry.GetOrCreate(Config()), registry.GetOrCreate(other));
            Assert.Equal(2, built.Count);
        }

        [Fact]
        public void T50_DroppedConfigsAreNeverConfusedAndDeadEntriesArePruned()
        {
            (KeycloakTokenProviderRegistry registry, List<OndewoClientConfig> built) = CountingRegistry();

            for (int user = 0; user < 50; user++)
            {
                registry.GetOrCreate(Config(username: $"user-{user}"));
                CollectGarbage();
            }

            Assert.Equal(Enumerable.Range(0, 50).Select(user => $"user-{user}"), built.Select(config => config.Username));
            Assert.True(registry.Count < 50, $"{registry.Count} entries kept");
        }

        [Fact]
        public void T51_TwoTokenOnlyTenantsGetTwoProviders()
        {
            (KeycloakTokenProviderRegistry registry, List<OndewoClientConfig> built) = CountingRegistry();

            KeycloakTokenProvider a = registry.GetOrCreate(Config(password: null, refreshToken: "off-tenant-a"));
            KeycloakTokenProvider b = registry.GetOrCreate(Config(password: null, refreshToken: "off-tenant-b"));

            Assert.NotSame(a, b);
            Assert.Equal(new[] { "off-tenant-a", "off-tenant-b" }, built.Select(config => config.RefreshToken));
        }

        [Fact]
        public void T52_T53_TheKeyHoldsNoSecretAndIsSha256Hex()
        {
            string key = KeycloakTokenProviderRegistry.ComputeKey(Config(refreshToken: "off-secret-1", tokenExpirationInS: 600));

            Assert.Matches(new Regex("^[0-9a-f]{64}$"), key);
            Assert.DoesNotContain("off-secret-1", key, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, key, StringComparison.Ordinal);
        }

        [Fact]
        public void TheKeyMatchesTheCrossSdkDerivation()
        {
            // sha256("https://k/auth\0r\0c\0u\0p\0\0True\0") - computed with the python SDK's derivation.
            var config = new OndewoClientConfig(
                "localhost", 50055, keycloakUrl: "https://k/auth", realm: "r", clientId: "c", username: "u", password: "p");
            string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("https://k/auth\0r\0c\0u\0p\0\0True\0")));

            Assert.Equal(expected, KeycloakTokenProviderRegistry.ComputeKey(config));
            Assert.Throws<ArgumentNullException>(() => KeycloakTokenProviderRegistry.ComputeKey(null!));
            Assert.Throws<ArgumentNullException>(() => new KeycloakTokenProviderRegistry().GetOrCreate(null!));
        }

        [Fact]
        public void T54_AClosedProviderIsNeverHandedOutAgain()
        {
            (KeycloakTokenProviderRegistry registry, List<OndewoClientConfig> built) = CountingRegistry();
            KeycloakTokenProvider first = registry.GetOrCreate(Config());
            first.Close();

            KeycloakTokenProvider second = registry.GetOrCreate(Config());

            Assert.NotSame(first, second);
            Assert.Equal(2, built.Count);
        }

        [Fact]
        public void ConcurrentFirstUseLogsInOnce()
        {
            (KeycloakTokenProviderRegistry registry, List<OndewoClientConfig> built) = CountingRegistry();

            KeycloakTokenProvider[] providers = Enumerable.Range(0, 16).AsParallel()
                .Select(_ => registry.GetOrCreate(Config(username: "concurrent"))).ToArray();

            Assert.Single(built);
            Assert.All(providers, provider => Assert.Same(providers[0], provider));
        }

        // ---------------------------------------------------------------- the real transport

        [Fact]
        public async Task TheDefaultRegistryLogsInOverHttpWithATokenOnlyConfig()
        {
            await using FakeKeycloakServer keycloak = await FakeKeycloakServer.StartAsync(_ => (200, TokenBody("acc-http")));
            OndewoClientConfig config = Config(url: keycloak.BaseUrl(), password: null, refreshToken: "off-handed-0");

            using KeycloakTokenProvider provider = new KeycloakTokenProviderRegistry().GetOrCreate(config);

            Assert.Equal("acc-http", provider.AccessToken);
            (string path, Dictionary<string, string> form) = Assert.Single(keycloak.Requests);
            Assert.Equal("/auth/realms/ondewo-ccai-platform/protocol/openid-connect/token", path);
            Assert.Equal(
                new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = "off-handed-0" },
                form);
        }

        [Fact]
        public async Task AnErrorStatusOverHttpIsTyped()
        {
            await using FakeKeycloakServer keycloak = await FakeKeycloakServer.StartAsync(
                _ => (401, "{\"error\":\"invalid_grant\",\"error_description\":\"Invalid user credentials\"}"));

            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => new KeycloakTokenProvider(keycloak.BaseUrl(), Realm, ClientId, Username, Password, startBackgroundRefresh: false));

            Assert.Contains("Keycloak token login failed with status 401: {\"error\":\"invalid_grant\"", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ASelfSignedKeycloakIsRefusedUnlessVerificationIsOff()
        {
            await using FakeKeycloakServer keycloak = await FakeKeycloakServer.StartAsync(
                _ => (200, TokenBody("acc-tls")), TlsTestPki.Instance.Server);
            string url = keycloak.BaseUrl("https");

            KeycloakAuthenticationException refused = Assert.Throws<KeycloakAuthenticationException>(
                () => new KeycloakTokenProvider(url, Realm, ClientId, Username, Password, startBackgroundRefresh: false));
            using var accepted = new KeycloakTokenProvider(
                url, Realm, ClientId, Username, Password, verifySsl: false, startBackgroundRefresh: false);

            Assert.Contains("could not reach the token endpoint (HttpRequestException", refused.Message, StringComparison.Ordinal);
            Assert.Equal("acc-tls", accepted.AccessToken);
            Assert.False(accepted.VerifySsl);
        }

        [Fact]
        public async Task ASlowKeycloakTimesOutAsANetworkFailure()
        {
            await using FakeKeycloakServer keycloak = await FakeKeycloakServer.StartAsync(_ => (200, TokenBody("late")));
            keycloak.Delay = TimeSpan.FromSeconds(5);

            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => new KeycloakTokenProvider(
                    keycloak.BaseUrl(), Realm, ClientId, Username, Password, null, true, null, null, null, false, null, null,
                    string.Empty, new KeycloakTimings(httpTimeoutS: 0.3)));

            Assert.Contains("could not reach the token endpoint (TimeoutException: the token endpoint did not answer within 0.3s)",
                error.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(error.InnerException);
        }

        [Fact]
        public void AnUnreachableKeycloakIsANetworkFailure()
        {
            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => new KeycloakTokenProvider("http://127.0.0.1:1/auth", Realm, ClientId, Username, Password, startBackgroundRefresh: false));

            Assert.Contains("Keycloak token login could not reach the token endpoint", error.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- the channel factory attaches the token

        [Fact]
        public async Task ATlsChannelCarriesTheKeycloakBearerToken()
        {
            await using FakeKeycloakServer keycloak = await FakeKeycloakServer.StartAsync(_ => (200, TokenBody("acc-channel")));
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, TlsTestPki.Instance.Server);
            var logger = new CapturingLogger();
            OndewoClientConfig config = Config(
                url: keycloak.BaseUrl(), username: $"channel-{Guid.NewGuid()}", port: server.Port, grpcCert: TlsTestPki.Instance.CaPem);

            using GrpcChannel channel = OndewoChannelFactory.Create(
                config, options => options.LoggerFactory = new SingleLoggerFactory(logger));
            using GrpcChannel second = OndewoChannelFactory.Create(config);
            await TlsTestServer.CallAsync(channel);
            await TlsTestServer.CallAsync(second);

            Assert.Equal("Bearer acc-channel", server.LastAuthorization);
            Assert.Single(keycloak.Requests);
        }

        [Fact]
        public async Task APlaintextChannelCarriesTheKeycloakBearerToken()
        {
            await using FakeKeycloakServer keycloak = await FakeKeycloakServer.StartAsync(_ => (200, TokenBody("acc-plain")));
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, null);
            OndewoClientConfig config = Config(
                url: keycloak.BaseUrl(), username: $"plain-{Guid.NewGuid()}", port: server.Port, useSecureChannel: false);

            using GrpcChannel channel = OndewoChannelFactory.Create(config);
            await TlsTestServer.CallAsync(channel);

            Assert.Equal("Bearer acc-plain", server.LastAuthorization);
        }

        [Fact]
        public async Task CredentialsSetByTheCallerWinAndNoLoginHappens()
        {
            await using FakeKeycloakServer keycloak = await FakeKeycloakServer.StartAsync(_ => (200, TokenBody("unused")));
            await using TlsTestServer server = await TlsTestServer.StartAsync(IPAddress.Loopback, TlsTestPki.Instance.Server);
            OndewoClientConfig config = Config(
                url: keycloak.BaseUrl(), username: $"own-{Guid.NewGuid()}", port: server.Port, grpcCert: TlsTestPki.Instance.CaPem);

            using GrpcChannel channel = OndewoChannelFactory.Create(config, options => options.Credentials =
                ChannelCredentials.Create(ChannelCredentials.SecureSsl, OndewoAuth.CreateBearerCredentials("own-token")));
            await TlsTestServer.CallAsync(channel);

            Assert.Equal("Bearer own-token", server.LastAuthorization);
            Assert.Empty(keycloak.Requests);
        }

        [Fact]
        public void AFailedLoginFailsTheChannelCreation()
        {
            OndewoClientConfig config = Config(url: "http://127.0.0.1:1/auth", username: $"down-{Guid.NewGuid()}");

            Assert.Throws<KeycloakAuthenticationException>(() => OndewoChannelFactory.Create(config));
        }

        [Fact]
        public void CreateKeycloakCredentialsRefusesNullArguments()
        {
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1")), new FakeClock());

            Assert.Throws<ArgumentNullException>(() => OndewoChannelFactory.CreateKeycloakCredentials(null!, provider));
            Assert.Throws<ArgumentNullException>(() => OndewoChannelFactory.CreateKeycloakCredentials(Config(), null!));
            Assert.NotNull(OndewoChannelFactory.CreateKeycloakCredentials(Config(useSecureChannel: false), provider));
        }

        private sealed class SingleLoggerFactory : ILoggerFactory
        {
            private readonly ILogger logger;

            internal SingleLoggerFactory(ILogger logger)
            {
                this.logger = logger;
            }

            public void AddProvider(ILoggerProvider provider)
            {
            }

            public ILogger CreateLogger(string categoryName) => logger;

            public void Dispose()
            {
            }
        }
    }
}
