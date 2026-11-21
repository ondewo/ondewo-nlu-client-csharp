#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ondewo.Nlu.Client.Auth;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>A controllable monotonic clock, in seconds.</summary>
    internal sealed class FakeClock
    {
        private double now;

        internal FakeClock(double start = 1000.0)
        {
            now = start;
        }

        internal double Now
        {
            get => Volatile.Read(ref now);
            set => Volatile.Write(ref now, value);
        }

        internal double Time() => Now;

        internal void Advance(double seconds) => Now += seconds;
    }

    /// <summary>One scripted answer of <see cref="FakeTransport"/>: a response or an exception.</summary>
    public sealed class Step
    {
        private Step(KeycloakTokenResponse? response, Exception? error)
        {
            Response = response;
            Error = error;
        }

        internal KeycloakTokenResponse? Response { get; }

        internal Exception? Error { get; }

        internal static Step Token(string accessToken, double expiresIn = 300, string? refreshToken = "off-1")
        {
            string refresh = refreshToken == null ? string.Empty : $", \"refresh_token\": \"{refreshToken}\"";
            return Ok($"{{\"access_token\": \"{accessToken}\", \"expires_in\": {expiresIn.ToString(System.Globalization.CultureInfo.InvariantCulture)}{refresh}}}");
        }

        internal static Step Ok(string body) => new(new KeycloakTokenResponse(200, body), null);

        internal static Step Status(int status, string? body) => new(new KeycloakTokenResponse(status, body), null);

        internal static Step Refused() =>
            Status(400, "{\"error\":\"invalid_grant\",\"error_description\":\"Offline user session not found\"}");

        internal static Step Throw(Exception error) => new(null, error);
    }

    /// <summary>
    /// A scripted token endpoint: records every form, answers from the script (repeating the last step once
    /// the script is exhausted), and can advance a clock or delay to simulate a slow endpoint.
    /// </summary>
    internal sealed class FakeTransport : IKeycloakTokenTransport
    {
        private readonly List<Step> script;
        private readonly object sync = new();
        private int index;

        internal FakeTransport(params Step[] script)
        {
            this.script = script.ToList();
        }

        internal List<Dictionary<string, string>> Forms { get; } = new();

        internal List<string> Urls { get; } = new();

        internal List<TimeSpan> Timeouts { get; } = new();

        /// <summary>Advances this clock by <see cref="AdvancePerRefresh"/> on every refresh grant.</summary>
        internal FakeClock? Clock { get; set; }

        internal double AdvancePerRefresh { get; set; }

        internal TimeSpan Delay { get; set; } = TimeSpan.Zero;

        internal Action? OnRefresh { get; set; }

        internal List<string> Grants
        {
            get
            {
                lock (sync)
                {
                    return Forms.Select(form => form["grant_type"]).ToList();
                }
            }
        }

        public async Task<KeycloakTokenResponse> PostAsync(
            string url, IReadOnlyDictionary<string, string> form, TimeSpan timeout)
        {
            Step step;
            lock (sync)
            {
                Forms.Add(form.ToDictionary(entry => entry.Key, entry => entry.Value));
                Urls.Add(url);
                Timeouts.Add(timeout);
                step = script[Math.Min(index, script.Count - 1)];
                index++;
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay);
            }

            if (form["grant_type"] == "refresh_token")
            {
                Clock?.Advance(AdvancePerRefresh);
                OnRefresh?.Invoke();
            }

            if (step.Error != null)
            {
                throw step.Error;
            }

            return step.Response!;
        }
    }

    /// <summary>
    /// A stop signal that never really sleeps: each wait is recorded, advances the clock by its length and
    /// times out, until <c>timedOutWaits</c> waits have done so; the next one reports "stopped".
    /// </summary>
    internal sealed class FakeStopSignal : IKeycloakStopSignal
    {
        private readonly FakeClock? clock;
        private readonly int timedOutWaits;

        internal FakeStopSignal(FakeClock? clock, int timedOutWaits)
        {
            this.clock = clock;
            this.timedOutWaits = timedOutWaits;
        }

        internal List<double> Waits { get; } = new();

        internal Action? DuringWait { get; set; }

        internal bool ThrowOnSet { get; set; }

        internal bool ThrowOnWait { get; set; }

        public bool IsSet { get; private set; }

        public void Set()
        {
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("a broken stop signal");
            }

            IsSet = true;
        }

        public Task<bool> WaitAsync(double seconds)
        {
            if (ThrowOnWait)
            {
                throw new InvalidOperationException("a broken wait");
            }

            if (Waits.Count >= timedOutWaits)
            {
                IsSet = true;
                return Task.FromResult(true);
            }

            Waits.Add(seconds);
            clock?.Advance(seconds);
            DuringWait?.Invoke();
            return Task.FromResult(IsSet);
        }
    }

    /// <summary>An <see cref="ILogger"/> recording every formatted line with its level.</summary>
    internal sealed class CapturingLogger : ILogger
    {
        internal ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

        internal IEnumerable<string> At(LogLevel level) => Lines.Where(line => line.Level == level).Select(line => line.Message);

        internal string All => string.Join("\n", Lines.Select(line => line.Message));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>An in-process Keycloak token endpoint over HTTP/1.1 (optionally TLS), answering from a script.</summary>
    internal sealed class FakeKeycloakServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly Func<int, (int Status, string Body)> answer;
        private int requests;

        private FakeKeycloakServer(WebApplication app, Func<int, (int Status, string Body)> answer)
        {
            this.app = app;
            this.answer = answer;
        }

        internal int Port { get; private set; }

        internal ConcurrentQueue<(string Path, Dictionary<string, string> Form)> Requests { get; } = new();

        internal TimeSpan Delay { get; set; } = TimeSpan.Zero;

        internal string BaseUrl(string scheme = "http") => $"{scheme}://127.0.0.1:{Port}/auth/";

        internal static async Task<FakeKeycloakServer> StartAsync(
            Func<int, (int Status, string Body)> answer, TlsTestPki.Identity? identity = null)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            X509Certificate2? certificate = identity?.ToCertificate();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                if (certificate != null)
                {
                    listen.UseHttps(certificate);
                }
            }));
            WebApplication app = builder.Build();
            var server = new FakeKeycloakServer(app, answer);
            app.Run(server.HandleAsync);
            await app.StartAsync();
            string bound = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            server.Port = new Uri(bound).Port;
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private async Task HandleAsync(HttpContext context)
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            Requests.Enqueue((context.Request.Path.Value!, form.ToDictionary(entry => entry.Key, entry => entry.Value.ToString())));
            int number = Interlocked.Increment(ref requests);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, context.RequestAborted);
            }

            (int status, string body) = answer(number);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
        }
    }

    internal static class KeycloakTestSupport
    {
        internal const string Url = "https://keycloak.example/auth";
        internal const string Realm = "ondewo-ccai-platform";
        internal const string ClientId = "ondewo-nlu-cai-sdk-public";
        internal const string Username = "tech@ondewo.com";
        internal const string Password = "pw-never-logged-5d1c";

        /// <summary>Production timings unless overridden.</summary>
        internal static KeycloakTokenProvider Provider(
            FakeTransport transport,
            FakeClock clock,
            double jitter = 1.0,
            int? tokenExpirationInS = null,
            string refreshToken = "",
            string password = Password,
            ILogger? logger = null,
            IKeycloakStopSignal? stopSignal = null,
            bool startBackgroundRefresh = false,
            KeycloakTimings? timings = null)
        {
            return new KeycloakTokenProvider(
                Url, Realm, ClientId, Username, password, tokenExpirationInS, true, transport, clock.Time, stopSignal,
                startBackgroundRefresh, () => jitter, logger, refreshToken, timings ?? KeycloakTimings.Default);
        }

        /// <summary>Runs the background loop body against a fake stop signal.</summary>
        internal static Task RunLoop(KeycloakTokenProvider provider, FakeStopSignal stop, FakeClock clock)
        {
            return KeycloakTokenProvider.RunRefreshLoopAsync(
                new WeakReference<KeycloakTokenProvider>(provider), stop, clock.Time);
        }

        /// <summary>
        /// Builds a provider that nothing references once this returns, and hands back only a weak reference.
        /// Not inlined so the JIT cannot extend the provider's lifetime into the caller.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WeakReference<KeycloakTokenProvider> Abandoned(
            FakeTransport transport, FakeClock clock, IKeycloakStopSignal? stopSignal = null)
        {
            return new WeakReference<KeycloakTokenProvider>(Provider(transport, clock, stopSignal: stopSignal));
        }

        internal static void CollectGarbage()
        {
            for (int round = 0; round < 3; round++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
