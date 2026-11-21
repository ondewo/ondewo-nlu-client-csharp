#if NET
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ondewo.Nlu.Client.Auth
{
    /// <summary>
    /// Raised for every failure to acquire or refresh a Keycloak access token: the endpoint could not be
    /// reached, answered a non-2xx status or an unusable body, or the access token has expired and could
    /// not be refreshed. The underlying error, if any, is the <see cref="Exception.InnerException"/>. The
    /// message never contains a password, a refresh token or an access token.
    /// </summary>
    public sealed class KeycloakAuthenticationException : Exception
    {
        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">What failed; never a credential.</param>
        public KeycloakAuthenticationException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a message and the error that caused it.</summary>
        /// <param name="message">What failed; never a credential.</param>
        /// <param name="innerException">The underlying error.</param>
        public KeycloakAuthenticationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>The status code and raw body of one token-endpoint answer.</summary>
    public sealed class KeycloakTokenResponse
    {
        /// <summary>Creates a response.</summary>
        /// <param name="statusCode">The HTTP status code.</param>
        /// <param name="body">The raw response body; null is read as empty.</param>
        public KeycloakTokenResponse(int statusCode, string? body)
        {
            StatusCode = statusCode;
            Body = body ?? string.Empty;
        }

        /// <summary>The HTTP status code.</summary>
        public int StatusCode { get; }

        /// <summary>The raw response body.</summary>
        public string Body { get; }
    }

    /// <summary>
    /// The HTTP transport for the Keycloak token endpoint. The default sends a real
    /// <c>application/x-www-form-urlencoded</c> POST; unit tests inject a fake so the provider can be
    /// exercised without any network.
    /// </summary>
    public interface IKeycloakTokenTransport
    {
        /// <summary>Sends a form-encoded POST and returns the answer.</summary>
        /// <param name="url">The token endpoint.</param>
        /// <param name="form">The form fields.</param>
        /// <param name="timeout">Bound for the whole exchange (connect + read).</param>
        /// <returns>The status code and raw body. A network failure or timeout is thrown.</returns>
        Task<KeycloakTokenResponse> PostAsync(string url, IReadOnlyDictionary<string, string> form, TimeSpan timeout);
    }

    /// <summary>
    /// The interruptible wait of the background refresh loop, signalled by
    /// <see cref="KeycloakTokenProvider.Stop"/>. Injectable so tests can drive the loop without real sleeps.
    /// </summary>
    public interface IKeycloakStopSignal
    {
        /// <summary>True once <see cref="Set"/> was called.</summary>
        bool IsSet { get; }

        /// <summary>Signals the stop, ending any pending <see cref="WaitAsync"/> at once. Idempotent.</summary>
        void Set();

        /// <summary>Waits <paramref name="seconds"/> or until <see cref="Set"/> is called.</summary>
        /// <param name="seconds">How long to wait.</param>
        /// <returns>True when the wait ended because of <see cref="Set"/>, false when it timed out.</returns>
        Task<bool> WaitAsync(double seconds);
    }

    /// <summary>
    /// Acquires and auto-refreshes a Keycloak access token for the headless SDK, and attaches it to gRPC
    /// calls as <c>authorization: Bearer &lt;token&gt;</c>.
    /// <para>
    /// With a <c>refreshToken</c> (a handed-off Keycloak OFFLINE token) the provider starts with ONE
    /// <c>grant_type=refresh_token</c> exchange and never sends a password grant, whatever the password
    /// holds and whatever the outcome. Without one it performs ONE password grant with
    /// <c>scope=offline_access</c>. A failure of that first acquisition is thrown from the constructor.
    /// </para>
    /// <para>
    /// Afterwards the access token is refreshed proactively by a background loop that wakes
    /// <see cref="ExpiryLeewaySeconds"/> before expiry, and lazily on every read
    /// (<see cref="GetAuthorizationMetadataAsync"/>, the interceptor, the call credentials). A failed refresh
    /// never ends the loop: it is logged once as a WARNING and retried with full-jitter exponential backoff
    /// (<see cref="RetryBaseDelaySeconds"/> doubling up to <see cref="RetryMaxDelaySeconds"/>), shared by
    /// both paths, so an outage costs one token request per backoff step instead of one per gRPC call. While
    /// the current token is still valid it keeps being served; once it has expired the read throws
    /// <see cref="KeycloakAuthenticationException"/>. A refused refresh token is NEVER answered with a
    /// password login. With <c>tokenExpirationInS</c> the refresh stops that many seconds after the initial
    /// acquisition and the token is allowed to lapse.
    /// </para>
    /// <para>Dispose (or <see cref="Stop"/>) it to end the background loop; the lazy path keeps working.</para>
    /// </summary>
    public sealed class KeycloakTokenProvider : IDisposable
    {
        /// <summary>Refresh the access token this long before it expires.</summary>
        public const double ExpiryLeewaySeconds = 30.0;

        /// <summary>Floor of any background wait, so an <c>expires_in</c> of 0 cannot spin the loop.</summary>
        public const double MinRefreshDelaySeconds = 1.0;

        /// <summary>First retry delay after a failed refresh.</summary>
        public const double RetryBaseDelaySeconds = 5.0;

        /// <summary>Ceiling of the failure backoff.</summary>
        public const double RetryMaxDelaySeconds = 300.0;

        /// <summary>Exponent cap of the backoff: 5 * 2^6 = 320 already exceeds the ceiling.</summary>
        public const int MaxRetryExponent = 6;

        /// <summary>Bound of one token-endpoint call (connect + read); below the leeway on purpose.</summary>
        public const double HttpTimeoutSeconds = 10.0;

        /// <summary>Longest slice of an error body quoted in an exception message.</summary>
        public const int MaxErrorBodyChars = 500;

        /// <summary>Bounded wait for the background loop on <see cref="Stop"/>.</summary>
        public const double ThreadJoinTimeoutSeconds = 5.0;

        private static readonly AsyncLocal<bool> InsideRefreshLoop = new();

        private readonly IKeycloakTokenTransport transport;
        private readonly Func<double> timeFn;
        private readonly Func<double> randomFn;
        private readonly IKeycloakStopSignal stopSignal;
        private readonly ILogger logger;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly string tokenEndpoint;
        private Task? worker;

        /// <summary>
        /// Acquires the initial token and starts the background refresh.
        /// </summary>
        /// <param name="keycloakUrl">Keycloak base URL, the part before <c>/realms/&lt;realm&gt;</c>; trailing
        /// <c>/</c> characters are stripped.</param>
        /// <param name="realm">The realm, e.g. <c>ondewo-ccai-platform</c>.</param>
        /// <param name="clientId">The PUBLIC SDK client id; no client secret is ever sent.</param>
        /// <param name="username">Technical user of the password grant.</param>
        /// <param name="password">Its password; unused when <paramref name="refreshToken"/> is set.</param>
        /// <param name="tokenExpirationInS">Stop refreshing this many seconds after the initial acquisition;
        /// null refreshes until the offline session itself dies.</param>
        /// <param name="verifySsl">Verify Keycloak's TLS certificate (default transport only); false is for a
        /// self-signed local proxy.</param>
        /// <param name="transport">Test seam: the token-endpoint transport. Null sends real HTTP.</param>
        /// <param name="timeFn">Test seam: a monotonic clock in seconds. Null uses <see cref="Stopwatch"/>.</param>
        /// <param name="stopSignal">Test seam: the interruptible wait of the background loop.</param>
        /// <param name="startBackgroundRefresh">Test seam: false starts no background loop.</param>
        /// <param name="randomFn">Test seam: the backoff jitter, a fraction in [0, 1); consulted on failures
        /// only.</param>
        /// <param name="logger">Receives one WARNING per failed refresh and one INFO on recovery; never a
        /// credential. Null logs nothing.</param>
        /// <param name="refreshToken">A handed-off Keycloak offline refresh token used instead of the
        /// password. Last on purpose: appending keeps every positional caller's arguments where they were.</param>
        /// <exception cref="KeycloakAuthenticationException">The initial acquisition failed.</exception>
        public KeycloakTokenProvider(
            string keycloakUrl,
            string realm,
            string clientId,
            string username,
            string password,
            int? tokenExpirationInS = null,
            bool verifySsl = true,
            IKeycloakTokenTransport? transport = null,
            Func<double>? timeFn = null,
            IKeycloakStopSignal? stopSignal = null,
            bool startBackgroundRefresh = true,
            Func<double>? randomFn = null,
            ILogger? logger = null,
            string refreshToken = "")
            : this(keycloakUrl, realm, clientId, username, password, tokenExpirationInS, verifySsl, transport,
                timeFn, stopSignal, startBackgroundRefresh, randomFn, logger, refreshToken, KeycloakTimings.Default)
        {
        }

        internal KeycloakTokenProvider(
            string keycloakUrl,
            string realm,
            string clientId,
            string username,
            string password,
            int? tokenExpirationInS,
            bool verifySsl,
            IKeycloakTokenTransport? transport,
            Func<double>? timeFn,
            IKeycloakStopSignal? stopSignal,
            bool startBackgroundRefresh,
            Func<double>? randomFn,
            ILogger? logger,
            string refreshToken,
            KeycloakTimings timings)
        {
            ArgumentNullException.ThrowIfNull(keycloakUrl);
            ArgumentNullException.ThrowIfNull(realm);
            ArgumentNullException.ThrowIfNull(clientId);
            ArgumentNullException.ThrowIfNull(username);
            ArgumentNullException.ThrowIfNull(password);
            ArgumentNullException.ThrowIfNull(refreshToken);
            KeycloakUrl = keycloakUrl.TrimEnd('/');
            Realm = realm;
            ClientId = clientId;
            Username = username;
            TokenExpirationInS = tokenExpirationInS;
            VerifySsl = verifySsl;
            Password = password;
            Timings = timings;
            this.transport = transport ?? HttpKeycloakTokenTransport.For(verifySsl);
            this.timeFn = timeFn ?? MonotonicSeconds;
            this.randomFn = randomFn ?? Random.Shared.NextDouble;
            this.stopSignal = stopSignal ?? new KeycloakStopSignal();
            this.logger = logger ?? NullLogger.Instance;
            tokenEndpoint = $"{KeycloakUrl}/realms/{realm}/protocol/openid-connect/token";
            RefreshToken = refreshToken;

            // A refresh grant with a handed-off offline token, or one password grant: never both, never a
            // fallback. Keycloak's brute-force detection counts failed LOGINS, so N processes refreshing one
            // offline token is not the burst the realm penalises, while N password grants is.
            string action = refreshToken.Length > 0 ? "refresh" : "login";
            try
            {
                Task acquisition = refreshToken.Length > 0 ? RefreshAsync() : LoginAsync();
                acquisition.GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not KeycloakAuthenticationException)
            {
                throw new KeycloakAuthenticationException(
                    $"Keycloak token {action} failed ({exception.GetType().Name}: {exception.Message})", exception);
            }

            // Armed on BOTH paths, after the acquisition, so the bound means the same on the token path.
            if (tokenExpirationInS.HasValue)
            {
                LoginDeadline = this.timeFn() + tokenExpirationInS.Value;
            }

            if (startBackgroundRefresh)
            {
                var self = new WeakReference<KeycloakTokenProvider>(this);
                IKeycloakStopSignal signal = this.stopSignal;
                Func<double> clock = this.timeFn;
                worker = Task.Run(() => RunRefreshLoopAsync(self, signal, clock));
            }
        }

        /// <summary>Stops the background loop when the provider is collected; never waits, never throws.</summary>
        ~KeycloakTokenProvider()
        {
            SignalStop();
        }

        /// <summary>The Keycloak base URL, trailing <c>/</c> stripped.</summary>
        public string KeycloakUrl { get; }

        /// <summary>The realm.</summary>
        public string Realm { get; }

        /// <summary>The public SDK client id.</summary>
        public string ClientId { get; }

        /// <summary>The technical user of the password grant.</summary>
        public string Username { get; }

        /// <summary>The auto-refresh bound in seconds since the initial acquisition, or null.</summary>
        public int? TokenExpirationInS { get; }

        /// <summary>Whether the default transport verifies Keycloak's TLS certificate.</summary>
        public bool VerifySsl { get; }

        /// <summary>The most recently issued access token.</summary>
        public string AccessToken { get; private set; } = string.Empty;

        /// <summary>True once <see cref="Stop"/> or <see cref="Dispose"/> was called.</summary>
        public bool IsClosed { get; private set; }

        internal string Password { get; }

        internal string RefreshToken { get; private set; }

        internal KeycloakTimings Timings { get; }

        internal double AccessTokenExpiresAt { get; private set; }

        internal double? LoginDeadline { get; private set; }

        internal int ConsecutiveRefreshFailures { get; private set; }

        internal double NextRefreshAttemptAt { get; private set; }

        internal Task? Worker => worker;

        /// <summary>
        /// The <c>("authorization", "Bearer &lt;token&gt;")</c> metadata pair, refreshing the token first when it
        /// is inside the leeway. Concurrent callers share ONE token request.
        /// </summary>
        /// <param name="cancellationToken">Cancels waiting for a refresh another caller is running.</param>
        /// <returns>The lowercase metadata key and the bearer value.</returns>
        /// <exception cref="KeycloakAuthenticationException">The token has expired and could not be refreshed.</exception>
        public async Task<KeyValuePair<string, string>> GetAuthorizationMetadataAsync(
            CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RefreshIfWithinWindowAsync(timeFn()).ConfigureAwait(false);
                return new KeyValuePair<string, string>(OndewoAuth.AuthorizationHeader, OndewoAuth.BearerPrefix + AccessToken);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>The blocking form of <see cref="GetAuthorizationMetadataAsync"/>.</summary>
        /// <returns>The lowercase metadata key and the bearer value.</returns>
        /// <exception cref="KeycloakAuthenticationException">The token has expired and could not be refreshed.</exception>
        public KeyValuePair<string, string> GetAuthorizationMetadata()
        {
            return GetAuthorizationMetadataAsync().GetAwaiter().GetResult();
        }

        /// <summary>Call metadata carrying the current bearer token, for a generated method's <c>headers</c>.</summary>
        /// <returns>Metadata holding exactly the authorization entry.</returns>
        /// <exception cref="KeycloakAuthenticationException">The token has expired and could not be refreshed.</exception>
        public Metadata CreateBearerMetadata()
        {
            KeyValuePair<string, string> entry = GetAuthorizationMetadata();
            return new Metadata { { entry.Key, entry.Value } };
        }

        /// <summary>An interceptor adding the current bearer token to every call it is applied to.</summary>
        /// <returns>The interceptor.</returns>
        public AsyncAuthInterceptor CreateInterceptor()
        {
            return async (context, metadata) =>
            {
                KeyValuePair<string, string> entry =
                    await GetAuthorizationMetadataAsync(context.CancellationToken).ConfigureAwait(false);
                metadata.Add(entry.Key, entry.Value);
            };
        }

        /// <summary>Call credentials adding the current bearer token to every call on a channel.</summary>
        /// <returns>The call credentials.</returns>
        public CallCredentials CreateCallCredentials()
        {
            return CallCredentials.FromInterceptor(CreateInterceptor());
        }

        /// <summary>
        /// Ends the background loop, interrupting a pending wait (a backoff wait included) so no further
        /// request is sent, and waits for it at most <see cref="ThreadJoinTimeoutSeconds"/>. Idempotent, never
        /// throws, never waits for itself. The lazy path keeps working afterwards.
        /// </summary>
        public void Stop()
        {
            IsClosed = true;
            SignalStop();
            Task? running = worker;
            if (running == null || InsideRefreshLoop.Value)
            {
                return;
            }

            try
            {
                running.Wait(TimeSpan.FromSeconds(Timings.ThreadJoinTimeoutS));
            }
            catch (Exception)
            {
                // The loop never faults by design; a wait that fails anyway must not escape a teardown.
            }
        }

        /// <summary>An alias of <see cref="Stop"/>.</summary>
        public void Close()
        {
            Stop();
        }

        /// <summary>Stops the background loop (<see cref="Stop"/>).</summary>
        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// The wait before the next attempt after <paramref name="consecutiveFailures"/> failed refreshes:
        /// uniform in [base, ceiling], the ceiling doubling from <see cref="RetryBaseDelaySeconds"/> up to
        /// <see cref="RetryMaxDelaySeconds"/>. The jitter keeps N containers that failed in the same instant
        /// from retrying in lockstep for the whole outage.
        /// </summary>
        /// <param name="consecutiveFailures">Failed refreshes so far; below 1 counts as 1.</param>
        /// <param name="randomFn">The jitter fraction; a value outside [0, 1] is clamped.</param>
        /// <returns>Seconds to wait.</returns>
        public static double BackoffDelaySeconds(long consecutiveFailures, Func<double> randomFn)
        {
            return BackoffDelaySeconds(consecutiveFailures, randomFn, KeycloakTimings.Default);
        }

        internal static double BackoffDelaySeconds(long consecutiveFailures, Func<double> randomFn, KeycloakTimings timings)
        {
            ArgumentNullException.ThrowIfNull(randomFn);
            long exponent = Math.Min(Math.Max(consecutiveFailures, 1) - 1, timings.MaxRetryExponent);
            double ceiling = Math.Min(timings.RetryBaseDelayS * Math.Pow(2, exponent), timings.RetryMaxDelayS);
            double fraction = Math.Clamp(randomFn(), 0.0, 1.0);
            return timings.RetryBaseDelayS + (fraction * (ceiling - timings.RetryBaseDelayS));
        }

        /// <summary>
        /// The background loop. It holds the provider only weakly (so a collected provider ends it), waits on
        /// <paramref name="stop"/> until the leeway before expiry - or, after a failure, until the backoff
        /// allows the next attempt - and then refreshes under the lock. A failure NEVER ends it: it was
        /// recorded and warned already, so it is only logged at DEBUG here.
        /// </summary>
        internal static async Task RunRefreshLoopAsync(
            WeakReference<KeycloakTokenProvider> providerRef, IKeycloakStopSignal stop, Func<double> timeFn)
        {
            InsideRefreshLoop.Value = true;
            while (!stop.IsSet)
            {
                if (!providerRef.TryGetTarget(out KeycloakTokenProvider? provider))
                {
                    return;
                }

                double now = timeFn();
                double? deadline = provider.LoginDeadline;
                if (now >= deadline)
                {
                    // The bounded auto-refresh window has closed: stop renewing and let the token lapse.
                    stop.Set();
                    return;
                }

                double delay = provider.ConsecutiveRefreshFailures > 0
                    ? provider.NextRefreshAttemptAt - now
                    : provider.AccessTokenExpiresAt - provider.Timings.ExpiryLeewayS - now;
                delay = Math.Max(delay, provider.Timings.MinRefreshDelayS);
                delay = Math.Min(delay, (deadline ?? double.PositiveInfinity) - now);

                // Drop the strong reference before sleeping so a long wait cannot pin the provider.
                provider = null;
                if (await stop.WaitAsync(delay).ConfigureAwait(false) || !providerRef.TryGetTarget(out provider))
                {
                    return;
                }

                await provider.gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await provider.RefreshIfWithinWindowAsync(timeFn()).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    provider.logger.LogDebug(
                        "Keycloak background refresh will be retried ({ErrorType}: {ErrorMessage})",
                        exception.GetType().Name,
                        exception.Message);
                }
                finally
                {
                    provider.gate.Release();
                }
            }
        }

        /// <summary>The shared refresh decision of the lazy path and the loop; the caller holds the lock.</summary>
        internal async Task RefreshIfWithinWindowAsync(double now)
        {
            if (now >= LoginDeadline || now < AccessTokenExpiresAt - Timings.ExpiryLeewayS)
            {
                // The window has closed (the token is allowed to lapse), or the token is comfortably valid.
                return;
            }

            if (now < NextRefreshAttemptAt)
            {
                // A previous refresh failed and its backoff has not elapsed: send nothing.
                if (now < AccessTokenExpiresAt)
                {
                    return;
                }

                throw new KeycloakAuthenticationException(
                    "The Keycloak access token has expired and the refresh is backing off after "
                    + $"{ConsecutiveRefreshFailures} consecutive failure(s); the next attempt is due in "
                    + $"{OneDecimal(NextRefreshAttemptAt - now)}s");
            }

            try
            {
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordRefreshFailure(exception);
                // Re-read the clock: the failed attempt itself may have taken up to the HTTP timeout.
                if (timeFn() < AccessTokenExpiresAt)
                {
                    return;
                }

                if (exception is KeycloakAuthenticationException)
                {
                    throw;
                }

                throw new KeycloakAuthenticationException(
                    $"Keycloak token refresh failed ({exception.GetType().Name}: {exception.Message})", exception);
            }

            RecordRefreshSuccess();
        }

        private static double MonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private static string OneDecimal(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        private static bool IsTransportFailure(Exception exception)
        {
            return exception is HttpRequestException or OperationCanceledException or TimeoutException
                or IOException or SocketException;
        }

        private static string JsonTypeName(JsonValueKind kind)
        {
            return kind switch
            {
                JsonValueKind.Array => "array",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.Null => "null",
                _ => "boolean",
            };
        }

        private void SignalStop()
        {
            try
            {
                stopSignal.Set();
            }
            catch (Exception)
            {
                // A stop must never throw, least of all from the finalizer.
            }
        }

        private void RecordRefreshFailure(Exception exception)
        {
            // Measured from when the failure is RECORDED: an attempt that hung for the whole HTTP timeout
            // would otherwise schedule a retry that is already due.
            double now = timeFn();
            ConsecutiveRefreshFailures++;
            double delay = BackoffDelaySeconds(ConsecutiveRefreshFailures, randomFn, Timings);
            NextRefreshAttemptAt = now + delay;
            double remaining = AccessTokenExpiresAt - now;
            string validity = remaining > 0
                ? $"the current access token stays valid for {OneDecimal(remaining)}s"
                : "the current access token has expired";
            logger.LogWarning(
                "Keycloak token refresh failed (attempt {Attempt}); retrying in {Delay}s, {Validity} ({ErrorType}: {ErrorMessage})",
                ConsecutiveRefreshFailures,
                OneDecimal(delay),
                validity,
                exception.GetType().Name,
                exception.Message);
        }

        private void RecordRefreshSuccess()
        {
            if (ConsecutiveRefreshFailures > 0)
            {
                logger.LogInformation(
                    "Keycloak token refresh recovered after {Failures} failed attempt(s)", ConsecutiveRefreshFailures);
            }

            ConsecutiveRefreshFailures = 0;
            NextRefreshAttemptAt = 0;
        }

        private Task LoginAsync()
        {
            return PostAndStoreAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["client_id"] = ClientId,
                    ["username"] = Username,
                    ["password"] = Password,
                    ["scope"] = "offline_access",
                },
                "login");
        }

        private Task RefreshAsync()
        {
            return PostAndStoreAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = ClientId,
                    ["refresh_token"] = RefreshToken,
                },
                "refresh");
        }

        private async Task PostAndStoreAsync(Dictionary<string, string> form, string action)
        {
            KeycloakTokenResponse response;
            try
            {
                response = await transport
                    .PostAsync(tokenEndpoint, form, TimeSpan.FromSeconds(Timings.HttpTimeoutS))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                throw new KeycloakAuthenticationException(
                    $"Keycloak token {action} could not reach the token endpoint "
                    + $"({exception.GetType().Name}: {exception.Message})",
                    exception);
            }

            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                string body = response.Body;
                throw new KeycloakAuthenticationException(
                    $"Keycloak token {action} failed with status {response.StatusCode}: "
                    + body.Substring(0, Math.Min(body.Length, Timings.MaxErrorBodyChars)));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(response.Body);
            }
            catch (JsonException exception)
            {
                throw new KeycloakAuthenticationException(
                    $"Keycloak token {action} returned status {response.StatusCode} with a body that is not JSON",
                    exception);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new KeycloakAuthenticationException(
                        $"Keycloak token {action} returned a JSON {JsonTypeName(root.ValueKind)} instead of an object");
                }

                StoreTokens(root);
            }
        }

        private void StoreTokens(JsonElement payload)
        {
            // Validate everything BEFORE assigning anything: a malformed response changes no state.
            if (!payload.TryGetProperty("access_token", out JsonElement accessToken)
                || accessToken.ValueKind != JsonValueKind.String
                || accessToken.GetString()!.Length == 0)
            {
                // The field NAMES only, never a value: a token response is made of credentials.
                IEnumerable<string> names = payload.EnumerateObject()
                    .Select(property => $"'{property.Name}'")
                    .OrderBy(name => name, StringComparer.Ordinal);
                throw new KeycloakAuthenticationException(
                    "Keycloak token response did not contain an access_token; the response carried the fields "
                    + $"[{string.Join(", ", names)}] (values withheld: a token response is credential material)");
            }

            double expiresIn = 0;
            if (payload.TryGetProperty("expires_in", out JsonElement expires) && !TryReadSeconds(expires, out expiresIn))
            {
                throw new KeycloakAuthenticationException(
                    "Keycloak token response carried a non-numeric expires_in (value withheld)");
            }

            AccessToken = accessToken.GetString()!;
            // Keycloak re-issues the refresh token (rotation); keep the previous one when it is absent.
            if (payload.TryGetProperty("refresh_token", out JsonElement rotated)
                && rotated.ValueKind == JsonValueKind.String
                && rotated.GetString()!.Length > 0)
            {
                RefreshToken = rotated.GetString()!;
            }

            AccessTokenExpiresAt = timeFn() + Math.Max(expiresIn, 0);
        }

        private static bool TryReadSeconds(JsonElement value, out double seconds)
        {
            seconds = 0;
            bool parsed = value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetDouble(out seconds),
                JsonValueKind.String => double.TryParse(
                    value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds),
                _ => false,
            };
            return parsed && double.IsFinite(seconds);
        }
    }

    /// <summary>The tunable constants; production uses <see cref="Default"/>, tests shrink them.</summary>
    internal sealed class KeycloakTimings
    {
        internal static readonly KeycloakTimings Default = new();

        internal readonly double ExpiryLeewayS;
        internal readonly double MinRefreshDelayS;
        internal readonly double RetryBaseDelayS;
        internal readonly double RetryMaxDelayS;
        internal readonly int MaxRetryExponent;
        internal readonly double HttpTimeoutS;
        internal readonly int MaxErrorBodyChars;
        internal readonly double ThreadJoinTimeoutS;

        internal KeycloakTimings(
            double expiryLeewayS = KeycloakTokenProvider.ExpiryLeewaySeconds,
            double minRefreshDelayS = KeycloakTokenProvider.MinRefreshDelaySeconds,
            double retryBaseDelayS = KeycloakTokenProvider.RetryBaseDelaySeconds,
            double retryMaxDelayS = KeycloakTokenProvider.RetryMaxDelaySeconds,
            int maxRetryExponent = KeycloakTokenProvider.MaxRetryExponent,
            double httpTimeoutS = KeycloakTokenProvider.HttpTimeoutSeconds,
            int maxErrorBodyChars = KeycloakTokenProvider.MaxErrorBodyChars,
            double threadJoinTimeoutS = KeycloakTokenProvider.ThreadJoinTimeoutSeconds)
        {
            ExpiryLeewayS = expiryLeewayS;
            MinRefreshDelayS = minRefreshDelayS;
            RetryBaseDelayS = retryBaseDelayS;
            RetryMaxDelayS = retryMaxDelayS;
            MaxRetryExponent = maxRetryExponent;
            HttpTimeoutS = httpTimeoutS;
            MaxErrorBodyChars = maxErrorBodyChars;
            ThreadJoinTimeoutS = threadJoinTimeoutS;
        }
    }

    /// <summary>The production stop signal: a cancellation token cancelling a <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    internal sealed class KeycloakStopSignal : IKeycloakStopSignal
    {
        // Task.Delay refuses more than ~49.7 days; the loop recomputes its wait after this anyway.
        private static readonly double MaxWaitSeconds = TimeSpan.FromDays(1).TotalSeconds;
        private readonly CancellationTokenSource source = new();

        public bool IsSet => source.IsCancellationRequested;

        public void Set()
        {
            source.Cancel();
        }

        public async Task<bool> WaitAsync(double seconds)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, MaxWaitSeconds)), source.Token).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }
    }

    /// <summary>The production transport: one shared <see cref="HttpClient"/> per TLS-verification setting.</summary>
    internal sealed class HttpKeycloakTokenTransport : IKeycloakTokenTransport
    {
        private static readonly HttpKeycloakTokenTransport Verifying = new(verifySsl: true);
        private static readonly HttpKeycloakTokenTransport NonVerifying = new(verifySsl: false);
        private readonly HttpClient client;

        internal HttpKeycloakTokenTransport(bool verifySsl)
        {
            var handler = new SocketsHttpHandler();
            if (!verifySsl)
            {
                // Scoped to THIS client, i.e. to the token-endpoint call only; never process-wide.
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        internal static HttpKeycloakTokenTransport For(bool verifySsl)
        {
            return verifySsl ? Verifying : NonVerifying;
        }

        public async Task<KeycloakTokenResponse> PostAsync(
            string url, IReadOnlyDictionary<string, string> form, TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            using var content = new FormUrlEncodedContent(form);
            try
            {
                using HttpResponseMessage response =
                    await client.PostAsync(url, content, deadline.Token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                return new KeycloakTokenResponse((int)response.StatusCode, body);
            }
            catch (OperationCanceledException exception)
            {
                throw new TimeoutException(
                    $"the token endpoint did not answer within {timeout.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s",
                    exception);
            }
        }
    }
}
#endif
