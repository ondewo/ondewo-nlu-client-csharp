#if NET
#nullable enable
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ondewo.Nlu.Client.Connection
{
    /// <summary>
    /// Builds the <see cref="GrpcChannel"/> every generated <c>&lt;Service&gt;Client</c> takes, from an
    /// <see cref="OndewoClientConfig"/>: plaintext, TLS against the platform's trust store, TLS against
    /// a custom CA (<see cref="OndewoClientConfig.GrpcCert"/>), or mutual TLS.
    /// <code>
    /// var config = new OndewoClientConfig(
    ///     host: "nlu.example.com",
    ///     port: 50055,
    ///     grpcCert: File.ReadAllText("certs/ca.pem"),
    ///     grpcClientCert: File.ReadAllText("certs/client.pem"),
    ///     grpcClientKey: File.ReadAllText("certs/client.key"));
    /// using GrpcChannel channel = OndewoChannelFactory.Create(config);
    /// var client = new Users.UsersClient(channel);
    /// </code>
    /// <para>
    /// The channel uses one <see cref="SocketsHttpHandler"/> (<see cref="CreateHttpHandler"/>) with the
    /// keepalive and message-size defaults the ONDEWO Python client uses, as far as .NET exposes them.
    /// One channel serves every service client of a server; build it once and share it.
    /// </para>
    /// </summary>
    public static class OndewoChannelFactory
    {
        /// <summary>Largest message sent or received, in bytes (2^31 - 1, as in the Python client).</summary>
        public const int MaxMessageLength = int.MaxValue;

        /// <summary>Idle time on an active connection before a keepalive PING (Python: <c>grpc.keepalive_time_ms</c>).</summary>
        public static readonly TimeSpan KeepAlivePingDelay = TimeSpan.FromMilliseconds(30000);

        /// <summary>
        /// Wait for a PING reply before the connection is declared dead (Python:
        /// <c>grpc.http2.ping_timeout_ms</c> / <c>grpc.keepalive_timeout_ms</c>).
        /// </summary>
        public static readonly TimeSpan KeepAlivePingTimeout = TimeSpan.FromMilliseconds(20000);

        /// <summary>Upper bound of the reconnect backoff (Python: <c>grpc.max_reconnect_backoff_ms</c>).</summary>
        public static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromMilliseconds(5000);

        /// <summary>
        /// Builds a channel to <paramref name="config"/>'s server.
        /// </summary>
        /// <param name="config">Host, port and TLS material. It was validated when it was built.</param>
        /// <param name="configure">Optional: adjusts the channel options after the defaults are applied
        /// (e.g. <c>LoggerFactory</c>, <c>Credentials</c> with call credentials, a different message size).
        /// Replacing <c>HttpHandler</c> or <c>HttpClient</c> discards the TLS setup built here.</param>
        /// <returns>The channel. Dispose it when every client using it is done.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        /// <exception cref="ArgumentException">The host does not form a valid address, or a PEM field
        /// cannot be loaded. The message names the field and <c>host:port</c>, never the content.</exception>
        public static GrpcChannel Create(OndewoClientConfig config, Action<GrpcChannelOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            Uri address = GetAddress(config);
            SocketsHttpHandler handler = CreateHttpHandler(config);
            try
            {
                var options = new GrpcChannelOptions
                {
                    HttpHandler = handler,
                    DisposeHttpClient = true,
                    MaxReceiveMessageSize = MaxMessageLength,
                    MaxSendMessageSize = MaxMessageLength,
                    MaxReconnectBackoff = MaxReconnectBackoff,
                };
                configure?.Invoke(options);

                if (!config.UseSecureChannel)
                {
                    ILogger logger = options.LoggerFactory?.CreateLogger(typeof(OndewoChannelFactory).FullName!)
                                     ?? NullLogger.Instance;
                    logger.LogWarning(
                        "Opening an INSECURE (plaintext) gRPC channel to {HostAndPort}: nothing on it is "
                        + "encrypted or authenticated. Use it for local development only.",
                        config.HostAndPort);
                }

                return GrpcChannel.ForAddress(address, options);
            }
            catch
            {
                handler.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The <c>http://</c> or <c>https://</c> address of <paramref name="config"/>'s server.
        /// </summary>
        /// <param name="config">The connection configuration.</param>
        /// <returns><c>https://host:port</c> for a secure channel, <c>http://host:port</c> otherwise.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        /// <exception cref="ArgumentException">The host does not form a valid address.</exception>
        public static Uri GetAddress(OndewoClientConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            string scheme = config.UseSecureChannel ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            if (!Uri.TryCreate($"{scheme}://{config.HostAndPort}", UriKind.Absolute, out Uri? address))
            {
                throw new ArgumentException(
                    $"{nameof(OndewoClientConfig)}.{nameof(OndewoClientConfig.Host)} does not form a valid "
                    + $"{scheme} address with port {config.Port}; give a host name or IP literal without scheme or path.",
                    nameof(config));
            }

            return address;
        }

        /// <summary>
        /// Builds the HTTP/2 handler <see cref="Create"/> puts under the channel: TLS trust and client
        /// identity from <paramref name="config"/>, keepalive pings while calls are active, multiple
        /// HTTP/2 connections when one is full. Use it directly only to build your own
        /// <see cref="GrpcChannelOptions"/>.
        /// </summary>
        /// <param name="config">The connection configuration.</param>
        /// <returns>A new handler; the channel it is given to disposes it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        /// <exception cref="ArgumentException">A PEM field cannot be loaded.</exception>
        public static SocketsHttpHandler CreateHttpHandler(OndewoClientConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = KeepAlivePingDelay,
                KeepAlivePingTimeout = KeepAlivePingTimeout,
                // Python: keepalive_permit_without_calls=False - never ping an idle connection.
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            };
            if (config.UseSecureChannel)
            {
                handler.SslOptions = CreateSslOptions(config);
            }

            return handler;
        }

        private static SslClientAuthenticationOptions CreateSslOptions(OndewoClientConfig config)
        {
            var sslOptions = new SslClientAuthenticationOptions();
            if (OndewoClientConfig.IsSet(config.GrpcCert))
            {
                // Only the given CA(s) are trusted; the host name is still checked against the
                // certificate's subject alternative names by the TLS stack.
                var policy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                };
                policy.CustomTrustStore.AddRange(LoadCertificates(config.GrpcCert, nameof(config.GrpcCert), config));
                sslOptions.CertificateChainPolicy = policy;
            }

            if (config.HasClientIdentity)
            {
                sslOptions.ClientCertificateContext = LoadClientIdentity(config);
            }

            return sslOptions;
        }

        private static X509Certificate2Collection LoadCertificates(string pem, string field, OndewoClientConfig config)
        {
            var certificates = new X509Certificate2Collection();
            try
            {
                certificates.ImportFromPem(pem);
            }
            catch (CryptographicException exception)
            {
                throw new ArgumentException(
                    $"{nameof(OndewoClientConfig)}.{field} for {config.HostAndPort} holds a malformed PEM certificate.",
                    exception);
            }

            if (certificates.Count == 0)
            {
                throw new ArgumentException(
                    $"{nameof(OndewoClientConfig)}.{field} for {config.HostAndPort} contains no PEM certificate. "
                    + "It takes the PEM content of the certificate, not a file path: pass File.ReadAllText(path).");
            }

            return certificates;
        }

        private static SslStreamCertificateContext LoadClientIdentity(OndewoClientConfig config)
        {
            // Any further certificate in GrpcClientCert is an intermediate sent along with the leaf.
            X509Certificate2Collection chain = LoadCertificates(config.GrpcClientCert, nameof(config.GrpcClientCert), config);
            X509Certificate2 leaf;
            try
            {
                using X509Certificate2 ephemeral = X509Certificate2.CreateFromPem(config.GrpcClientCert, config.GrpcClientKey);
                // A PKCS#12 round trip gives the key a form every platform's TLS stack accepts
                // (SChannel on Windows refuses the ephemeral key CreateFromPem returns).
                leaf = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
            }
            catch (CryptographicException exception)
            {
                // A malformed, encrypted or missing key PEM.
                throw ClientIdentityError(config, exception);
            }
            catch (ArgumentException exception)
            {
                // A key that does not belong to the certificate.
                throw ClientIdentityError(config, exception);
            }

            var intermediates = new X509Certificate2Collection();
            foreach (X509Certificate2 certificate in chain)
            {
                if (certificate.Thumbprint != leaf.Thumbprint)
                {
                    intermediates.Add(certificate);
                }
            }

            return SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        }

        private static ArgumentException ClientIdentityError(OndewoClientConfig config, Exception cause)
        {
            return new ArgumentException(
                $"{nameof(OndewoClientConfig)}.{nameof(config.GrpcClientCert)} / {nameof(config.GrpcClientKey)} for "
                + $"{config.HostAndPort} could not be loaded as a PEM certificate and its unencrypted private key "
                + "(is the key encrypted, or does it belong to another certificate?).",
                cause);
        }
    }
}
#endif
