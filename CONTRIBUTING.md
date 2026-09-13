# How to become a contributor and submit your own code

## Contributor License Agreements

We'd love to accept your sample apps and patches! Before we can take them, we have to jump a couple of legal
hurdles.

Please fill out either the individual or corporate Contributor License Agreement (CLA).

If you are an individual writing original source code and you're sure you own the intellectual property, then
you'll need to sign an [individual CLA](TODO:).
If you work for a company that wants to allow you to contribute your work, then you'll need to sign a
[corporate CLA](TODO:).

Follow either of the two links above to access the appropriate CLA and instructions for how to sign and return
it. Once we receive it, we'll be able to accept your pull requests.

## Contributing a patch

1. Submit an issue describing your proposed change to the repo in question.
1. The repo owner will respond to your issue promptly.
1. If your proposed change is accepted, and you haven't already done so, sign a Contributor License Agreement
   (see details above).
1. Fork the desired repo, develop and test your code changes.
1. Ensure that your code adheres to the existing style in the sample to which you are contributing. Refer to the
   [.NET coding conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions)
   for the recommended coding standards for this repository.
1. Ensure that your code has an appropriate set of unit tests which all pass.
1. Submit a pull request.

## What is generated and what is not

Almost everything in this repository is generated. `api/` and `Ondewo.NLU.Client.csproj` are the
output of the `ondewo-csharp-proto-compiler` docker image running over the `ondewo-nlu-api` submodule, and they are
overwritten in full by `make build`.

- A change to the **API surface** (a message, a field, an RPC) belongs in
  [ondewo-nlu-api](https://github.com/ondewo/ondewo-nlu-api), not here. Once it is released there, bump
  `ONDEWO_NLU_API_GIT_BRANCH` in the `Makefile` and run `make build`.
- A change to **how the stubs are generated** belongs in
  [ondewo-proto-compiler](https://github.com/ondewo/ondewo-proto-compiler). Bump
  `ONDEWO_PROTO_COMPILER_GIT_BRANCH` afterwards.
- Hand-written C# that ships in the package (helpers, authentication, convenience wrappers) belongs here, beside
  the generated code. It is compiled into the assembly automatically — see the README for the one
  `Compile Remove` line a test project needs.

Never edit a file under `api/` directly: the next `make build` deletes it.

## Before you open a pull request

```shell
make precommit_hooks_run_all_files   ## markdownlint + hygiene hooks
make build                           ## regenerate the stubs from the pinned submodules
make test                            ## compile the library and run the test suite
```

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat: …`, `fix(scope): …`,
`docs: …`). The `giticket` hook reads the ticket id from the branch name and prepends `[<ticket>]` for you, so do
not write it yourself — doing so produces a duplicated prefix.
