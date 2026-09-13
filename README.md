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
dotnet add package Ondewo.NLU.Client --version 7.1.0
```

Or write the `PackageReference` item into your `.csproj` directly:

```xml
<ItemGroup>
  <PackageReference Include="Ondewo.NLU.Client" Version="7.1.0" />
</ItemGroup>
```

In the Visual Studio Package Manager Console:

```powershell
Install-Package Ondewo.NLU.Client -Version 7.1.0
```

A few things worth knowing before you take the dependency:

- **Target framework.** The package is built for `netstandard2.0`, so it is consumable from .NET
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
- **Versioning.** `Ondewo.NLU.Client` **7.1.x** is generated from ONDEWO NLU API **7.1.0**: major
  and minor always match the API, the patch number is this client's own.

From source:

```shell
git clone https://github.com/ondewo/ondewo-nlu-client-csharp.git   ## Clone the repository
cd ondewo-nlu-client-csharp                                        ## Change into the repo directory
make setup_developer_environment_locally              ## Check out the submodules, install the hooks
make build                                            ## Regenerate the stubs and pack the NuGet package
```

The package targets `netstandard2.0`, so it is consumable from .NET Framework 4.6.1+, .NET Core 2.0+ and every
modern .NET. It brings `Google.Protobuf`, `Grpc.Net.Client`, `Grpc.Core.Api` and `Google.Api.CommonProtos` with
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

## Repository structure

```
.
├── api                                      <----- generated stubs, nested by C# namespace
│   └── Ondewo
│       └── ...
├── auth                                     <----- the only hand-written sources in the package
├── tests                                    <----- xunit suite over the committed stubs
├── artifacts                                <----- compiled assembly, symbols, XML docs (not tracked)
├── coverage                                 <----- cobertura/lcov written by `make test` (not tracked)
├── nupkg                                    <----- the packed .nupkg / .snupkg (not tracked)
├── ondewo-nlu-api                               <----- submodule: the .proto sources
├── ondewo-proto-compiler                    <----- submodule: the code generator
├── Ondewo.NLU.Client.csproj     <----- generated project file (tracked)
├── Directory.Build.props                    <----- fallback MSBuild pins for a submodule-free build
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

- The image runs as **root** (it writes into a root-owned directory inside the container), so the files it copies
  out are owned by root. `generate_ondewo_protos` calls `fix_generated_file_ownership` right afterwards, which
  `sudo chown`s exactly the four paths the image owns — you will be prompted for your password.
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

None of these needs docker, the submodules or the network beyond NuGet — they work on a plain clone, which is
exactly what CI runs: `.github/workflows/ci.yml` builds and tests the **committed** stubs and never builds the
compiler image.

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
produces. A clone without that submodule — CI included — has no Dockerfile to read, so `Directory.Build.props`
carries a committed fallback for each pin; it declares them only when they are still empty, so the `Makefile`
always wins, and `make check_dotnet_properties` fails the build if the two ever disagree. Update both together.

### Adding hand-written code

Anything you put in the repository is copied into the image's internal compile directory and picked up by the
SDK's default `Compile` glob, so a hand-written `auth/Something.cs` ships inside the package with no barrel file
to maintain — in C# the assembly *is* the barrel. `auth/OndewoAuth.cs` is the one example in the tree.

The same glob is why `Ondewo.NLU.Client.csproj` carries

```xml
<Compile Remove="tests/**" />
```

— without it the test sources, and `tests/**/obj/*AssemblyInfo.cs`, are compiled into the library itself.

Hand-written code is what the coverage gate measures, so anything added here needs tests: `make test` fails below
100% line, branch and method coverage of everything outside `api/`.

## Releasing

Bump `ONDEWO_NLU_VERSION` in the `Makefile`, add a `RELEASE.md` entry in the existing format under a
`## Release ONDEWO NLU Csharp Client <version>` heading — `build_gh_release` slices the release notes out by
grepping for exactly that line — then:

```shell
make ondewo_release
```

That checks the release branch and tag do not exist yet (`spc`), pulls the credentials from the
`ondewo-devops-accounts` repository, rebuilds everything, tags it, publishes the GitHub release and publishes the
package to NuGet. `GITHUB_GH_TOKEN` and `NUGET_API_KEY` are read at runtime only and must never be committed.

### The NuGet half

| Target              | What it does                                                                       |
| ------------------- | ---------------------------------------------------------------------------------- |
| `pack`              | `dotnet pack` into `nupkg/` — the `.nupkg` and the `.snupkg` symbol package          |
| `verify_nupkg_metadata` | reads the nuspec back out of the packed `.nupkg` and fails on missing metadata  |
| `verify_nupkg_installs` | restores the packed `.nupkg` from a local folder feed into a throwaway consumer  |
| `publish_dry_run`   | the three above — **needs no credential**, and is what `ci.yml` runs on every push   |
| `push_to_nuget`     | `dotnet nuget push` to `NUGET_SOURCE` — the only step that needs `NUGET_API_KEY`     |
| `publish`           | `publish_dry_run` then `push_to_nuget`; this is what `release` calls                 |

`NUGET_API_KEY` comes from `ondewo-devops-accounts/account_nuget.env` (read by `run_release_with_devops`) and
must be an API key scoped to **Push** for the glob pattern `Ondewo.*`. The recipe that carries it is
`@`-prefixed and hands the key to `dotnet` through the environment, so it never reaches a build log. Pushing the
`.nupkg` uploads the `.snupkg` beside it automatically.

Pushing a version tag (`7.1.0`, `7.1.0-rc.1` — the shape `make create_release_tag` writes) also triggers
`.github/workflows/release.yml`, which rebuilds the committed stubs, re-runs the test and dry-run gates and
publishes with the `NUGET_API_KEY` **repository secret**. Without that secret the run stops on its very first
step with an explicit error rather than quietly publishing nothing.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Commits follow
[Conventional Commits](https://www.conventionalcommits.org/); the `giticket` hook prepends the ticket id from the
branch name, so never write it yourself.

## License

[Apache 2.0](LICENSE)
