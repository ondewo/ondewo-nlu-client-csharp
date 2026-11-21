#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Ondewo.Nlu.Client.Auth;
using Xunit;
using static Ondewo.Nlu.Client.Tests.KeycloakTestSupport;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// The cross-SDK Keycloak test catalogue (T01-T47) for <see cref="KeycloakTokenProvider"/>: a fake token
    /// endpoint, a fake monotonic clock, a fixed jitter and a scripted stop signal - no network, no real
    /// sleeps, except in the real-worker cases (T39-T41).
    /// </summary>
    public class KeycloakTokenProviderTests
    {
        private const string Bearer1 = "Bearer acc-1";

        private static KeycloakTimings Shrunk(double joinTimeout = 5.0, double httpTimeout = 10.0) => new(
            expiryLeewayS: 0.05, minRefreshDelayS: 0.01, retryBaseDelayS: 0.05, retryMaxDelayS: 0.05,
            httpTimeoutS: httpTimeout, threadJoinTimeoutS: joinTimeout);

        // ---------------------------------------------------------------- A. backoff ladder

        [Fact]
        public void T01_TheCeilingDoublesUpToTheMaximum()
        {
            double[] ladder = Enumerable.Range(1, 10).Select(n => KeycloakTokenProvider.BackoffDelaySeconds(n, () => 1.0)).ToArray();

            Assert.Equal(new double[] { 5, 10, 20, 40, 80, 160, 300, 300, 300, 300 }, ladder);
        }

        [Fact]
        public void T02_AZeroFractionIsAlwaysTheBase()
        {
            Assert.All(Enumerable.Range(1, 19), n => Assert.Equal(5.0, KeycloakTokenProvider.BackoffDelaySeconds(n, () => 0.0)));
        }

        [Theory]
        [InlineData(2, 0.5, 7.5)]
        [InlineData(3, 0.25, 8.75)]
        public void T03_TheDelayIsInsideTheWindow(int failures, double fraction, double expected)
        {
            Assert.Equal(expected, KeycloakTokenProvider.BackoffDelaySeconds(failures, () => fraction), 6);
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(-0.001)]
        [InlineData(1.5)]
        [InlineData(1e9)]
        public void T04_AFaultyJitterIsClamped(double fraction)
        {
            double delay = KeycloakTokenProvider.BackoffDelaySeconds(4, () => fraction);

            Assert.InRange(delay, 5.0, 40.0);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void T05_NoFailuresCountsAsOne(int failures)
        {
            Assert.Equal(5.0, KeycloakTokenProvider.BackoffDelaySeconds(failures, () => 1.0));
        }

        [Theory]
        [InlineData(1_000_000_000L)]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void T06_AHugeCountNeverOverflows(long failures)
        {
            double delay = KeycloakTokenProvider.BackoffDelaySeconds(failures, () => 1.0);

            Assert.Equal(failures > 0 ? 300.0 : 5.0, delay);
        }

        [Fact]
        public void T07_TheConstantsHoldTheirRelationsAndTheSharedValues()
        {
            Assert.True(KeycloakTokenProvider.RetryBaseDelaySeconds > KeycloakTokenProvider.MinRefreshDelaySeconds);
            Assert.True(KeycloakTokenProvider.RetryBaseDelaySeconds * Math.Pow(2, KeycloakTokenProvider.MaxRetryExponent)
                        >= KeycloakTokenProvider.RetryMaxDelaySeconds);
            Assert.True(KeycloakTokenProvider.HttpTimeoutSeconds < KeycloakTokenProvider.ExpiryLeewaySeconds);
            Assert.Equal(30.0, KeycloakTokenProvider.ExpiryLeewaySeconds);
            Assert.Equal(1.0, KeycloakTokenProvider.MinRefreshDelaySeconds);
            Assert.Equal(5.0, KeycloakTokenProvider.RetryBaseDelaySeconds);
            Assert.Equal(300.0, KeycloakTokenProvider.RetryMaxDelaySeconds);
            Assert.Equal(6, KeycloakTokenProvider.MaxRetryExponent);
            Assert.Equal(10.0, KeycloakTokenProvider.HttpTimeoutSeconds);
            Assert.Equal(500, KeycloakTokenProvider.MaxErrorBodyChars);
            Assert.Equal(5.0, KeycloakTokenProvider.ThreadJoinTimeoutSeconds);
            KeycloakTimings timings = KeycloakTimings.Default;
            Assert.Equal(
                new object[] { 30.0, 1.0, 5.0, 300.0, 6, 10.0, 500, 5.0 },
                new object[]
                {
                    timings.ExpiryLeewayS, timings.MinRefreshDelayS, timings.RetryBaseDelayS, timings.RetryMaxDelayS,
                    timings.MaxRetryExponent, timings.HttpTimeoutS, timings.MaxErrorBodyChars, timings.ThreadJoinTimeoutS,
                });
        }

        [Fact]
        public void BackoffRefusesANullJitterSource()
        {
            Assert.Throws<ArgumentNullException>(() => KeycloakTokenProvider.BackoffDelaySeconds(1, null!));
        }

        // ---------------------------------------------------------------- B. the loop survives every failure

        [Fact]
        public async Task T08_ARefusedRefreshIsRetriedAndRecovers()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Refused(), Step.Token("acc-3"));
            using KeycloakTokenProvider provider = Provider(transport, clock);

            await RunLoop(provider, new FakeStopSignal(clock, 2), clock);

            Assert.Equal(new[] { "password", "refresh_token", "refresh_token" }, transport.Grants);
            Assert.Equal("acc-3", provider.AccessToken);
            Assert.Equal(0, provider.ConsecutiveRefreshFailures);
        }

        public static IEnumerable<object[]> FailureKinds()
        {
            yield return new object[] { "network", Step.Throw(new HttpRequestException("network down")) };
            yield return new object[] { "refused", Step.Throw(new SocketException((int)SocketError.ConnectionRefused)) };
            yield return new object[] { "timeout", Step.Throw(new TaskCanceledException("timed out")) };
            yield return new object[] { "io", Step.Throw(new IOException("reset by peer")) };
            yield return new object[] { "timeout-exception", Step.Throw(new TimeoutException("too slow")) };
            yield return new object[] { "bug", Step.Throw(new InvalidOperationException("transport bug")) };
            yield return new object[] { "503", Step.Status(503, "{\"error\":\"unavailable\"}") };
            yield return new object[] { "502-empty", Step.Status(502, string.Empty) };
            yield return new object[] { "non-json", Step.Ok("<html>bad gateway</html>") };
            yield return new object[] { "array", Step.Ok("[1, 2]") };
            yield return new object[] { "no-access-token", Step.Ok("{\"expires_in\": 300}") };
            yield return new object[] { "expires-soon", Step.Ok("{\"access_token\": \"acc-x\", \"expires_in\": \"soon\"}") };
        }

        [Theory]
        [MemberData(nameof(FailureKinds))]
        public async Task T09_NoFailureKindEndsTheLoop(string kind, Step failure)
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), failure, Step.Token("acc-ok"));
            using KeycloakTokenProvider provider = Provider(transport, clock);

            await RunLoop(provider, new FakeStopSignal(clock, 2), clock);

            Assert.True(transport.Forms.Count == 3, kind);
            Assert.Equal("acc-ok", provider.AccessToken);
        }

        [Fact]
        public async Task T10_APersistentFailureKeepsTheLoopRunning()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"));
            using KeycloakTokenProvider provider = Provider(transport, clock);

            await RunLoop(provider, new FakeStopSignal(clock, 6), clock);

            Assert.Equal(new[] { "password" }.Concat(Enumerable.Repeat("refresh_token", 6)), transport.Grants);
            Assert.Equal("acc-1", provider.AccessToken);
            Assert.Equal(6, provider.ConsecutiveRefreshFailures);
        }

        [Fact]
        public async Task T11_ARefusedClientIsNeverAnsweredWithAPasswordLogin()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(401, "{\"error\":\"invalid_client\"}"));
            using KeycloakTokenProvider provider = Provider(transport, clock);

            await RunLoop(provider, new FakeStopSignal(clock, 10), clock);

            Assert.Single(transport.Grants, grant => grant == "password");
            Assert.Equal(11, transport.Forms.Count);
        }

        // ---------------------------------------------------------------- C. the loop backs off

        [Fact]
        public async Task T12_TheFirstRetryWaitsTheBaseNotTheFloor()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"));
            using KeycloakTokenProvider provider = Provider(transport, clock);
            var stop = new FakeStopSignal(clock, 2);

            await RunLoop(provider, stop, clock);

            Assert.Equal(new[] { 270.0, 5.0 }, stop.Waits);
        }

        [Fact]
        public async Task T13_APersistentOutageClimbsTheLadder()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"));
            var logger = new CapturingLogger();
            using KeycloakTokenProvider provider = Provider(transport, clock, logger: logger);
            var stop = new FakeStopSignal(clock, 9);

            await RunLoop(provider, stop, clock);

            Assert.Equal(new double[] { 5, 10, 20, 40, 80, 160, 300, 300 }, stop.Waits.Skip(1).Select(wait => Math.Round(wait, 6)));
            // Once the token has expired the loop's refresh raises; that is logged at DEBUG, never warned twice.
            Assert.Equal(9, logger.At(LogLevel.Warning).Count());
            Assert.Contains(logger.At(LogLevel.Debug), line => line.Contains("will be retried", StringComparison.Ordinal));
        }

        [Fact]
        public async Task T14_ASuccessResetsTheBackoff()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(
                Step.Token("acc-1"), Step.Status(503, "{}"), Step.Status(503, "{}"), Step.Token("acc-4", 120), Step.Status(503, "{}"));
            var logger = new CapturingLogger();
            using KeycloakTokenProvider provider = Provider(transport, clock, logger: logger);
            var stop = new FakeStopSignal(clock, 5);

            await RunLoop(provider, stop, clock);

            Assert.Equal(new double[] { 270, 5, 10, 90, 5 }, stop.Waits);
            Assert.Single(logger.At(LogLevel.Information), line => line.Contains("recovered after 2 failed attempt(s)", StringComparison.Ordinal));
        }

        [Fact]
        public async Task T15_TheBackoffIsMeasuredFromTheRecordedFailure()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"))
            {
                Clock = clock,
                AdvancePerRefresh = KeycloakTokenProvider.HttpTimeoutSeconds,
            };
            using KeycloakTokenProvider provider = Provider(transport, clock);
            var stop = new FakeStopSignal(clock, 2);

            await RunLoop(provider, stop, clock);

            Assert.Equal(5.0, stop.Waits[1]);
        }

        [Fact]
        public async Task T16_TheDeadlineEndsTheLoopDuringABackoff()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1", 30), Step.Status(503, "{}"));
            using KeycloakTokenProvider provider = Provider(transport, clock, tokenExpirationInS: 100);
            var stop = new FakeStopSignal(clock, 10_000);

            await RunLoop(provider, stop, clock);

            Assert.True(stop.IsSet);
            Assert.True(clock.Now <= 1101.0, $"the loop ran until {clock.Now}");
            Assert.All(stop.Waits, wait => Assert.True(wait <= 100.0));
        }

        [Fact]
        public async Task T17_AStopDuringABackoffWaitSendsNothingMore()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"));
            using KeycloakTokenProvider provider = Provider(transport, clock);
            var stop = new FakeStopSignal(clock, 1);

            await RunLoop(provider, stop, clock);

            Assert.Equal(new[] { "password", "refresh_token" }, transport.Grants);
        }

        [Fact]
        public async Task T18_ACollectedProviderEndsTheLoopWithoutARequest()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"));
            WeakReference<KeycloakTokenProvider> gone = Abandoned(transport, clock);
            CollectGarbage();

            await KeycloakTokenProvider.RunRefreshLoopAsync(gone, new FakeStopSignal(clock, 5), clock.Time);

            Assert.Single(transport.Forms);
        }

        [Fact]
        public async Task T18_AProviderCollectedDuringTheWaitEndsTheLoop()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"));
            var stop = new FakeStopSignal(clock, 5) { DuringWait = CollectGarbage };
            WeakReference<KeycloakTokenProvider> gone = Abandoned(transport, clock);

            await KeycloakTokenProvider.RunRefreshLoopAsync(gone, stop, clock.Time);

            Assert.Single(stop.Waits);
            Assert.Single(transport.Forms);
        }

        [Fact]
        public async Task T18_AClosedProviderEndsTheLoopWithoutARequest()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"));
            var stop = new FakeStopSignal(clock, 5);
            using KeycloakTokenProvider provider = Provider(transport, clock, stopSignal: stop);
            provider.Close();

            await RunLoop(provider, stop, clock);

            Assert.True(provider.IsClosed);
            Assert.Empty(stop.Waits);
            Assert.Single(transport.Forms);
        }

        // ---------------------------------------------------------------- D. the lazy path shares the backoff

        [Fact]
        public void T19_ATokenInsideTheLeewayIsServedWhenTheRefreshFails()
        {
            var clock = new FakeClock();
            var logger = new CapturingLogger();
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}")), clock, logger: logger);
            clock.Now = 1271;

            KeyValuePair<string, string> metadata = provider.GetAuthorizationMetadata();

            Assert.Equal("authorization", metadata.Key);
            Assert.Equal(Bearer1, metadata.Value);
            string warning = Assert.Single(logger.At(LogLevel.Warning));
            Assert.Contains("refresh failed (attempt 1)", warning, StringComparison.Ordinal);
            Assert.Contains("retrying in 5.0s, the current access token stays valid for 29.0s", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void T20_NoRequestIsSentDuringTheBackoff()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"));
            using KeycloakTokenProvider provider = Provider(transport, clock);
            clock.Now = 1275;

            for (int call = 0; call < 100; call++)
            {
                Assert.Equal(Bearer1, provider.GetAuthorizationMetadata().Value);
                clock.Advance(0.04);
            }

            Assert.Equal(new[] { "password", "refresh_token" }, transport.Grants);
        }

        [Fact]
        public void T21_AnExpiredTokenDuringTheBackoffRaises()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"));
            using KeycloakTokenProvider provider = Provider(transport, clock);
            clock.Now = 1299;
            Assert.Equal(Bearer1, provider.GetAuthorizationMetadata().Value);
            clock.Advance(2);

            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(() => provider.GetAuthorizationMetadata());

            Assert.Contains("backing off after 1 consecutive failure", error.Message, StringComparison.Ordinal);
            Assert.Contains("the next attempt is due in 3.0s", error.Message, StringComparison.Ordinal);
            Assert.Equal(2, transport.Forms.Count);
        }

        [Fact]
        public void T22_AnExpiredTokenWhoseRefreshIsRefusedRaises()
        {
            var clock = new FakeClock();
            var logger = new CapturingLogger();
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1"), Step.Refused()), clock, logger: logger);
            clock.Now = 1400;

            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(() => provider.GetAuthorizationMetadata());

            Assert.Contains("status 400", error.Message, StringComparison.Ordinal);
            Assert.Contains("the current access token has expired", Assert.Single(logger.At(LogLevel.Warning)), StringComparison.Ordinal);
        }

        [Fact]
        public void T23_ANonAuthTransportErrorIsWrappedWithItsCause()
        {
            var clock = new FakeClock();
            var bug = new InvalidOperationException("boom");
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1"), Step.Throw(bug)), clock);
            clock.Now = 1400;

            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(() => provider.GetAuthorizationMetadata());

            Assert.Same(bug, error.InnerException);
            Assert.Equal("Keycloak token refresh failed (InvalidOperationException: boom)", error.Message);
        }

        [Fact]
        public void T24_TheLazyPathRecovers()
        {
            var clock = new FakeClock();
            var logger = new CapturingLogger();
            using KeycloakTokenProvider provider = Provider(
                new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"), Step.Token("acc-3")), clock, logger: logger);
            clock.Now = 1280;
            provider.GetAuthorizationMetadata();
            clock.Advance(5);

            Assert.Equal("Bearer acc-3", provider.GetAuthorizationMetadata().Value);
            Assert.Equal(0, provider.ConsecutiveRefreshFailures);
            Assert.Single(logger.At(LogLevel.Information), line => line.Contains("recovered after 1 failed attempt", StringComparison.Ordinal));
        }

        [Fact]
        public async Task T25_ALazyFailureSchedulesTheLoop()
        {
            var clock = new FakeClock();
            using KeycloakTokenProvider provider = Provider(
                new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"), Step.Token("acc-3")), clock);
            clock.Now = 1280;
            provider.GetAuthorizationMetadata();
            clock.Advance(2);
            var stop = new FakeStopSignal(clock, 1);

            await RunLoop(provider, stop, clock);

            Assert.Equal(3.0, stop.Waits[0], 6);
            Assert.Equal("acc-3", provider.AccessToken);
        }

        [Fact]
        public async Task T26_ConcurrentCallersShareOneRequestAndNoneFails()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1", 20), Step.Throw(new HttpRequestException("down")))
            {
                Delay = TimeSpan.FromMilliseconds(50),
            };
            using KeycloakTokenProvider provider = Provider(transport, clock);

            string[][] values = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                var seen = new List<string>();
                for (int call = 0; call < 20; call++)
                {
                    seen.Add((await provider.GetAuthorizationMetadataAsync()).Value);
                }

                return seen.ToArray();
            })));

            Assert.Equal(320, values.SelectMany(value => value).Count());
            Assert.All(values.SelectMany(value => value), value => Assert.Equal(Bearer1, value));
            Assert.Equal(new[] { "password", "refresh_token" }, transport.Grants);
        }

        // ---------------------------------------------------------------- E. response validation

        public static IEnumerable<object[]> NetworkFailures()
        {
            yield return new object[] { new HttpRequestException("network down") };
            yield return new object[] { new SocketException((int)SocketError.ConnectionRefused) };
            yield return new object[] { new TaskCanceledException("timed out") };
        }

        [Theory]
        [MemberData(nameof(NetworkFailures))]
        public void T27_ANetworkFailureAtConstructionIsTyped(Exception failure)
        {
            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(new FakeTransport(Step.Throw(failure)), new FakeClock()));

            Assert.Contains("Keycloak token login could not reach the token endpoint", error.Message, StringComparison.Ordinal);
            Assert.Same(failure, error.InnerException);
        }

        [Fact]
        public void T27_AnyOtherErrorAtConstructionIsWrapped()
        {
            var bug = new InvalidOperationException("bug");

            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(new FakeTransport(Step.Throw(bug)), new FakeClock(), refreshToken: "off-handed-0"));

            Assert.Equal("Keycloak token refresh failed (InvalidOperationException: bug)", error.Message);
            Assert.Same(bug, error.InnerException);
        }

        [Fact]
        public void T28_ANonJsonBodyAtConstructionIsTyped()
        {
            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(new FakeTransport(Step.Ok("<html>bad gateway</html>")), new FakeClock()));

            Assert.Equal("Keycloak token login returned status 200 with a body that is not JSON", error.Message);
            Assert.NotNull(error.InnerException);
        }

        [Theory]
        [InlineData("[1, 2]", "array")]
        [InlineData("\"text\"", "string")]
        [InlineData("42", "number")]
        [InlineData("null", "null")]
        [InlineData("true", "boolean")]
        public void T29_AJsonNonObjectAtConstructionIsTyped(string body, string type)
        {
            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(new FakeTransport(Step.Ok(body)), new FakeClock()));

            Assert.Equal($"Keycloak token login returned a JSON {type} instead of an object", error.Message);
        }

        [Fact]
        public void T30_ALongErrorBodyIsTruncated()
        {
            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(new FakeTransport(Step.Status(502, "<html>" + new string('x', 10_000))), new FakeClock()));

            Assert.StartsWith("Keycloak token login failed with status 502: <html>xx", error.Message, StringComparison.Ordinal);
            Assert.True(error.Message.Length < 700, $"{error.Message.Length} characters");
        }

        [Theory]
        [InlineData("{\"refresh_token\":\"secret-offline-token-77\",\"expires_in\":300}", "['expires_in', 'refresh_token']")]
        [InlineData("{\"access_token\":\"\",\"refresh_token\":\"secret-offline-token-77\"}", "['access_token', 'refresh_token']")]
        [InlineData("{\"access_token\":17,\"refresh_token\":\"secret-offline-token-77\"}", "['access_token', 'refresh_token']")]
        [InlineData("{}", "[]")]
        public void T31_AMissingAccessTokenNamesTheFieldsAndWithholdsTheValues(string body, string fields)
        {
            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(new FakeTransport(Step.Ok(body)), new FakeClock()));

            Assert.Equal(
                $"Keycloak token response did not contain an access_token; the response carried the fields {fields} "
                + "(values withheld: a token response is credential material)",
                error.Message);
            Assert.DoesNotContain("secret-offline-token-77", error.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("\"soon\"")]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("\"NaN\"")]
        [InlineData("\"Infinity\"")]
        public void T32_ANonNumericExpiresInChangesNoState(string expiresIn)
        {
            var clock = new FakeClock();
            using KeycloakTokenProvider provider = Provider(
                new FakeTransport(Step.Token("acc-1"), Step.Ok($"{{\"access_token\": \"acc-2\", \"expires_in\": {expiresIn}, \"refresh_token\": \"off-9\"}}")),
                clock);
            clock.Now = 1280;

            Assert.Equal(Bearer1, provider.GetAuthorizationMetadata().Value);
            Assert.Equal("acc-1", provider.AccessToken);
            Assert.Equal(1300.0, provider.AccessTokenExpiresAt);
            Assert.Equal("off-1", provider.RefreshToken);
        }

        [Fact]
        public void T32_ANonNumericExpiresInAtConstructionIsTyped()
        {
            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(new FakeTransport(Step.Ok("{\"access_token\":\"acc-1\",\"expires_in\":\"soon\"}")), new FakeClock()));

            Assert.Equal("Keycloak token response carried a non-numeric expires_in (value withheld)", error.Message);
        }

        [Theory]
        [InlineData("\"300\"", 1300.0)]
        [InlineData("300", 1300.0)]
        [InlineData("\"1.5e2\"", 1150.0)]
        public void T33_ANumericExpiresInIsAccepted(string expiresIn, double expiresAt)
        {
            using KeycloakTokenProvider provider = Provider(
                new FakeTransport(Step.Ok($"{{\"access_token\": \"acc-1\", \"expires_in\": {expiresIn}}}")), new FakeClock());

            Assert.Equal(expiresAt, provider.AccessTokenExpiresAt);
        }

        [Fact]
        public void T33_AMissingExpiresInIsAlreadyDue()
        {
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Ok("{\"access_token\": \"acc-1\"}")), new FakeClock());

            Assert.Equal(1000.0, provider.AccessTokenExpiresAt);
        }

        [Fact]
        public void T34_ANegativeExpiresInIsAlreadyExpired()
        {
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1", -50)), new FakeClock());

            Assert.Equal(1000.0, provider.AccessTokenExpiresAt);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("17")]
        public void T35_ARefreshWithoutANewRefreshTokenKeepsThePrevious(string? rotated)
        {
            var clock = new FakeClock();
            string acc2 = rotated switch
            {
                null => "{\"access_token\": \"acc-2\", \"expires_in\": 300}",
                "17" => "{\"access_token\": \"acc-2\", \"expires_in\": 300, \"refresh_token\": 17}",
                _ => "{\"access_token\": \"acc-2\", \"expires_in\": 300, \"refresh_token\": \"\"}",
            };
            var transport = new FakeTransport(Step.Token("acc-1", 300, "off-1"), Step.Ok(acc2), Step.Token("acc-3"));
            using KeycloakTokenProvider provider = Provider(transport, clock);
            clock.Now = 1280;
            provider.GetAuthorizationMetadata();
            clock.Now = 1560;
            provider.GetAuthorizationMetadata();

            Assert.Equal(new[] { "off-1", "off-1" }, transport.Forms.Skip(1).Select(form => form["refresh_token"]));
            Assert.Equal("acc-3", provider.AccessToken);
        }

        [Fact]
        public void T36_TheGrantFormsAndTheEndpointAreExact()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1", 300, "off-1"), Step.Token("acc-2"));
            using var provider = new KeycloakTokenProvider(
                "https://keycloak.example/auth//", Realm, ClientId, Username, Password, transport: transport,
                timeFn: clock.Time, startBackgroundRefresh: false);
            clock.Now = 1280;
            provider.GetAuthorizationMetadata();

            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "password", ["client_id"] = ClientId, ["username"] = Username,
                    ["password"] = Password, ["scope"] = "offline_access",
                },
                transport.Forms[0]);
            Assert.Equal(
                new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = "off-1" },
                transport.Forms[1]);
            Assert.All(transport.Urls, url => Assert.Equal(
                "https://keycloak.example/auth/realms/ondewo-ccai-platform/protocol/openid-connect/token", url));
            Assert.All(transport.Timeouts, timeout => Assert.Equal(TimeSpan.FromSeconds(10), timeout));
            Assert.Equal("https://keycloak.example/auth", provider.KeycloakUrl);
            Assert.Equal(Realm, provider.Realm);
            Assert.Equal(ClientId, provider.ClientId);
            Assert.Equal(Username, provider.Username);
            Assert.Null(provider.TokenExpirationInS);
            Assert.True(provider.VerifySsl);
        }

        // ---------------------------------------------------------------- F. no credential in logs or errors

        [Fact]
        public async Task T37_NoCredentialReachesALogOrAnError()
        {
            string[] secrets = { Password, "acc-secret-1", "off-secret-1", "acc-secret-2" };
            var clock = new FakeClock();
            var logger = new CapturingLogger();
            var transport = new FakeTransport(
                Step.Token("acc-secret-1", 300, "off-secret-1"), Step.Refused(), Step.Token("acc-secret-2"), Step.Status(503, "{}"));
            using KeycloakTokenProvider provider = Provider(transport, clock, logger: logger);

            await RunLoop(provider, new FakeStopSignal(clock, 2), clock);
            clock.Now = 100_000;
            KeycloakAuthenticationException expired = Assert.Throws<KeycloakAuthenticationException>(() => provider.GetAuthorizationMetadata());
            KeycloakAuthenticationException backingOff = Assert.Throws<KeycloakAuthenticationException>(() => provider.GetAuthorizationMetadata());

            Assert.Contains("refresh failed (attempt 1)", logger.All, StringComparison.Ordinal);
            Assert.Contains("recovered", logger.All, StringComparison.Ordinal);
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, logger.All, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, expired.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, backingOff.Message, StringComparison.Ordinal);
            }
        }

        // ---------------------------------------------------------------- G. stop / close

        [Fact]
        public void T38_StopTwiceWithoutAWorkerIsHarmless()
        {
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1")), new FakeClock());

            provider.Stop();
            provider.Stop();
            provider.Dispose();

            Assert.True(provider.IsClosed);
            Assert.Null(provider.Worker);
            Assert.Equal(Bearer1, provider.GetAuthorizationMetadata().Value);
        }

        [Fact]
        public void T39_ABrokenStopSignalNeverEscapesAStopOrTheFinalizer()
        {
            var stop = new FakeStopSignal(null, 0) { ThrowOnSet = true };
            using (KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1")), new FakeClock(), stopSignal: stop))
            {
                provider.Stop();
            }

            Abandoned(new FakeTransport(Step.Token("acc-1")), new FakeClock(), stop);
            CollectGarbage();
        }

        [Fact]
        public void T39_TheFinalizerStopsTheLoopOfACollectedProvider()
        {
            var stop = new FakeStopSignal(null, 0);
            Abandoned(new FakeTransport(Step.Token("acc-1")), new FakeClock(), stop);

            CollectGarbage();

            Assert.True(stop.IsSet);
        }

        [Fact]
        public async Task T39_AStopFromInsideTheWorkerNeverWaitsForItself()
        {
            var transport = new FakeTransport(Step.Token("acc-1", 0.1), Step.Token("acc-2", 3600));
            var built = new TaskCompletionSource<KeycloakTokenProvider>();
            var stopped = new TaskCompletionSource<TimeSpan>();
            transport.OnRefresh = () =>
            {
                KeycloakTokenProvider self = built.Task.GetAwaiter().GetResult();
                var watch = Stopwatch.StartNew();
                self.Stop();
                stopped.TrySetResult(watch.Elapsed);
            };
            var provider = new KeycloakTokenProvider(
                Url, Realm, ClientId, Username, Password, null, true, transport, null, null, true, null, null, string.Empty,
                Shrunk());
            built.SetResult(provider);

            TimeSpan elapsed = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await provider.Worker!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Stop() from the worker took {elapsed}");
            Assert.True(provider.IsClosed);
        }

        [Fact]
        public async Task T40_AWorkerThatOutlivesTheJoinTimeoutDoesNotBlockTheStop()
        {
            var transport = new FakeTransport(Step.Token("acc-1", 0.1), Step.Token("acc-2", 3600));
            var entered = new TaskCompletionSource();
            transport.OnRefresh = () =>
            {
                entered.TrySetResult();
                System.Threading.Thread.Sleep(1500);
            };
            using var provider = new KeycloakTokenProvider(
                Url, Realm, ClientId, Username, Password, null, true, transport, null, null, true, null, null, string.Empty,
                Shrunk(joinTimeout: 0.05));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var watch = Stopwatch.StartNew();

            provider.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Stop() took {watch.Elapsed}");
            await provider.Worker!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task T40_AFaultedWorkerDoesNotEscapeTheStop()
        {
            var stop = new FakeStopSignal(null, 10) { ThrowOnWait = true };
            var provider = new KeycloakTokenProvider(
                Url, Realm, ClientId, Username, Password, null, true, new FakeTransport(Step.Token("acc-1")), null, stop, true,
                null, null, string.Empty, KeycloakTimings.Default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Worker!.WaitAsync(TimeSpan.FromSeconds(10)));

            provider.Dispose();

            Assert.True(provider.IsClosed);
        }

        // ---------------------------------------------------------------- H. the real worker end to end

        [Fact]
        public async Task T41_TheRealWorkerSurvivesFailuresAndRecovers()
        {
            var transport = new FakeTransport(
                Step.Token("acc-1", 0.1),
                Step.Throw(new HttpRequestException("network down")),
                Step.Status(503, "{}"),
                Step.Throw(new InvalidOperationException("generic")),
                Step.Token("acc-ok", 3600));
            using var provider = new KeycloakTokenProvider(
                Url, Realm, ClientId, Username, Password, null, true, transport, null, null, true, null, null, string.Empty,
                Shrunk());

            var watch = Stopwatch.StartNew();
            while (provider.AccessToken != "acc-ok" && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(10);
            }

            Assert.Equal("acc-ok", provider.AccessToken);
            Assert.False(provider.Worker!.IsCompleted);
            Assert.Equal(new[] { "password" }.Concat(Enumerable.Repeat("refresh_token", 4)), transport.Grants);
            provider.Stop();
            Assert.True(provider.Worker.IsCompleted);
        }

        // ---------------------------------------------------------------- I. offline-token start-up

        [Fact]
        public void T42_TheStartUpIsOneRefreshGrantWithTheHandedOffToken()
        {
            var transport = new FakeTransport(Step.Token("acc-1"));
            using KeycloakTokenProvider provider = Provider(transport, new FakeClock(), refreshToken: "off-handed-0");

            Dictionary<string, string> form = Assert.Single(transport.Forms);
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("off-handed-0", form["refresh_token"]);
            Assert.False(form.ContainsKey("password"));
            Assert.Equal("acc-1", provider.AccessToken);
        }

        [Fact]
        public void T43_APasswordBesideTheTokenIsNeverSent()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Status(503, "{}"), Step.Token("acc-3"));
            using KeycloakTokenProvider provider = Provider(transport, clock, refreshToken: "off-handed-0");
            clock.Now = 1280;
            provider.GetAuthorizationMetadata();
            clock.Now = 1290;
            provider.GetAuthorizationMetadata();

            Assert.Equal(Enumerable.Repeat("refresh_token", 3), transport.Grants);
            Assert.All(transport.Forms, form => Assert.False(form.ContainsKey("password")));
            Assert.Equal("acc-3", provider.AccessToken);
        }

        [Fact]
        public void T44_ARefusedStartUpTokenIsNeverAnsweredWithAPasswordLogin()
        {
            var transport = new FakeTransport(Step.Refused(), Step.Token("acc-1"));

            KeycloakAuthenticationException error = Assert.Throws<KeycloakAuthenticationException>(
                () => Provider(transport, new FakeClock(), refreshToken: "off-handed-0"));

            Assert.Contains("Keycloak token refresh failed with status 400", error.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "refresh_token" }, transport.Grants);
        }

        [Fact]
        public void T45_ARotatedTokenIsUsedForTheNextRefresh()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1", 300, "off-rotated"), Step.Token("acc-2"));
            using KeycloakTokenProvider provider = Provider(transport, clock, refreshToken: "off-handed-0");
            clock.Now = 1280;
            provider.GetAuthorizationMetadata();

            Assert.Equal("off-rotated", transport.Forms[1]["refresh_token"]);
        }

        [Fact]
        public async Task T46_TheDeadlineIsArmedOnTheTokenPath()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1", 60));
            using KeycloakTokenProvider provider = Provider(transport, clock, tokenExpirationInS: 200, refreshToken: "off-handed-0");
            var stop = new FakeStopSignal(clock, 1000);

            Assert.Equal(1200.0, provider.LoginDeadline!.Value);
            await RunLoop(provider, stop, clock);

            Assert.True(stop.IsSet);
            Assert.True(clock.Now <= 1201.0, $"the loop ran until {clock.Now}");
            // After the deadline the lazy path no longer refreshes: the token is allowed to lapse.
            int requests = transport.Forms.Count;
            clock.Now = 5000;
            Assert.Equal(Bearer1, provider.GetAuthorizationMetadata().Value);
            Assert.Equal(requests, transport.Forms.Count);
        }

        [Fact]
        public async Task T47_TheLoopSurvivesAFailingOfflineToken()
        {
            var clock = new FakeClock();
            var transport = new FakeTransport(Step.Token("acc-1"), Step.Refused(), Step.Refused(), Step.Token("acc-4"));
            using KeycloakTokenProvider provider = Provider(transport, clock, refreshToken: "off-handed-0");

            await RunLoop(provider, new FakeStopSignal(clock, 3), clock);

            Assert.Equal(Enumerable.Repeat("refresh_token", 4), transport.Grants);
            Assert.Equal("acc-4", provider.AccessToken);
        }

        // ---------------------------------------------------------------- the gRPC attachment surface

        [Fact]
        public async Task TheInterceptorAndTheCredentialsAttachTheBearerToken()
        {
            using KeycloakTokenProvider provider = Provider(new FakeTransport(Step.Token("acc-1")), new FakeClock());
            var metadata = new Metadata { { "x-caller", "test" } };

            await provider.CreateInterceptor()(new AuthInterceptorContext("https://nlu.ondewo.com", "GetUser"), metadata);

            Assert.Equal(Bearer1, metadata.GetValue("authorization"));
            Assert.Equal(Bearer1, Assert.Single(provider.CreateBearerMetadata()).Value);
            Assert.NotNull(provider.CreateCallCredentials());
        }

        [Fact]
        public void TheConstructorRefusesNullArguments()
        {
            var clock = new FakeClock();
            Assert.Throws<ArgumentNullException>(() => new KeycloakTokenProvider(null!, Realm, ClientId, Username, Password));
            Assert.Throws<ArgumentNullException>(() => new KeycloakTokenProvider(Url, null!, ClientId, Username, Password));
            Assert.Throws<ArgumentNullException>(() => new KeycloakTokenProvider(Url, Realm, null!, Username, Password));
            Assert.Throws<ArgumentNullException>(() => new KeycloakTokenProvider(Url, Realm, ClientId, null!, Password));
            Assert.Throws<ArgumentNullException>(() => new KeycloakTokenProvider(Url, Realm, ClientId, Username, null!));
            Assert.Throws<ArgumentNullException>(() => new KeycloakTokenProvider(
                Url, Realm, ClientId, Username, Password, timeFn: clock.Time, refreshToken: null!));
        }

        [Fact]
        public void AResponseWithANullBodyReadsAsEmpty()
        {
            Assert.Equal(string.Empty, new KeycloakTokenResponse(502, null).Body);
            Assert.Equal(502, new KeycloakTokenResponse(502, null).StatusCode);
        }

        [Fact]
        public void T55_RefreshTokenIsTheLastParameterOfThePublicConstructor()
        {
            System.Reflection.ConstructorInfo constructor = Assert.Single(typeof(KeycloakTokenProvider).GetConstructors());

            Assert.Equal("refreshToken", constructor.GetParameters().Last().Name);
        }
    }
}
