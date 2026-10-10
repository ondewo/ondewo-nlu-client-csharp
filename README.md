<div align="center">
  <table>
    <tr>
      <td>
        <a href="https://ondewo.com">
            <img width="400px" src="https://raw.githubusercontent.com/ondewo/ondewo-logos/master/ondewo_we_automate_your_phone_calls.png"/>
        </a>
      </td>
    </tr>
    <tr>
       <td align="center">
          <a href="https://www.linkedin.com/company/ondewo "><img width="40px" src="https://cdn-icons-png.flaticon.com/512/3536/3536505.png"></a>
          <a href="https://www.facebook.com/ondewo"><img width="40px" src="https://cdn-icons-png.flaticon.com/512/733/733547.png"></a>
          <a href="https://twitter.com/ondewo"><img width="40px" src="https://cdn-icons-png.flaticon.com/512/733/733579.png"> </a>
          <a href="https://www.instagram.com/ondewo.ai/"><img width="40px" src="https://cdn-icons-png.flaticon.com/512/174/174855.png"></a>
       </td>
    </tr>
  </table>
  <h1 align="center">
    ONDEWO NLU Client C-Sharp
  </h1>
</div>

## Overview

`Ondewo.NLU.Client` is a compiled version of the
[ONDEWO NLU API](https://github.com/ondewo/ondewo-nlu-api) — the gRPC interface to ONDEWO
Natural Language Understanding — generated with the
[ONDEWO PROTO COMPILER](https://github.com/ondewo/ondewo-proto-compiler).

ONDEWO APIs use [Protocol Buffers](https://github.com/protocolbuffers/protobuf) version 3 (proto3) as their
Interface Definition Language (IDL) to define the API interface and the structure of the payload messages. The same
interface definition is used for the gRPC versions of the API in all languages.

Nothing in `api/` is written by hand: it is the output of the `ondewo-csharp-proto-compiler` docker image running
over the `ondewo-nlu-api` submodule, and it is regenerated in full by `make build`.

## Installation

The package is published to [nuget.org](https://www.nuget.org/packages/Ondewo.NLU.Client) as
**`Ondewo.NLU.Client`**. No custom feed, credential or `nuget.config` entry is needed — the default
`nuget.org` source is enough.

With the .NET CLI, from the directory of the project that should consume it:

```shell
dotnet add package Ondewo.NLU.Client
```

That resolves the latest stable version. To pin one — which is what you want in a service, because
the client version tracks the ONDEWO NLU API in major and minor:

```shell
dotnet add package Ondewo.NLU.Client --version 7.3.1
```

Or write the `PackageReference` item into your `.csproj` directly:

```xml
<ItemGroup>
  <PackageReference Include="Ondewo.NLU.Client" Version="7.3.1" />
</ItemGroup>
```

In the Visual Studio Package Manager Console:

```powershell
Install-Package Ondewo.NLU.Client -Version 7.3.1
```

A few things worth knowing before you take the dependency:

- **Target framework.** The package is built for `netstandard2.0` and `net10.0`. The `netstandard2.0` build is consumable from .NET
  Framework 4.6.1+, .NET Core 2.0+ and every modern .NET. Note that gRPC over
  `Grpc.Net.Client` needs HTTP/2, which in practice means .NET Core 3.0+ / .NET 5+; on .NET
  Framework you additionally need `Grpc.Net.Client.Web` or the legacy `Grpc.Core` channel.
- **Transitive dependencies.** `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Core.Api` and
  `Google.Api.CommonProtos` come with it, at the versions pinned by the compiler image. The
  `google/api`, `google/rpc` and `google/type` descriptors the ONDEWO API imports are taken from
  `Google.Api.CommonProtos` rather than generated a second time into this assembly, so they never
  collide with another Google library in your graph.
- **Debugging.** Every release also publishes a `.snupkg` symbol package to the nuget.org symbol
  server, so stepping into the generated stubs works once `https://symbols.nuget.org/download/symbols`
  is enabled in your debugger's symbol settings.
- **Versioning.** Major and minor of `Ondewo.NLU.Client` always match the ONDEWO NLU API it is
  generated from; the patch number is this client's own.

From source:

```shell
git clone https://github.com/ondewo/ondewo-nlu-client-csharp.git   ## Clone the repository
cd ondewo-nlu-client-csharp                                        ## Change into the repo directory
make setup_developer_environment_locally              ## Check out the submodules, install the hooks
make build                                            ## Regenerate the stubs and pack the NuGet package
```

The package targets `netstandard2.0` and `net10.0`: the `netstandard2.0` build is consumable from .NET Framework
4.6.1+, .NET Core 2.0+ and every modern .NET, and only the `net10.0` build carries the TLS / mutual-TLS channel
factory (see [TLS, mutual TLS and certificates](#tls-mutual-tls-and-certificates)). It brings `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Core.Api` and `Google.Api.CommonProtos` with
it — the versions are pinned by the compiler image, never by hand.

## Usage

```csharp
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;

// protoc derives the C# namespace from the proto `package` declaration and PascalCases it, so
// `package ondewo.nlu;` becomes `Ondewo.<Pascal>` — check the `namespace` line at the top of
// any generated file under api/ for the exact spelling.
using Ondewo.NLU;

// A bearer token is attached to every call through CallCredentials, so it is refreshed in one
// place instead of being copied into each request's Metadata.
var callCredentials = CallCredentials.FromInterceptor((context, metadata) =>
{
    metadata.Add("authorization", $"Bearer {accessToken}");
    return Task.CompletedTask;
});

using var channel = GrpcChannel.ForAddress(
    "https://grpc-nlu.ondewo.com:443",
    new GrpcChannelOptions
    {
        Credentials = ChannelCredentials.Create(ChannelCredentials.SecureSsl, callCredentials),
    });

// Every service declared in the .proto files has a generated `<Service>.<Service>Client` type.
var client = new SomeService.SomeServiceClient(channel);
var response = await client.SomeRpcAsync(new SomeRequest());
```

## TLS, mutual TLS and certificates

gRPC encrypts with **TLS**. `OndewoChannelFactory.Create` (namespace `Ondewo.Nlu.Client.Connection`) builds the
`GrpcChannel` every generated `<Service>.<Service>Client` takes from an `OndewoClientConfig`, with the same rules
as the ONDEWO Python client. It is part of the package's **`net10.0`** build; a `netstandard2.0` consumer (.NET
Framework, older .NET) does not get it, because the APIs it needs (`SocketsHttpHandler`,
`SslClientAuthenticationOptions`, `X509Certificate2.CreateFromPem`) do not exist there.

| Mode                                | `useSecureChannel` | Config fields                                                       |
|-------------------------------------|--------------------|---------------------------------------------------------------------|
| Plaintext (not for production)      | `false`            | none - a client identity is refused                                 |
| TLS, platform trust store           | `true` (default)   | none                                                                |
| TLS, custom CA                      | `true` (default)   | `grpcCert` = PEM of the CA that signed the server certificate       |
| Mutual TLS                          | `true` (default)   | `grpcCert` (or the platform trust store) plus `grpcClientCert` and `grpcClientKey` |

Rules the code enforces:

- The three certificate fields hold **PEM content**, **not file paths**. Read the files yourself
  (`File.ReadAllText`). A `grpcCert` or `grpcClientCert` without any PEM certificate in it - typically a path - is
  refused with an `ArgumentException` that says so.
- `grpcClientCert` and `grpcClientKey` go together: setting only one throws `ArgumentException` when the
  `OndewoClientConfig` is built. Empty strings on both mean no client identity: plain server-authenticated TLS.
- `useSecureChannel: false` with a client identity throws `ArgumentException` instead of silently dropping the
  identity. A plaintext channel is allowed otherwise, and logs a warning naming `host:port` through the
  `ILoggerFactory` you set on `GrpcChannelOptions.LoggerFactory` (nothing is logged without one; the SDK never
  configures logging itself).
- `grpcClientKey` must be an **unencrypted** PEM key (PKCS#8, PKCS#1 or SEC1). An encrypted key, or one that
  belongs to another certificate, is refused with `ArgumentException`. Certificates after the first one in
  `grpcClientCert` are sent along as intermediates.
- With `grpcCert` set, the server certificate must chain to one of the certificates in it - the platform trust
  store is not consulted, and revocation is not checked (as in grpc-core). The host you connect to must match one
  of the certificate's subject alternative names (SAN); there is no name override, so connect by a name in the SAN.
- A bare IPv6 literal host is bracketed (`::1` becomes `https://[::1]:50055`); CRLF line endings in PEMs work.
- No exception message renders a PEM, a key or the config; `OndewoClientConfig.ToString()` prints the key as
  `***REDACTED***` (empty when it is empty) and the certificates only by their length.

```csharp
using System.IO;
using Grpc.Core;
using Grpc.Net.Client;
using Ondewo.Nlu.Client.Auth;
using Ondewo.Nlu.Client.Connection;

var config = new OndewoClientConfig(
    host: "ondewo.example.com",
    port: 50055,
    grpcCert: File.ReadAllText("certs/ca.pem"),
    grpcClientCert: File.ReadAllText("certs/client.pem"),  // leave both out for server-authenticated TLS
    grpcClientKey: File.ReadAllText("certs/client.key"));

// One channel for every service client of this server; dispose it when they are all done.
using GrpcChannel channel = OndewoChannelFactory.Create(config, options =>
{
    // Optional: a bearer token on every call, and a logger for gRPC and this SDK.
    options.Credentials = ChannelCredentials.Create(
        ChannelCredentials.SecureSsl, OndewoAuth.CreateBearerCredentials(accessToken));
    options.LoggerFactory = loggerFactory;
});
var client = new SomeService.SomeServiceClient(channel);
```

The `configure` callback runs after the defaults below are applied, so it can change any of them. Replacing
`HttpHandler` or `HttpClient` there discards the TLS setup; `OndewoChannelFactory.CreateHttpHandler(config)` returns
the configured `SocketsHttpHandler` if you build your own `GrpcChannelOptions`.

### Channel defaults

The defaults of the Python client, mapped to what `Grpc.Net.Client` exposes:

| Python channel option                                | .NET                                                         |
|------------------------------------------------------|--------------------------------------------------------------|
| `grpc.keepalive_time_ms=30000`                       | `SocketsHttpHandler.KeepAlivePingDelay` = 30 s               |
| `grpc.http2.ping_timeout_ms=20000`                   | `SocketsHttpHandler.KeepAlivePingTimeout` = 20 s             |
| `grpc.keepalive_permit_without_calls=0`              | `KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests` |
| `grpc.http2.max_pings_without_data=2`                | not exposed by .NET: pings are sent only while calls are active |
| `grpc.keepalive_timeout_ms=20000` (`TCP_USER_TIMEOUT`) | not exposed by .NET; the 20 s ping timeout detects a dead connection |
| `grpc.max_reconnect_backoff_ms=5000`                 | `GrpcChannelOptions.MaxReconnectBackoff` = 5 s               |
| max send / receive message length 2^31-1             | `MaxSendMessageSize` / `MaxReceiveMessageSize` = `int.MaxValue` |
| per-method retry policy (idempotent methods only)    | not configured: `Grpc.Net.Client` retries nothing without a service config |

`EnableMultipleHttp2Connections` is on and pooled connections never idle out, as the `Grpc.Net.Client` docs
recommend for long-lived channels.

### A test PKI with openssl

A CA, a server certificate with SANs, and a client certificate with the `clientAuth` extended key usage. For tests
only: the keys are unencrypted.

```bash
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 365 \
  -subj "/CN=Test CA" -keyout ca.key -out ca.pem

printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n' > server.ext
openssl req -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
  -subj "/CN=localhost" -keyout server.key -out server.csr
openssl x509 -req -in server.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 365 \
  -extfile server.ext -out server.pem

printf 'extendedKeyUsage=clientAuth\n' > client.ext
openssl req -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
  -subj "/CN=my-client" -keyout client.key -out client.csr
openssl x509 -req -in client.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 365 \
  -extfile client.ext -out client.pem

chmod 600 *.key
openssl verify -CAfile ca.pem server.pem client.pem
```

The client then uses `ca.pem` / `client.pem` / `client.key`; a server that requires client certificates uses
`server.pem` / `server.key` and trusts `ca.pem` for its clients. An encrypted key is decrypted with
`openssl pkey -in encrypted.key -out client.key`.

The test suite builds the same kind of PKI in code at test time (`tests/*/TlsTestPki.cs`) and runs real
handshakes against an in-process Kestrel server, so no private key is committed.

### TLS security notes

- `OndewoClientConfig` holds `grpcClientKey` as a plain managed string for the lifetime of the config, and .NET
  cannot wipe it from memory. Load it from a file (mode `0600`, never committed) or a secret store at startup, and
  keep the config's lifetime short if that matters to you.
- `ToString()` redacts the key, but do not log the PEM strings you pass in, or the files they came from.
- This SDK has no serialization of the config, so nothing it does writes the key anywhere.

### TLS troubleshooting

A failed handshake is an `RpcException`; `Grpc.Net.Client` reports a certificate the client refused as
`StatusCode.Internal` (grpc-core and the Python client say `UNAVAILABLE`) and a connection the server dropped as
`StatusCode.Unavailable`. The cause is in `exception.Status.DebugException` and its inner exceptions:

- **`The remote certificate is invalid because of errors in the certificate chain: PartialChain`** (or
  `UntrustedRoot`): `grpcCert` is not the CA that issued the server certificate, or `grpcCert` is empty and the
  server uses a private CA, or the server does not send its intermediate certificates.
- **`The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch`**:
  the host you connect to is not in the server certificate's SAN. Connect by a name in the SAN, or add the SAN.
- **`An HTTP/2 connection could not be established because the server did not complete the HTTP/2 handshake`**
  against a server that requires client certificates: no client certificate was presented, or one the server's CA
  did not issue. The server log names the reason. Set `grpcClientCert` / `grpcClientKey`.
- **`ArgumentException: ... contains no PEM certificate ... not a file path`**: a certificate field holds a path or
  other non-PEM text. Pass `File.ReadAllText(path)`.
- **`ArgumentException: ... could not be loaded as a PEM certificate and its unencrypted private key`**: the key is
  encrypted, malformed, or belongs to another certificate.

## Repository structure

```
.
├── api                                      <----- generated stubs, nested by C# namespace
│   └── Ondewo
│       └── ...
├── auth                                     <----- hand-written: bearer-token helpers
├── connection                               <----- hand-written: TLS / mutual-TLS channel factory (net10.0)
├── tests                                    <----- xunit suite over the committed stubs
├── artifacts                                <----- compiled assembly, symbols, XML docs (not tracked)
├── coverage                                 <----- cobertura/lcov written by `make test` (not tracked)
├── nupkg                                    <----- the packed .nupkg / .snupkg (not tracked)
├── ondewo-nlu-api                               <----- submodule: the .proto sources
├── ondewo-proto-compiler                    <----- submodule: the code generator
├── Ondewo.NLU.Client.csproj     <----- generated project file (tracked)
├── Directory.Build.props                    <----- fallback MSBuild pins for a submodule-free build
├── Dockerfile.utils                         <----- the .NET SDK + gh image every dotnet/gh call of a release runs in
├── Makefile                                 <----- every documented entry point, see `make help`
├── README.md
└── RELEASE.md
```

## Regenerating the stubs

```shell
make build
```

`make build` is the whole pipeline, and each step is also a documented target of its own:

| Target                                | What it does                                                                     |
| ------------------------------------- | -------------------------------------------------------------------------------- |
| `clean`                               | removes `api/`, `artifacts/`, `nupkg/`, `bin/`, `obj/`                             |
| `update_submodules`                   | `git submodule update --init --recursive`                                          |
| `checkout_defined_submodule_versions`  | checks out the pins at the top of the `Makefile`                                   |
| `build_compiler`                      | builds `ondewo-csharp-proto-compiler:latest` from the pinned submodule              |
| `generate_ondewo_protos`              | runs that image over `ondewo-nlu-api/ondewo` and writes the library back into the repo  |
| `check_build`                         | fails when a `.proto` produced no `.cs` stub                                        |

Run `make help` for the full list, and `make TEST` to print the resolved versions before you build anything.

Two details are worth knowing:

- The image runs as **you** (`docker run --user`), with its compile directory and dotnet's home moved to `/tmp`
  inside the container, so everything it writes back into the repository is owned by you — no `sudo`, no `chown`.
- The generated `Ondewo.NLU.Client.csproj` is **tracked**. On the next run the image finds it in the
  input volume and uses it instead of its own default, which is what lets this repository customise the package —
  so keep any edit you make to it restorable from the compiler image's pre-warmed offline NuGet feed, or the
  restore fails with `NU1101`.

## Building and testing locally

```shell
make build_library     ## dotnet build of the generated project, no docker
make test              ## build_library + the xunit suite, gated on coverage
make pack              ## dotnet pack into nupkg/ (.nupkg + .snupkg)
make publish_dry_run   ## pack + verify the package is publishable, no credential needed
```

These run the `dotnet` on your `PATH`, so they need a .NET 10 SDK. None of them needs docker, the `ondewo-nlu-api`
submodule or the network beyond NuGet — only the small `ondewo-proto-compiler` submodule, whose `Dockerfile`
`check_dotnet_properties` compares the committed pins against (`git submodule update --init ondewo-proto-compiler`).
CI runs the first two the same way: `.github/workflows/ci.yml` runs pre-commit and `make test` over the
**committed** stubs on every push and pull request, never builds the compiler image, and packs or publishes
nothing — it is a test gate, not part of the release.

Without a local SDK, run the same targets in the utils image built from `Dockerfile.utils` — docker is all you
need, and this is how `make release` runs them:

```shell
make test_via_docker_image              ## make test inside the utils image
make publish_dry_run_via_docker_image   ## make publish_dry_run inside the utils image
```

The container runs as you, with the repository mounted at its own path and every dotnet/NuGet cache kept in the
container's `/tmp`, so `bin/`, `obj/`, `coverage/` and `nupkg/` land in your working tree owned by you.

`make test` runs the suite under `tests/` and fails when coverage of the **hand-written** sources drops below
`COVERAGE_THRESHOLD` (100%). Everything under `api/` is excluded from the metric — it is machine output, and a
percentage over it measures the generator rather than this repository — but it is still exercised hard: the suite
round-trips every generated message through its wire format, checks every generated enum starts at its zero
value, and binds every generated service client to a channel, asserting it exposes every RPC its service
descriptor declares. Reports land in `coverage/` as cobertura and lcov.

The generated project file carries no literal versions: it reads `$(OndewoPackageId)`,
`$(OndewoPackageVersion)`, `$(OndewoTargetFramework)` and the three package-version properties as MSBuild
properties. The `Makefile` reads those straight back out of the pinned
`ondewo-proto-compiler/csharp/Dockerfile` and `export`s them, so a host build can never drift from what the image
produces. A plain `dotnet build`, which does not go through the `Makefile`, gets none of those exports, so
`Directory.Build.props` carries a committed fallback for each pin; it declares them only when they are still
empty, so the `Makefile` always wins, and `make check_dotnet_properties` fails the build if the two ever disagree.
Update both together.

### Adding hand-written code

Anything you put in the repository is copied into the image's internal compile directory and picked up by the
SDK's default `Compile` glob, so a hand-written `auth/Something.cs` ships inside the package with no barrel file
to maintain — in C# the assembly *is* the barrel. `auth/OndewoAuth.cs` and `connection/` are the examples in the tree.

The same glob is why `Ondewo.NLU.Client.csproj` carries

```xml
<Compile Remove="tests/**" />
```

— without it the test sources, and `tests/**/obj/*AssemblyInfo.cs`, are compiled into the library itself.

Hand-written code is what the coverage gate measures, so anything added here needs tests: `make test` fails below
100% line, branch and method coverage of everything outside `api/`.

## Releasing

A release runs entirely on the machine that runs the make target — the NuGet push and the GitHub release included.
No CI workflow packs or publishes anything, and nothing reads a credential from this repository or from a GitHub
secret: `GITHUB_GH_TOKEN` and `NUGET_API_KEY` live only in the `ondewo-devops-accounts` repository.

Bump `ONDEWO_NLU_VERSION` and the `ONDEWO_NLU_API_GIT_BRANCH` pin in the `Makefile`, add a `RELEASE.md` entry in
the existing format under a `## Release ONDEWO NLU Csharp Client <version>` heading — `build_gh_release` slices
the release notes out by grepping for exactly that line — then:

```shell
make ondewo_release
```

In this order, that:

1. checks the release branch and tag do not exist yet (`spc`);
1. clones `ondewo-devops-accounts`, reads `GITHUB_GH_TOKEN` from `account_github.env` and `NUGET_API_KEY` from
   `account_nuget.env`, and runs `make release` with exactly those two;
1. checks both are set (`check_release_credentials`) and — read-only, in the utils image — that the GitHub token
   logs in and may push to this repository (`validate_release_credentials`);
1. checks `RELEASE.md` has notes for the version, points the `README.md` install snippets at it
   (`update_readme_version`), regenerates the stubs and runs the test suite and the NuGet dry run in the utils
   image;
1. only then commits and pushes, and pushes the release branch and the tag;
1. pushes the package to nuget.org (`push_to_nuget_via_docker_image`);
1. creates the GitHub release (`release_to_github_via_docker_image`) — last, so a GitHub release only ever
   exists for a complete release.

`make release_all_clients` in `ondewo-nlu-api` does all of this for this client: it clones the repository, inserts
a generated `RELEASE.md` entry, rewrites the version and both submodule pins in the `Makefile` and runs
`make ondewo_release`.

The release machine needs only make, git, docker and perl besides the standard shell tools (grep, sed, find,
coreutils): the stubs are generated in the compiler image, and every `gh` and `dotnet` call runs in the utils image
(`make build_utils_docker_image`, from `Dockerfile.utils`).

nuget.org documents no read-only way to test a push API key, so an expired or revoked `NUGET_API_KEY` is only
noticed in step 6, after the tag is pushed — and `spc` then refuses to run the release again. Replace the key in
`ondewo-devops-accounts` and finish the release from the same checkout, which a failed release leaves in place
with its `nupkg/` (run `make publish_dry_run_via_docker_image` first if `nupkg/` is gone):

```shell
make clone_devops_accounts
make push_to_nuget_via_docker_image release_to_github_via_docker_image \
  $(grep -hE '^(GITHUB_GH_TOKEN|NUGET_API_KEY)=' ondewo-devops-accounts/account_github.env ondewo-devops-accounts/account_nuget.env)
rm -rf ondewo-devops-accounts
```

`--skip-duplicate` makes the NuGet step a no-op if the version did reach nuget.org, so the same two steps also
finish a release that stopped at the GitHub release.

### The NuGet half

| Target              | What it does                                                                       |
| ------------------- | ---------------------------------------------------------------------------------- |
| `pack`              | `dotnet pack` into `nupkg/` — the `.nupkg` and the `.snupkg` symbol package          |
| `verify_nupkg_metadata` | reads the nuspec back out of the packed `.nupkg` and fails on missing metadata  |
| `verify_nupkg_installs` | restores the packed `.nupkg` from a local folder feed into a throwaway consumer  |
| `publish_dry_run`   | the three above — **needs no credential**                                            |
| `push_to_nuget`     | `dotnet nuget push --skip-duplicate` to `NUGET_SOURCE` — the only step that needs `NUGET_API_KEY` |
| `publish_dry_run_via_docker_image` | `publish_dry_run` in the utils image; `release` runs it before anything is pushed |
| `push_to_nuget_via_docker_image`   | `push_to_nuget` in the utils image; `release` runs it right after the tag  |

`release` uploads to nuget.org only through `push_to_nuget_via_docker_image`. `NUGET_API_KEY` comes from
`ondewo-devops-accounts/account_nuget.env` (read by `run_release_with_devops`) and must be an API key scoped to
**Push** for the glob pattern `Ondewo.*`. The utils container gets it by name only (`docker run -e
NUGET_API_KEY`), and the `@`-prefixed recipe that hands it to `dotnet nuget push --api-key` never echoes it into a
build log. Pushing the `.nupkg` uploads the `.snupkg` beside it automatically.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Commits follow
[Conventional Commits](https://www.conventionalcommits.org/); the `giticket` hook prepends the ticket id from the
branch name, so never write it yourself.

## License

[Apache 2.0](LICENSE)
