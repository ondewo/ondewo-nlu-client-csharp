#if NET
#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Ondewo.Nlu.Client.Connection
{
    /// <summary>
    /// Where and how to connect to an ONDEWO server: host, port and the TLS material.
    /// <para>
    /// Pass it to <see cref="OndewoChannelFactory.Create"/> to get a <c>GrpcChannel</c>. The three
    /// certificate fields hold PEM <b>content</b>, never a file path - read the files yourself
    /// (<c>File.ReadAllText</c>):
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="GrpcCert"/>: the CA certificate(s) the server certificate is verified
    /// against. Empty means the platform's default trust store.</item>
    /// <item><see cref="GrpcClientCert"/> and <see cref="GrpcClientKey"/>: the client identity for
    /// mutual TLS. Both or neither: the constructor refuses half a pair.</item>
    /// </list>
    /// <para>
    /// Every check runs in the constructor, so an invalid configuration fails where it is built.
    /// No exception message and no <see cref="ToString"/> output renders a PEM or the key.
    /// </para>
    /// </summary>
    public sealed class OndewoClientConfig
    {
        /// <summary>What <see cref="ToString"/> renders in place of a non-empty <see cref="GrpcClientKey"/>.</summary>
        public const string Redacted = "***REDACTED***";

        /// <summary>
        /// Builds and validates a connection configuration.
        /// </summary>
        /// <param name="host">Host name or IP literal of the server, without scheme or port. A bare
        /// IPv6 literal (<c>::1</c>) is bracketed by <see cref="HostAndPort"/>.</param>
        /// <param name="port">TCP port of the server, 1 to 65535.</param>
        /// <param name="grpcCert">PEM content of the CA certificate(s) to trust. Null or empty: the
        /// platform's default trust store.</param>
        /// <param name="grpcClientCert">PEM content of the client certificate (chain) for mutual TLS,
        /// together with <paramref name="grpcClientKey"/>.</param>
        /// <param name="grpcClientKey">PEM content of the unencrypted private key of
        /// <paramref name="grpcClientCert"/>.</param>
        /// <param name="useSecureChannel">True (the default) for TLS; false for a plaintext channel,
        /// for local development only.</param>
        /// <param name="keycloakUrl">Keycloak base URL, the part before <c>/realms/&lt;realm&gt;</c>, e.g.
        /// <c>https://host/auth</c>. Together with <paramref name="realm"/> and <paramref name="clientId"/> it
        /// switches Keycloak authentication on; all three or none.</param>
        /// <param name="realm">Keycloak realm, e.g. <c>ondewo-ccai-platform</c>.</param>
        /// <param name="clientId">The PUBLIC SDK client id, e.g. <c>ondewo-nlu-cai-sdk-public</c>; no client
        /// secret exists and none is sent.</param>
        /// <param name="username">Technical user of the password grant.</param>
        /// <param name="password">Its password. A secret, redacted by <see cref="ToString"/>.</param>
        /// <param name="tokenExpirationInS">Stop auto-refreshing this many seconds after the initial token
        /// acquisition; null (the default) refreshes until the offline session itself dies.</param>
        /// <param name="verifySsl">Verify Keycloak's TLS certificate on the token-endpoint call only; false is
        /// for a self-signed local proxy.</param>
        /// <param name="refreshToken">A handed-off Keycloak OFFLINE refresh token used instead of the password:
        /// the provider starts with one refresh grant and never sends a password grant. A secret, redacted by
        /// <see cref="ToString"/>. Last on purpose, so positional callers keep their arguments.</param>
        /// <exception cref="ArgumentException">The host is empty; only one of
        /// <paramref name="grpcClientCert"/> and <paramref name="grpcClientKey"/> is set; or a client
        /// identity is set together with <paramref name="useSecureChannel"/> = false; only one or two of
        /// <paramref name="keycloakUrl"/>, <paramref name="realm"/> and <paramref name="clientId"/> are set; or
        /// Keycloak is configured with neither a <paramref name="password"/> nor a
        /// <paramref name="refreshToken"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The port is outside 1 to 65535.</exception>
        public OndewoClientConfig(
            string host,
            int port,
            string? grpcCert = null,
            string? grpcClientCert = null,
            string? grpcClientKey = null,
            bool useSecureChannel = true,
            string? keycloakUrl = null,
            string? realm = null,
            string? clientId = null,
            string? username = null,
            string? password = null,
            int? tokenExpirationInS = null,
            bool verifySsl = true,
            string? refreshToken = null)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException(
                    $"{nameof(OndewoClientConfig)}.{nameof(Host)} is required - it is empty or whitespace",
                    nameof(host));
            }

            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(port),
                    port,
                    $"{nameof(OndewoClientConfig)}.{nameof(Port)} must be between 1 and 65535");
            }

            Host = host;
            Port = port;
            GrpcCert = grpcCert ?? string.Empty;
            GrpcClientCert = grpcClientCert ?? string.Empty;
            GrpcClientKey = grpcClientKey ?? string.Empty;
            UseSecureChannel = useSecureChannel;
            KeycloakUrl = keycloakUrl ?? string.Empty;
            Realm = realm ?? string.Empty;
            ClientId = clientId ?? string.Empty;
            Username = username ?? string.Empty;
            Password = password ?? string.Empty;
            TokenExpirationInS = tokenExpirationInS;
            VerifySsl = verifySsl;
            RefreshToken = refreshToken ?? string.Empty;

            // Half a client identity cannot be presented: refuse it here, where it was configured,
            // instead of letting the TLS stack fail on it at the first call.
            bool hasCert = IsSet(GrpcClientCert);
            bool hasKey = IsSet(GrpcClientKey);
            if (hasCert != hasKey)
            {
                string present = hasCert ? nameof(GrpcClientCert) : nameof(GrpcClientKey);
                throw new ArgumentException(
                    $"{nameof(OndewoClientConfig)} for {HostAndPort} received only {present}; set both "
                    + $"{nameof(GrpcClientCert)} and {nameof(GrpcClientKey)} to use mutual TLS, or neither.");
            }

            if (!useSecureChannel && hasCert)
            {
                throw new ArgumentException(
                    $"{nameof(OndewoClientConfig)} for {HostAndPort} carries a client identity "
                    + $"({nameof(GrpcClientCert)} / {nameof(GrpcClientKey)}) but {nameof(UseSecureChannel)} is "
                    + "false: a plaintext channel cannot present it. Use a secure channel or remove the identity.");
            }

            // All three or none: half a Keycloak configuration would otherwise silently send no
            // authorization at all, which a server reports as UNAUTHENTICATED without naming the cause.
            int keycloakFields = new[] { KeycloakUrl, Realm, ClientId }.Count(field => field.Length > 0);
            if (keycloakFields is > 0 and < 3)
            {
                throw new ArgumentException(
                    "The Keycloak fields `keycloak_url`, `realm`, and `client_id` must be provided together in "
                    + $"{nameof(OndewoClientConfig)} ({nameof(KeycloakUrl)}, {nameof(Realm)}, {nameof(ClientId)}).");
            }

            if (KeycloakConfigured && Password.Length == 0 && RefreshToken.Length == 0)
            {
                throw new ArgumentException(
                    "Either the field `password` or the field `refresh_token` is mandatory in "
                    + $"{nameof(OndewoClientConfig)} when Keycloak is configured ({nameof(Password)} or {nameof(RefreshToken)}).");
            }
        }

        /// <summary>Host name or IP literal of the server.</summary>
        public string Host { get; }

        /// <summary>TCP port of the server.</summary>
        public int Port { get; }

        /// <summary>PEM content of the CA certificate(s) to trust; empty for the platform's trust store.</summary>
        public string GrpcCert { get; }

        /// <summary>PEM content of the client certificate (chain) for mutual TLS; empty for none.</summary>
        public string GrpcClientCert { get; }

        /// <summary>
        /// PEM content of the client private key for mutual TLS; empty for none. A secret:
        /// <see cref="ToString"/> renders it as <see cref="Redacted"/>.
        /// </summary>
        public string GrpcClientKey { get; }

        /// <summary>True for a TLS channel, false for a plaintext one.</summary>
        public bool UseSecureChannel { get; }

        /// <summary>Keycloak base URL; empty when Keycloak is not configured.</summary>
        public string KeycloakUrl { get; }

        /// <summary>Keycloak realm; empty when Keycloak is not configured.</summary>
        public string Realm { get; }

        /// <summary>The public SDK client id; empty when Keycloak is not configured.</summary>
        public string ClientId { get; }

        /// <summary>Technical user of the password grant.</summary>
        public string Username { get; }

        /// <summary>Technical user password. A secret: <see cref="ToString"/> renders it as <see cref="Redacted"/>.</summary>
        public string Password { get; }

        /// <summary>The auto-refresh bound in seconds since the initial token acquisition, or null.</summary>
        public int? TokenExpirationInS { get; }

        /// <summary>Whether the token-endpoint call verifies Keycloak's TLS certificate.</summary>
        public bool VerifySsl { get; }

        /// <summary>
        /// A handed-off Keycloak offline refresh token used instead of <see cref="Password"/>. A secret:
        /// <see cref="ToString"/> renders it as <see cref="Redacted"/>.
        /// </summary>
        public string RefreshToken { get; }

        /// <summary>
        /// True when <see cref="KeycloakUrl"/>, <see cref="Realm"/> and <see cref="ClientId"/> are set:
        /// <see cref="OndewoChannelFactory.Create"/> then attaches a Keycloak bearer token to every call.
        /// </summary>
        public bool KeycloakConfigured => KeycloakUrl.Length > 0 && Realm.Length > 0 && ClientId.Length > 0;

        /// <summary>True when both <see cref="GrpcClientCert"/> and <see cref="GrpcClientKey"/> are set.</summary>
        public bool HasClientIdentity => IsSet(GrpcClientCert) && IsSet(GrpcClientKey);

        /// <summary>
        /// <c>host:port</c>, with a bare IPv6 literal bracketed (<c>::1</c> becomes <c>[::1]:50051</c>).
        /// An already bracketed host is left as it is.
        /// </summary>
        public string HostAndPort =>
            !Host.StartsWith("[", StringComparison.Ordinal)
            && IPAddress.TryParse(Host, out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{Host}]:{Port}"
                : $"{Host}:{Port}";

        /// <summary>
        /// Renders the configuration for logs. <see cref="GrpcClientKey"/>, <see cref="Password"/> and
        /// <see cref="RefreshToken"/> are rendered as <see cref="Redacted"/> (empty when they are empty),
        /// and the certificates only by their length.
        /// </summary>
        /// <returns>A one-line description that never contains a PEM or the key.</returns>
        public override string ToString()
        {
            return $"{nameof(OndewoClientConfig)} {{ {nameof(Host)} = {Host}, {nameof(Port)} = {Port}, "
                   + $"{nameof(GrpcCert)} = {DescribePem(GrpcCert)}, "
                   + $"{nameof(GrpcClientCert)} = {DescribePem(GrpcClientCert)}, "
                   + $"{nameof(GrpcClientKey)} = {(IsSet(GrpcClientKey) ? Redacted : string.Empty)}, "
                   + $"{nameof(UseSecureChannel)} = {UseSecureChannel}, "
                   + $"{nameof(KeycloakUrl)} = {KeycloakUrl}, {nameof(Realm)} = {Realm}, {nameof(ClientId)} = {ClientId}, "
                   + $"{nameof(Username)} = {Username}, {nameof(Password)} = {RedactSecret(Password)}, "
                   + $"{nameof(TokenExpirationInS)} = {TokenExpirationInS}, {nameof(VerifySsl)} = {VerifySsl}, "
                   + $"{nameof(RefreshToken)} = {RedactSecret(RefreshToken)} }}";
        }

        internal static bool IsSet(string value) => !string.IsNullOrWhiteSpace(value);

        private static string RedactSecret(string secret) => secret.Length > 0 ? Redacted : string.Empty;

        private static string DescribePem(string pem) => IsSet(pem) ? $"<PEM, {pem.Length} chars>" : string.Empty;
    }
}
#endif
