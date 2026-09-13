using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Ondewo.Nlu.Client.Auth;
using Xunit;

namespace Ondewo.Nlu.Client.Tests
{
    /// <summary>
    /// Covers <see cref="OndewoAuth"/>, the only hand-written source in this package. `make test`
    /// gates on 100% line and branch coverage of it, generated stubs excluded, so every branch
    /// below has to stay exercised.
    /// </summary>
    public class OndewoAuthTests
    {
        private const string Token = "a-very-secret-token";
        private const string ExpectedHeaderValue = "Bearer a-very-secret-token";

        [Fact]
        public void CreateBearerMetadataCarriesExactlyTheAuthorizationEntry()
        {
            Metadata metadata = OndewoAuth.CreateBearerMetadata(Token);

            Metadata.Entry entry = Assert.Single(metadata);
            Assert.Equal(OndewoAuth.AuthorizationHeader, entry.Key);
            Assert.Equal(ExpectedHeaderValue, entry.Value);
        }

        [Fact]
        public void CreateBearerMetadataUsesALowercaseKeyAsGrpcRequires()
        {
            Metadata metadata = OndewoAuth.CreateBearerMetadata(Token);

            Assert.All(metadata, entry => Assert.Equal(entry.Key.ToLowerInvariant(), entry.Key));
            Assert.StartsWith(OndewoAuth.BearerPrefix, metadata.Single().Value, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CreateBearerInterceptorAppendsTheTokenToCallMetadata()
        {
            AsyncAuthInterceptor interceptor = OndewoAuth.CreateBearerInterceptor(Token);
            var metadata = new Metadata { { "x-caller", "test" } };

            await interceptor(new AuthInterceptorContext("https://nlu.ondewo.com", "GetUser"), metadata);

            Assert.Equal(2, metadata.Count);
            Assert.Equal(ExpectedHeaderValue, metadata.GetValue(OndewoAuth.AuthorizationHeader));
            Assert.Equal("test", metadata.GetValue("x-caller"));
        }

        [Fact]
        public async Task CreateBearerInterceptorIsReusableAcrossCalls()
        {
            AsyncAuthInterceptor interceptor = OndewoAuth.CreateBearerInterceptor(Token);
            var context = new AuthInterceptorContext("https://nlu.ondewo.com", "GetUser");
            var first = new Metadata();
            var second = new Metadata();

            await interceptor(context, first);
            await interceptor(context, second);

            Assert.Equal(ExpectedHeaderValue, first.GetValue(OndewoAuth.AuthorizationHeader));
            Assert.Equal(ExpectedHeaderValue, second.GetValue(OndewoAuth.AuthorizationHeader));
        }

        [Fact]
        public void CreateBearerCredentialsBuildsUsableCallCredentials()
        {
            CallCredentials credentials = OndewoAuth.CreateBearerCredentials(Token);

            Assert.NotNull(credentials);
            // The credentials have to compose with channel credentials, which is how they are
            // actually attached to a channel.
            Assert.NotNull(ChannelCredentials.Create(ChannelCredentials.SecureSsl, credentials));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        public void EveryFactoryRejectsAMissingToken(string token)
        {
            Assert.Throws<ArgumentException>("accessToken", () => { OndewoAuth.CreateBearerMetadata(token); });
            Assert.Throws<ArgumentException>("accessToken", () => { OndewoAuth.CreateBearerInterceptor(token); });
            Assert.Throws<ArgumentException>("accessToken", () => { OndewoAuth.CreateBearerCredentials(token); });
        }
    }
}
