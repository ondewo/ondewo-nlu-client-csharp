# Release History

*****************

## Release ONDEWO NLU Csharp Client 7.3.1

### New Features

* TLS and mutual TLS: `OndewoClientConfig` and `OndewoChannelFactory.Create` (namespace
  `Ondewo.Nlu.Client.Connection`) build the `GrpcChannel` the generated service clients take, with the rules of the
  ONDEWO Python client. **They exist only in the new `net10.0` target of the package**; the `netstandard2.0`
  target (.NET Framework, older .NET) does not contain them, because `SocketsHttpHandler`,
  `SslClientAuthenticationOptions` and `X509Certificate2.CreateFromPem` do not exist there.
  * `grpcCert`, `grpcClientCert` and `grpcClientKey` hold PEM content, not file paths; a certificate field
    without a PEM certificate in it is refused with `ArgumentException`.
  * With `grpcCert` set only those CAs are trusted, otherwise the platform trust store; the host is always
    checked against the certificate's SAN.
  * `grpcClientCert` / `grpcClientKey` are both-or-neither (refused in the constructor); empty strings on both
    mean plain TLS. The key must be an unencrypted PEM key (PKCS#8, PKCS#1 or SEC1).
  * `useSecureChannel: false` with a client identity is refused; a plaintext channel logs a warning naming
    `host:port` through `GrpcChannelOptions.LoggerFactory`.
  * No exception message renders a PEM or the key; `OndewoClientConfig.ToString()` redacts the key.
  * Bare IPv6 literal hosts are bracketed; CRLF PEMs work.
  * Channel defaults: keepalive ping every 30 s with a 20 s ping timeout, only while calls are active; 5 s maximum
    reconnect backoff; 2^31-1 maximum message size. Not configured (documented in the README): a per-method retry
    policy, `max_pings_without_data` and `TCP_USER_TIMEOUT`, which `Grpc.Net.Client` does not expose.

### Improvements

* The package now targets `netstandard2.0` and `net10.0`. The `netstandard2.0` target is unchanged: the
  generated stubs and `OndewoAuth`.
* README: new section "TLS, mutual TLS and certificates" (modes, rules, example, channel defaults, a test PKI
  with openssl, security notes, troubleshooting).
* Tests: real TLS and mutual TLS handshakes against an in-process Kestrel server with a PKI built at test time
  (custom CA, client chain, CRLF, IPv6, missing / unrelated client identity, wrong CA, wrong host name, system
  trust store, plaintext), and a test that pins the release-notes slice of `RELEASE.md` the GitHub release is
  cut from.
* Tracking API Version [7.3.0](https://github.com/ondewo/ondewo-nlu-api/releases/tag/7.3.0) ( [Documentation](https://ondewo.github.io/ondewo-nlu-api/) )

*****************

## Release ONDEWO NLU Csharp Client 7.3.0

### Improvements

* Tracking API Version [7.3.0](https://github.com/ondewo/ondewo-nlu-api/releases/tag/7.3.0) ( [Documentation](https://ondewo.github.io/ondewo-nlu-api/) )

*****************

## Release ONDEWO NLU Csharp Client 7.2.0

### Improvements

* Tracking API Version [7.2.0](https://github.com/ondewo/ondewo-nlu-api/releases/tag/7.2.0) ( [Documentation](https://ondewo.github.io/ondewo-nlu-api/) )

*****************

## Release ONDEWO NLU Csharp Client 7.1.0

### New Features

* Initial release of the ONDEWO NLU C# client. The package `Ondewo.NLU.Client` ships the
  gRPC message and client stubs generated from the [ONDEWO NLU API](https://github.com/ondewo/ondewo-nlu-api)
  by the `ondewo-csharp-proto-compiler` image of
  [ondewo-proto-compiler 5.15.1](https://github.com/ondewo/ondewo-proto-compiler): one
  `<Message>.cs` per proto plus one `<Service>Grpc.cs` per proto that declares a service, nested by C# namespace
  under `api/`.
* The package targets `netstandard2.0`, so it is consumable from .NET Framework 4.6.1+, .NET Core 2.0+ and every
  modern .NET, and carries `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Core.Api` and `Google.Api.CommonProtos` as
  its only dependencies. The `google/api`, `google/rpc` and `google/type` descriptors the API imports come from
  `Google.Api.CommonProtos` rather than being generated a second time into this assembly.
* `make build` is the whole pipeline — submodule checkout at the pinned versions, compiler image build, stub
  generation and `dotnet pack` — and `make ondewo_release` cuts the GitHub and NuGet release from the same
  version number.

*****************
