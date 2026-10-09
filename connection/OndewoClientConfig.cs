#if NET
#nullable enable
using System;
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
        /// <exception cref="ArgumentException">The host is empty; only one of
        /// <paramref name="grpcClientCert"/> and <paramref name="grpcClientKey"/> is set; or a client
        /// identity is set together with <paramref name="useSecureChannel"/> = false.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The port is outside 1 to 65535.</exception>
        public OndewoClientConfig(
            string host,
            int port,
            string? grpcCert = null,
            string? grpcClientCert = null,
            string? grpcClientKey = null,
            bool useSecureChannel = true)
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
        /// Renders the configuration for logs. <see cref="GrpcClientKey"/> is rendered as
        /// <see cref="Redacted"/> (empty when it is empty), and the certificates only by their length.
        /// </summary>
        /// <returns>A one-line description that never contains a PEM or the key.</returns>
        public override string ToString()
        {
            return $"{nameof(OndewoClientConfig)} {{ {nameof(Host)} = {Host}, {nameof(Port)} = {Port}, "
                   + $"{nameof(GrpcCert)} = {DescribePem(GrpcCert)}, "
                   + $"{nameof(GrpcClientCert)} = {DescribePem(GrpcClientCert)}, "
                   + $"{nameof(GrpcClientKey)} = {(IsSet(GrpcClientKey) ? Redacted : string.Empty)}, "
                   + $"{nameof(UseSecureChannel)} = {UseSecureChannel} }}";
        }

        internal static bool IsSet(string value) => !string.IsNullOrWhiteSpace(value);

        private static string DescribePem(string pem) => IsSet(pem) ? $"<PEM, {pem.Length} chars>" : string.Empty;
    }
}
#endif
