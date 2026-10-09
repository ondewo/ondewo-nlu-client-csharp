#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// An in-process HTTP/2 server on an ephemeral loopback port that answers every gRPC call with
    /// UNIMPLEMENTED. A call that comes back UNIMPLEMENTED therefore reached the server, which
    /// proves the TLS handshake (if any) succeeded; <see cref="Hits"/> counts those calls and
    /// <see cref="LastClientSubject"/> records the client certificate the server saw.
    /// </summary>
    internal sealed class TlsTestServer : IAsyncDisposable
    {
        private static readonly Marshaller<byte[]> Bytes = Marshallers.Create(bytes => bytes, bytes => bytes);

        /// <summary>A method no server implements; any service would do.</summary>
        private static readonly Method<byte[], byte[]> Probe =
            new(MethodType.Unary, "ondewo.test.Probe", "Ping", Bytes, Bytes);

        private readonly WebApplication app;
        private int hits;

        private TlsTestServer(WebApplication app)
        {
            this.app = app;
        }

        internal int Port { get; private set; }

        internal int Hits => Volatile.Read(ref hits);

        internal string? LastClientSubject { get; private set; }

        internal string? LastAuthorization { get; private set; }

        /// <summary>Starts a server.</summary>
        /// <param name="address">The loopback address to listen on.</param>
        /// <param name="serverIdentity">The TLS identity, or null for plaintext HTTP/2.</param>
        /// <param name="requireClientCertificate">Mutual TLS: refuse a client without a certificate
        /// issued by <see cref="TlsTestPki.Ca"/>. Otherwise a client certificate is optional (and,
        /// if presented, still has to be issued by that CA).</param>
        internal static async Task<TlsTestServer> StartAsync(
            IPAddress address, TlsTestPki.Identity? serverIdentity, bool requireClientCertificate = false)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            X509Certificate2? serverCertificate = serverIdentity?.ToCertificate();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(address, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                if (serverCertificate != null)
                {
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = serverCertificate;
                        https.ClientCertificateMode = requireClientCertificate
                            ? ClientCertificateMode.RequireCertificate
                            : ClientCertificateMode.AllowCertificate;
                        https.ClientCertificateValidation = (certificate, _, _) => IssuedByTestCa(certificate);
                    });
                }
            }));

            WebApplication app = builder.Build();
            var server = new TlsTestServer(app);
            app.Run(server.HandleAsync);
            await app.StartAsync();
            string bound = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            server.Port = new Uri(bound).Port;
            return server;
        }

        /// <summary>Makes one unary call and returns how it failed (it always fails).</summary>
        internal static async Task<RpcException> CallAsync(GrpcChannel channel)
        {
            var options = new CallOptions(deadline: DateTime.UtcNow.AddSeconds(20));
            AsyncUnaryCall<byte[]> call = channel.CreateCallInvoker()
                .AsyncUnaryCall(Probe, null, options, Array.Empty<byte>());
            try
            {
                await call.ResponseAsync;
            }
            catch (RpcException exception)
            {
                return exception;
            }

            throw new InvalidOperationException("the probe call unexpectedly succeeded");
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private static bool IssuedByTestCa(X509Certificate2 certificate)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.CustomTrustStore.Add(TlsTestPki.Instance.Ca);
            return chain.Build(certificate);
        }

        private async Task HandleAsync(HttpContext context)
        {
            Interlocked.Increment(ref hits);
            LastClientSubject = context.Connection.ClientCertificate?.Subject;
            LastAuthorization = context.Request.Headers.Authorization.ToString();
            context.Response.ContentType = "application/grpc";
            context.Response.AppendTrailer("grpc-status", ((int)StatusCode.Unimplemented).ToString());
            context.Response.AppendTrailer("grpc-message", "reached the test server");
            await context.Response.Body.FlushAsync();
        }
    }
}
