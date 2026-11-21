#if NET
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Ondewo.Nlu.Client.Connection;

namespace Ondewo.Nlu.Client.Auth
{
    /// <summary>
    /// One <see cref="KeycloakTokenProvider"/> per credential set, shared by every channel and service client
    /// built from it, so the login runs once. Providers are held WEAKLY: one is collected (and its background
    /// refresh ends) once no channel uses it any more. A closed provider is never handed out again.
    /// <para>
    /// The key is the SHA-256 of the credential fields, never an object identity (a reused address would hand
    /// a new client another user's provider) and never the raw values (the key holds no secret). The refresh
    /// token takes part: two token-only configs carry the same empty password and would otherwise collapse
    /// onto one provider - one tenant authenticating as another.
    /// </para>
    /// </summary>
    public sealed class KeycloakTokenProviderRegistry
    {
        private readonly object sync = new();
        private readonly Dictionary<string, WeakReference<KeycloakTokenProvider>> providers = new();
        private readonly Func<OndewoClientConfig, ILogger?, KeycloakTokenProvider> factory;

        /// <summary>Creates an empty registry that builds real providers.</summary>
        public KeycloakTokenProviderRegistry()
            : this(CreateProvider)
        {
        }

        internal KeycloakTokenProviderRegistry(Func<OndewoClientConfig, ILogger?, KeycloakTokenProvider> factory)
        {
            this.factory = factory;
        }

        /// <summary>The process-wide registry <see cref="OndewoChannelFactory.Create"/> uses.</summary>
        public static KeycloakTokenProviderRegistry Shared { get; } = new();

        /// <summary>
        /// The shared provider for <paramref name="config"/>'s credentials, created (and logged in) on first
        /// use. Creation is serialised, so concurrent first callers cause one login.
        /// </summary>
        /// <param name="config">A config with Keycloak configured.</param>
        /// <param name="logger">Logger of a provider created by this call; ignored when one exists.</param>
        /// <returns>The provider.</returns>
        /// <exception cref="ArgumentException">Keycloak is not configured in <paramref name="config"/>.</exception>
        /// <exception cref="KeycloakAuthenticationException">The initial token acquisition failed.</exception>
        public KeycloakTokenProvider GetOrCreate(OndewoClientConfig config, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (!config.KeycloakConfigured)
            {
                throw new ArgumentException(
                    $"{nameof(OndewoClientConfig)} for {config.HostAndPort} has no Keycloak configuration "
                    + $"({nameof(config.KeycloakUrl)}, {nameof(config.Realm)}, {nameof(config.ClientId)}).",
                    nameof(config));
            }

            string key = ComputeKey(config);
            lock (sync)
            {
                if (providers.TryGetValue(key, out WeakReference<KeycloakTokenProvider>? reference)
                    && reference.TryGetTarget(out KeycloakTokenProvider? existing)
                    && !existing.IsClosed)
                {
                    return existing;
                }

                KeycloakTokenProvider provider = factory(config, logger);
                foreach (string dead in providers.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToList())
                {
                    providers.Remove(dead);
                }

                providers[key] = new WeakReference<KeycloakTokenProvider>(provider);
                return provider;
            }
        }

        /// <summary>
        /// The registry key: SHA-256 hex of the credential fields joined with <c>"\0"</c>, in the order
        /// keycloak url, realm, client id, username, password, token expiration (empty when absent), verify
        /// ssl (<c>True</c>/<c>False</c>), refresh token - identical in every ONDEWO SDK.
        /// </summary>
        /// <param name="config">The config.</param>
        /// <returns>64 lowercase hex characters.</returns>
        public static string ComputeKey(OndewoClientConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            string joined = string.Join(
                "\0",
                config.KeycloakUrl,
                config.Realm,
                config.ClientId,
                config.Username,
                config.Password,
                config.TokenExpirationInS?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                config.VerifySsl ? "True" : "False",
                config.RefreshToken);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
        }

        internal int Count
        {
            get
            {
                lock (sync)
                {
                    return providers.Count;
                }
            }
        }

        private static KeycloakTokenProvider CreateProvider(OndewoClientConfig config, ILogger? logger)
        {
            return new KeycloakTokenProvider(
                config.KeycloakUrl,
                config.Realm,
                config.ClientId,
                config.Username,
                config.Password,
                config.TokenExpirationInS,
                config.VerifySsl,
                logger: logger,
                refreshToken: config.RefreshToken);
        }
    }
}
#endif
