export

# =====================================================================================
# ondewo-nlu-client-csharp - Makefile
#
# Single entry point for regenerating the gRPC client stubs from the ondewo-nlu-api protos and
# for cutting a release. There is no hand-written application here: `make build` drives the
# ondewo-proto-compiler docker image and everything under api/ is its output.
#
# Quick start:
#   make help                  # list every documented target
#   make makefile_chapters     # list the section headers below
#   make setup_developer_environment_locally
#   make build                 # submodules -> compiler image -> stubs -> NuGet package
#   make test_via_docker_image # compile the generated library and run the test suite in docker
#   make test                  # the same with a local .NET 10 SDK, which is how CI runs it
#
# Host requirements of `make build` and `make release`: make, git, docker and perl, plus the standard
# shell tools (grep, sed, find, coreutils). The stubs are generated in the ondewo-proto-compiler image,
# and every dotnet and gh call of a release runs in the utils image built from Dockerfile.utils (see
# UTILS_DOCKER_RUN).
#
# Versioning: ONDEWO_NLU_VERSION (below) is the single source of truth. It MUST
# match the ONDEWO NLU API in major and minor version. It is handed to MSBuild as
# the $(OndewoPackageVersion) property, so no project file has to be rewritten when it changes.
# The install snippets in README.md, which is packed as the package readme, are derived from it
# by `make update_readme_version`, which `make release` runs itself.
#
# Overriding variables: pass on the command line, e.g. `make build ONDEWO_NLU_VERSION=1.2.3`,
# or export in the environment. Credentials (GITHUB_GH_TOKEN, NUGET_API_KEY) live ONLY in the
# ondewo-devops-accounts repository: `make ondewo_release` clones it and hands them to
# `make release` at runtime. They are never committed here, and no CI workflow has them - the
# whole release, publishing included, runs on the machine that runs `make ondewo_release`.
# =====================================================================================

# ---------------- BEFORE RELEASE ----------------
# 1 - Update Version Number (ONDEWO_NLU_VERSION)
# 2 - Update RELEASE.md
# 3 - make build
# -------------- Release Process Steps --------------
# 1 - Get Credentials from devops-accounts repo
# 2 - Check the credentials: both set, and the GitHub token may push here (utils image)
# 3 - Build, test and dry-run the NuGet publish - everything that can fail, before any push
# 4 - Commit and push, create Release Branch and push
# 5 - Create Release Tag and push
# 6 - NuGet Release (utils image)
# 7 - GitHub Release (utils image) - LAST, so an existing GitHub release marks a complete release

########################################################
# 		Variables
########################################################

# MUST BE THE SAME AS THE API in Major and Minor Version Number
# example: API 1.2.0 --> Client 1.2.X
ONDEWO_NLU_VERSION=7.3.0

# Submodule pins. Both are checked out by `make checkout_defined_submodule_versions`, so the
# generated code is always reproducible from this file alone.
ONDEWO_NLU_API_GIT_BRANCH=tags/7.3.0
ONDEWO_PROTO_COMPILER_GIT_BRANCH=tags/5.15.2

# Both credentials come from the ondewo-devops-accounts repository and nowhere else:
# run_release_with_devops reads GITHUB_GH_TOKEN from account_github.env and NUGET_API_KEY from
# account_nuget.env. The placeholders below only make an unset credential recognisable.
# GITHUB_GH_TOKEN must be allowed to push to ${GH_REPO_SLUG} - validate_release_credentials proves it.
GITHUB_GH_TOKEN?=ENTER_YOUR_TOKEN_HERE
# NUGET_API_KEY is a nuget.org API key scoped to "Push" for the glob pattern Ondewo.*.
NUGET_API_KEY?=ENTER_HERE_YOUR_NUGET_API_KEY
NUGET_SOURCE?=https://api.nuget.org/v3/index.json

# Terminate the release-notes slice on the ***** separator that delimits release entries, NOT on
# /\*\*/ - that matches the first markdown **bold** span inside the entry and silently truncates
# the notes there, with no error from `gh release create`.
CURRENT_RELEASE_NOTES=`cat RELEASE.md \
	| perl -ne 'print if /Release ONDEWO NLU Csharp Client ${ONDEWO_NLU_VERSION}/../^\*{5}/'`

GH_REPO="https://github.com/ondewo/ondewo-nlu-client-csharp"
GH_REPO_SLUG=ondewo/ondewo-nlu-client-csharp
DEVOPS_ACCOUNT_GIT="ondewo-devops-accounts"
DEVOPS_ACCOUNT_DIR="./${DEVOPS_ACCOUNT_GIT}"

# --- Layout
# ONDEWO_API_DIR       submodule holding the .proto sources; it is also the protoc -I root, so it
#                      is what the image gets as its <relative_protos_dir> argument.
# ONDEWO_PROTOS_SUBDIR sub-directory of that root the compilation is scoped to. The sibling
#                      google/ tree stays an import path and is never code-generated: those
#                      descriptors ship in the Google.Api.CommonProtos NuGet package.
ONDEWO_API_DIR=ondewo-nlu-api
ONDEWO_PROTO_COMPILER_DIR:=ondewo-proto-compiler
ONDEWO_PROTOS_SUBDIR=ondewo
ONDEWO_PROTOS_DIR=${ONDEWO_API_DIR}/${ONDEWO_PROTOS_SUBDIR}

# The fixed image tag is the ONLY contract with the compiler; `make build_compiler` rebuilds it
# from the pinned submodule.
PROTO_COMPILER_IMAGE=ondewo-csharp-proto-compiler:latest
PROTO_COMPILER_DOCKERFILE:=${ONDEWO_PROTO_COMPILER_DIR}/csharp/Dockerfile

# --- Utils image: the .NET SDK and the GitHub CLI (Dockerfile.utils), so the host needs neither.
IMAGE_UTILS_NAME=ondewo-nlu-client-utils-csharp:${ONDEWO_NLU_VERSION}
# The prefix every *_via_docker_image target runs `make <inner target>` of THIS Makefile with -
# append any `-e <CREDENTIAL>`, then ${IMAGE_UTILS_NAME} and the command.
#   * --user: as the invoking user, so nothing root-owned lands in the working tree. HOME and the
#     dotnet/NuGet caches therefore live in the container's world-writable /tmp, and never in the
#     repository, whose default Compile glob would sweep a NuGet packages folder into the library.
#   * The repository is mounted at its OWN path, so ${CURDIR} in the recipes and the absolute paths
#     MSBuild writes into obj/ mean the same inside and outside the container.
#   * A credential is passed by NAME only (`-e NUGET_API_KEY`): docker copies it from the
#     environment the `export` at the top gives every recipe, so the value is in neither the
#     recipe nor docker's argv.
#   * The make inside the container re-reads this Makefile and sees none of the host's command-line
#     overrides, so the two overridable (?=) settings are forwarded the same way: without that,
#     `make push_to_nuget_via_docker_image NUGET_SOURCE=<test feed>` would push to nuget.org.
UTILS_DOCKER_RUN=docker run --rm \
	--user "$$(id -u):$$(id -g)" \
	-e HOME=/tmp/home \
	-e DOTNET_CLI_HOME=/tmp/home \
	-e NUGET_PACKAGES=/tmp/home/.nuget/packages \
	-e NUGET_SOURCE \
	-e COVERAGE_THRESHOLD \
	-v "${CURDIR}:${CURDIR}" -w "${CURDIR}"

# --- MSBuild properties read by the generated project file
# The generated <PackageId>.csproj deliberately carries no literal version: it reads
# $(OndewoPackageId), $(OndewoPackageVersion), $(OndewoTargetFramework), $(GoogleProtobufVersion),
# $(GrpcDotnetVersion) and $(GoogleApiCommonProtosVersion). Inside the compiler image those come
# from its Dockerfile ARG lines; on the host they are read back OUT of that same pinned Dockerfile
# here, so a host build can never drift from the image. `export` (top of this file) hands every
# one of them to MSBuild, which reads environment variables as properties.
OndewoPackageId=Ondewo.NLU.Client
OndewoPackageVersion=${ONDEWO_NLU_VERSION}
# The two artefacts `dotnet pack` produces, named by NuGet's fixed <id>.<version>.<ext> convention.
# The .snupkg is the symbol package: `dotnet nuget push` uploads it automatically when it sits next
# to the .nupkg it belongs to, which is why both live in the same directory.
NUPKG_DIR=nupkg
NUPKG=${NUPKG_DIR}/${OndewoPackageId}.${ONDEWO_NLU_VERSION}.nupkg
SNUPKG=${NUPKG_DIR}/${OndewoPackageId}.${ONDEWO_NLU_VERSION}.snupkg
# Scratch tree for publish_dry_run: the extracted nuspec and an isolated consumer restore.
DRY_RUN_DIR=.nuget-dry-run
# Keeps the .proto submodule out of the SDK's default Compile glob on a host build.
OndewoProtosDir=${ONDEWO_API_DIR}
# In a plain clone the submodule is not checked out and every $(shell sed ...)
# below yields the empty string. Directory.Build.props then supplies a committed fallback for each
# of them (it declares them only when they are still empty, so an exported value here always
# wins), and `make check_dotnet_properties` fails the build when the two ever disagree.
DOTNET_PROPS_FILE=Directory.Build.props
OndewoTargetFramework:=$(shell sed -n 's|^ARG DOTNET_TARGET_FRAMEWORK=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)
GoogleProtobufVersion:=$(shell sed -n 's|^ARG GOOGLE_PROTOBUF_VERSION=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)
GrpcDotnetVersion:=$(shell sed -n 's|^ARG GRPC_DOTNET_VERSION=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)
GoogleApiCommonProtosVersion:=$(shell sed -n 's|^ARG GOOGLE_API_COMMONPROTOS_VERSION=||p' ${PROTO_COMPILER_DOCKERFILE} 2>/dev/null)

# `make` with no target prints the help listing.
.DEFAULT_GOAL := help

# Define colors globally (reused for [INFO]/[SUCCESS]/[WARN]/[ERROR] log lines in recipes)
BLUE   := \033[1;34m
GREEN  := \033[0;32m
YELLOW := \033[1;33m
RED    := \033[0;31m
NC     := \033[0m

########################################################
#       ONDEWO Standard Make Targets
########################################################

setup_developer_environment_locally: update_submodules install_precommit_hooks ## Ready a fresh laptop: check out the submodules and install the pre-commit hooks

install_precommit_hooks: ## Installs pre-commit hooks and sets them up for the ondewo-nlu-client-csharp repo
	pip install pre-commit
	pre-commit install
	pre-commit install --hook-type commit-msg

precommit_hooks_run_all_files: ## Runs all pre-commit hooks on all files and not just the changed ones
	pre-commit run --all-files

help: ## Print usage info about help targets
	# (first comment after target starting with double hashes ##)
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' Makefile | sort | awk 'BEGIN {FS = ":.*?## "}; {printf "\033[36m%-40s\033[0m %s\n", $$1, $$2}'

makefile_chapters: ## Shows all sections of Makefile
	@echo `cat Makefile| grep "########################################################" -A 1 | grep -v "########################################################"`

TEST: ## Diagnostics - print the resolved build configuration and the current release notes
	@echo "Client version:       ${ONDEWO_NLU_VERSION}"
	@echo "API submodule pin:    ${ONDEWO_NLU_API_GIT_BRANCH}"
	@echo "Compiler pin:         ${ONDEWO_PROTO_COMPILER_GIT_BRANCH}"
	@echo "Compiler image:       ${PROTO_COMPILER_IMAGE}"
	@echo "Utils image:          ${IMAGE_UTILS_NAME}"
	@echo "NuGet package id:     ${OndewoPackageId}"
	@echo "Target framework:     ${OndewoTargetFramework}"
	@echo "Google.Protobuf:      ${GoogleProtobufVersion}"
	@echo "Grpc.Net.Client:      ${GrpcDotnetVersion}"
	@echo "Google.Api.CommonProtos: ${GoogleApiCommonProtosVersion}"
	@echo "NuGet source:         ${NUGET_SOURCE}"
	@echo "GITHUB_GH_TOKEN set:  $(if $(filter-out ENTER_YOUR_TOKEN_HERE,$(GITHUB_GH_TOKEN)),yes,no)"
	@echo "NUGET_API_KEY set:    $(if $(filter-out ENTER_HERE_YOUR_NUGET_API_KEY,$(NUGET_API_KEY)),yes,no)"
	@printf '\n%s\n' "${CURRENT_RELEASE_NOTES}"

########################################################
#       Repo Specific Make Targets
########################################################
#		Build

build: clean update_submodules checkout_defined_submodule_versions build_compiler generate_ondewo_protos check_build ## Build the client library: submodules -> compiler image -> stubs -> NuGet package
	@echo "$(GREEN)[SUCCESS]$(NC) ${OndewoPackageId} ${ONDEWO_NLU_VERSION} built"

build_compiler: ## Build the proto compiler docker image from the pinned submodule
	@echo "$(BLUE)[INFO]$(NC) Building ${PROTO_COMPILER_IMAGE} from ${ONDEWO_PROTO_COMPILER_DIR}/csharp ..."
	@test -f ${PROTO_COMPILER_DOCKERFILE} || { \
		echo "$(RED)[ERROR]$(NC) ${PROTO_COMPILER_DOCKERFILE} is missing - run 'make update_submodules' first"; \
		exit 1; \
	}
# The image COPYs image-data/ with the checkout's file modes, and generate_ondewo_protos runs it as the
# invoking user, not root: a checkout made under `umask 077` (e.g. around a release log) lands root-owned
# 0600 in the image ("compile-proto-2-csharp.sh: Permission denied"). a+rX only adds read, and search on
# directories - git tracks neither, so the submodule stays clean.
	chmod -R a+rX ${ONDEWO_PROTO_COMPILER_DIR}/csharp/image-data
	cd ${ONDEWO_PROTO_COMPILER_DIR}/csharp && sh build.sh

# Derived from ondewo-proto-compiler/csharp/example/run-compile.sh - same image tag and the same
# three positional arguments <relative_protos_dir> <target_subdir> <package_id>.
#   * NO -it: it breaks every non-interactive caller ("cannot attach stdin to a TTY-enabled
#     container because stdin is not a terminal"). Keep -it only on an interactive
#     --entrypoint /bin/bash debug run.
#   * The repository IS the input volume: the image copies it into an internal compile directory
#     and compiles there, so nothing mounted is ever mutated, and any hand-written .cs shipped
#     beside the stubs is picked up by the SDK's default Compile glob.
#   * The repository is also the output volume: the image writes api/, artifacts/ and nupkg/ and
#     wipes exactly those three first, so a renamed or deleted proto leaves no orphan behind.
#   * -e OndewoPackageVersion overrides the image default (which is the COMPILER version) with
#     this client's version, so the packed .nupkg carries the right number.
#   * --user: the container runs as the invoking user, so everything it writes to the output
#     volume is owned by you and no chown - and no sudo - is needed afterwards. Two defaults of the
#     image are root-owned and are moved to the world-writable /tmp for that: its compile directory
#     /image-data/src (TEMP_SRC_DIRECTORY, an override compile-proto-2-csharp.sh supports), and the
#     /tmp/.nuget the image build left behind as dotnet's home ("Failed to read NuGet.Config due to
#     unauthorized access" - HOME / DOTNET_CLI_HOME). Its pre-warmed feed under /nuget is
#     world-writable by design.
generate_ondewo_protos: ## Generate the csharp gRPC client stubs and the NuGet package from the API protos
	@test -d ${ONDEWO_PROTOS_DIR} || { \
		echo "$(RED)[ERROR]$(NC) ${ONDEWO_PROTOS_DIR} is missing - run 'make update_submodules' first"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) Generating csharp stubs from ${ONDEWO_PROTOS_DIR} into api/ ..."
	docker run --rm \
		--user "$$(id -u):$$(id -g)" \
		-e HOME=/tmp/home \
		-e DOTNET_CLI_HOME=/tmp/home \
		-e TEMP_SRC_DIRECTORY=/tmp/compile-src \
		-e OndewoPackageVersion=${ONDEWO_NLU_VERSION} \
		-v ${shell pwd}:/input-volume \
		-v ${shell pwd}:/output-volume \
		${PROTO_COMPILER_IMAGE} "${ONDEWO_API_DIR}" "${ONDEWO_PROTOS_SUBDIR}" "${OndewoPackageId}"
	@echo "$(GREEN)[SUCCESS]$(NC) Generated api/, ${OndewoPackageId}.csproj, artifacts/ and nupkg/"

# protoc emits one <PascalCase(basename)>.cs per proto plus one <PascalCase(basename)>Grpc.cs per
# proto that declares a service, so the file count is NOT a fixed multiple of the proto count.
# Both sides are therefore normalised (lower-cased, separators removed) before they are compared.
check_build: ## Checks that a csharp stub was generated for every compiled proto
	@test -d api || { \
		echo "$(RED)[ERROR]$(NC) no api/ directory - run 'make generate_ondewo_protos' first"; \
		exit 1; \
	}
	@find api -type f -name '*.cs' -exec basename {} .cs ';' \
		| sed -e 's/Grpc$$//' | tr -d '_-' | tr 'A-Z' 'a-z' | sort -u > build_check.txt
	@rc=0 ; \
	for proto in $$(find ${ONDEWO_PROTOS_DIR} -type f -name '*.proto') ; do \
		want=$$(basename "$$proto" .proto | tr -d '_-' | tr 'A-Z' 'a-z') ; \
		grep -qx "$$want" build_check.txt || { echo "$(RED)[ERROR]$(NC) No csharp stub for $$proto" ; rc=1 ; } ; \
	done ; \
	rm -f build_check.txt ; \
	if [ $$rc -ne 0 ]; then exit 1 ; fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) every proto under ${ONDEWO_PROTOS_DIR} has a generated stub"

# The generated project file has no literal versions - it reads them as MSBuild properties. They
# are pinned in ONE place, the compiler's Dockerfile ARG lines, and mirrored into
# ${DOTNET_PROPS_FILE} so that a checkout without submodules still builds. Drift between the two
# would silently compile the committed stubs against a different package graph than the image that
# generated them, so it is an error here rather than a surprise at run time.
check_dotnet_properties: ## Verify the committed MSBuild pins match the pinned compiler Dockerfile
	@if [ ! -f ${PROTO_COMPILER_DOCKERFILE} ]; then \
		echo "$(RED)[ERROR]$(NC) ${PROTO_COMPILER_DOCKERFILE} is not checked out, so the pins committed in" ; \
		echo "        ${DOTNET_PROPS_FILE} cannot be compared against anything and this target" ; \
		echo "        cannot do its job. It used to warn and exit 0 here, which made it a no-op in" ; \
		echo "        every submodule-free checkout - CI included - so it never once verified a pin." ; \
		echo "        Check the one submodule out and re-run:" ; \
		echo "            git submodule update --init ${ONDEWO_PROTO_COMPILER_DIR}" ; \
		exit 1 ; \
	fi ; \
	rc=0 ; \
	for pair in "OndewoTargetFramework=DOTNET_TARGET_FRAMEWORK" \
	            "GoogleProtobufVersion=GOOGLE_PROTOBUF_VERSION" \
	            "GrpcDotnetVersion=GRPC_DOTNET_VERSION" \
	            "GoogleApiCommonProtosVersion=GOOGLE_API_COMMONPROTOS_VERSION" ; do \
		property=$${pair%%=*} ; \
		argument=$${pair##*=} ; \
		pinned=$$(sed -n "s|^ARG $$argument=||p" ${PROTO_COMPILER_DOCKERFILE}) ; \
		committed=$$(sed -n "s|.*<$$property[^>]*>\(.*\)</$$property>.*|\1|p" ${DOTNET_PROPS_FILE}) ; \
		if [ -z "$$pinned" ] || [ "$$pinned" != "$$committed" ]; then \
			echo "$(RED)[ERROR]$(NC) $$property is '$$committed' in ${DOTNET_PROPS_FILE} but" ; \
			echo "        ARG $$argument='$$pinned' in ${PROTO_COMPILER_DOCKERFILE} - update the former" ; \
			rc=1 ; \
		fi ; \
	done ; \
	if [ $$rc -ne 0 ]; then exit 1 ; fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) the committed MSBuild pins match ${PROTO_COMPILER_DOCKERFILE}"

build_library: check_dotnet_properties ## Compile the generated library with the dotnet on PATH (in docker: test_via_docker_image)
	@test -f ${OndewoPackageId}.csproj || { \
		echo "$(RED)[ERROR]$(NC) ${OndewoPackageId}.csproj is missing - run 'make build' first"; \
		exit 1; \
	}
	dotnet build ${OndewoPackageId}.csproj -c Release

# Coverage is measured over the HAND-WRITTEN sources only: everything under api/ is machine
# output, and a coverage number over generated code measures the generator, not this repository.
# The stubs are still EXERCISED by the suite - it round-trips every generated message through the
# wire format and binds every generated service client to a channel - they are just not counted.
# `Include` narrows instrumentation to the client assembly (the test assembly itself and the
# Google.Protobuf / Grpc.* dependencies are none of our business) and `ExcludeByFile` then drops
# the generated half of it, which leaves exactly auth/.
# The commas in the coverlet switches are written `%2c`: MSBuild splits a /p: value on a literal
# comma and aborts with "MSB1006: Property is not valid. Switch: lcov".
COVERAGE_THRESHOLD?=100
TEST_PROJECT=tests/Ondewo.NLU.Client.Tests/Ondewo.NLU.Client.Tests.csproj

test: build_library ## Run the csharp test suite over the committed stubs, gated on hand-written coverage
	@test -f ${TEST_PROJECT} || { \
		echo "$(RED)[ERROR]$(NC) ${TEST_PROJECT} is missing - this repository must ship a test project"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) dotnet test ${TEST_PROJECT} (coverage gate: ${COVERAGE_THRESHOLD}% of the hand-written sources)"
	dotnet test ${TEST_PROJECT} -c Release \
		/p:CollectCoverage=true \
		/p:Include="[${OndewoPackageId}]*" \
		/p:ExcludeByFile="**/api/**/*.cs" \
		/p:CoverletOutput=${CURDIR}/coverage/ \
		/p:CoverletOutputFormat=cobertura%2clcov \
		/p:Threshold=${COVERAGE_THRESHOLD} \
		/p:ThresholdType=line%2cbranch%2cmethod \
		/p:ThresholdStat=total
	@echo "$(GREEN)[SUCCESS]$(NC) test suite passed with >= ${COVERAGE_THRESHOLD}% coverage of the hand-written sources"

# -warnaserror promotes the NU5xxx packaging warnings to errors, which is what turns "nuget.org
# warns about a missing licence/readme" into a failed build here instead of a published package
# nobody wants. It is safe to be this strict only because --no-build implies --no-restore: the
# restore-time NuGet audit advisories (NU19xx), which appear when a CVE is published for a
# dependency and have nothing to do with packaging, cannot reach this step.
pack: build_library ## Pack the NuGet package (.nupkg + .snupkg) into nupkg/ with the dotnet on PATH
	dotnet pack ${OndewoPackageId}.csproj -c Release --no-build -o ${NUPKG_DIR} -warnaserror

clean: ## Remove the generated stubs, the build output and the packed packages
	rm -rf api artifacts ${NUPKG_DIR} bin obj coverage build_check.txt ${DRY_RUN_DIR}

########################################################
#		Submodules

update_submodules: ## Initialize and update all submodules
	@echo "$(BLUE)[INFO]$(NC) START initializing submodules ..."
	git submodule update --init --recursive
	@echo "$(GREEN)[SUCCESS]$(NC) DONE initializing submodules"

checkout_defined_submodule_versions: ## Check out the submodule versions pinned at the top of this Makefile
	@echo "$(BLUE)[INFO]$(NC) START checking out submodules ..."
	git -C ${ONDEWO_API_DIR} fetch --all
	git -C ${ONDEWO_API_DIR} checkout ${ONDEWO_NLU_API_GIT_BRANCH}
	git -C ${ONDEWO_PROTO_COMPILER_DIR} fetch --all
	git -C ${ONDEWO_PROTO_COMPILER_DIR} checkout ${ONDEWO_PROTO_COMPILER_GIT_BRANCH}
	@echo "$(GREEN)[SUCCESS]$(NC) DONE checking out submodules"

########################################################
#		Release

check_release_credentials: ## Assert both release credentials are set before anything is pushed
# The registry credential used to be exercised only by the very LAST step of `release`, long after
# the release branch, the tag and the GitHub release had been pushed to origin. A missing NuGet key
# then left an immovable tag behind, and `spc` refused every retry because that branch and tag now
# existed - so the recovery was to hand-delete both from origin. Both credentials are therefore
# checked here, while the release is still a no-op. This proves only that they are SET; whether the
# GitHub token works is checked by validate_release_credentials.
	@rc=0 ; \
	if [ -z "${GITHUB_GH_TOKEN}" ] || [ "${GITHUB_GH_TOKEN}" = "ENTER_YOUR_TOKEN_HERE" ]; then \
		echo "$(RED)[ERROR]$(NC) GITHUB_GH_TOKEN is not set - make ondewo_release reads it from" ; \
		echo "        ondewo-devops-accounts/account_github.env" ; \
		rc=1 ; \
	fi ; \
	if [ -z "${NUGET_API_KEY}" ] || [ "${NUGET_API_KEY}" = "ENTER_HERE_YOUR_NUGET_API_KEY" ]; then \
		echo "$(RED)[ERROR]$(NC) NUGET_API_KEY is not set - make ondewo_release reads it from" ; \
		echo "        ondewo-devops-accounts/account_nuget.env (a nuget.org API key scoped to Push" ; \
		echo "        for the glob pattern 'Ondewo.*')" ; \
		rc=1 ; \
	fi ; \
	if [ $$rc -ne 0 ]; then \
		echo "$(RED)[ERROR]$(NC) refusing to start a release that cannot finish it" ; \
		exit 1 ; \
	fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) both release credentials are set"

release: ## Automate the entire release process - locally, publishing included
	@echo "$(BLUE)[INFO]$(NC) Start release ${ONDEWO_NLU_VERSION}"
# FIRST, before anything is built, branched, tagged or pushed: a release with a missing credential or a
# GitHub token that cannot push has to fail while it is still a no-op, not after a tag it cannot take
# back is on origin. The utils image is built here because the token check already runs in it.
	make check_release_credentials
	make build_utils_docker_image
	make validate_release_credentials_via_docker_image
	make check_release_notes
	make update_readme_version
	make build
	-make precommit_hooks_run_all_files
	git status
	make check_build
# Everything else that can fail also runs HERE, inside the utils image, while the release is still a
# no-op: the library build, the test suite with its coverage gate and the whole packaging path. The
# package publish_dry_run leaves in nupkg/ is the one push_to_nuget uploads below.
	make test_via_docker_image
	make publish_dry_run_via_docker_image
	git add api
	git add ${OndewoPackageId}.csproj
	git add Makefile
	git add README.md
	git add RELEASE.md
# tests/ is NOT packaged, but leaving it out of the release commit means a regression test written
# alongside a fix never reaches the repository and CI never runs it.
	-git add tests
	git add ${ONDEWO_PROTO_COMPILER_DIR}
	git add ${ONDEWO_API_DIR}
	git status
# Commit only when something is staged, and let a commit that FAILS stop the release. The old `-git
# commit` ignored every failure - a missing git identity included - and then tagged and published the
# previous commit under the new version.
	git diff --cached --quiet || git commit --no-verify -m "Preparing for release ${ONDEWO_NLU_VERSION}"
	git push
	make create_release_branch
	make create_release_tag
# Past the tag only the two publishing steps remain, both in the utils image built above. NuGet goes
# first and the GitHub release LAST, so a GitHub release exists only for a version that is complete
# on nuget.org too.
	make push_to_nuget_via_docker_image
	make release_to_github_via_docker_image
	@echo "$(GREEN)[SUCCESS]$(NC) Release finished"

create_release_branch: ## Create Release Branch and push it to origin
	git checkout -b "release/${ONDEWO_NLU_VERSION}"
	git push -u origin "release/${ONDEWO_NLU_VERSION}"

create_release_tag: ## Create Release Tag and push it to origin
	git tag -a ${ONDEWO_NLU_VERSION} -m "release/${ONDEWO_NLU_VERSION}"
	git push origin ${ONDEWO_NLU_VERSION}

login_to_gh: ## Login to Github CLI with Access Token
	@if [ -z "${GITHUB_GH_TOKEN}" ] || [ "${GITHUB_GH_TOKEN}" = "ENTER_YOUR_TOKEN_HERE" ]; then \
		echo "$(RED)[ERROR]$(NC) GITHUB_GH_TOKEN is not set - make ondewo_release reads it from ondewo-devops-accounts/account_github.env"; \
		exit 1; \
	fi
	@echo "${GITHUB_GH_TOKEN}" | gh auth login -p ssh --with-token

# The validity half of the credential check - check_release_credentials only proves the values are
# SET. Read-only, and run in the utils image before anything is pushed:
#   * login_to_gh is the very `gh auth login` push_to_gh runs at the end of the release, so a revoked,
#     expired or under-scoped token fails HERE ("HTTP 401: Bad credentials", "missing required
#     scope") instead of after the tag;
#   * `gh api repos/<repo>` then returns the token's permissions on THIS repository, and the push to
#     master, the release branch, the tag and the GitHub release all need push.
# NUGET_API_KEY has no equivalent. nuget.org documents no read-only endpoint that accepts a push API
# key: GET api/v2/verifykey takes only a one-time verify-scope key, and create-verification-key is a
# POST that mints one (https://learn.microsoft.com/nuget/api/nuget-protocols). The key is therefore
# first exercised by push_to_nuget, which is why that step is safe to re-run (--skip-duplicate).
validate_release_credentials: login_to_gh ## Fail unless GITHUB_GH_TOKEN logs in and may push to the repository (read-only; needs gh)
	@push=$$(gh api repos/${GH_REPO_SLUG} --jq .permissions.push) || { \
		echo "$(RED)[ERROR]$(NC) could not read ${GH_REPO_SLUG} with GITHUB_GH_TOKEN"; \
		exit 1; \
	}; \
	if [ "$$push" != "true" ]; then \
		echo "$(RED)[ERROR]$(NC) GITHUB_GH_TOKEN works, but may not push to ${GH_REPO_SLUG} (permissions.push=$$push)"; \
		echo "        - replace it in ondewo-devops-accounts/account_github.env before releasing"; \
		exit 1; \
	fi; \
	echo "$(GREEN)[SUCCESS]$(NC) GITHUB_GH_TOKEN may push to ${GH_REPO_SLUG}"

check_release_notes: ## Assert RELEASE.md carries an entry for ONDEWO_NLU_VERSION
# `gh release create -n ""` succeeds and publishes an EMPTY release, so an entry that was forgotten -
# or a heading whose wording drifted away from what the CURRENT_RELEASE_NOTES flip-flop greps for -
# is otherwise only noticed by whoever reads the release page afterwards, by which time the tag
# exists and cannot be moved.
	@notes="$(CURRENT_RELEASE_NOTES)"; \
	if [ -z "$$notes" ]; then \
		echo "$(RED)[ERROR]$(NC) RELEASE.md has no 'Release ONDEWO NLU Csharp Client ${ONDEWO_NLU_VERSION}' entry"; \
		echo "        The GitHub release would be created with empty notes - add the entry, under that"; \
		echo "        exact heading and terminated by the ***** separator, before releasing."; \
		exit 1; \
	fi; \
	echo "$(GREEN)[SUCCESS]$(NC) RELEASE.md has release notes for ${ONDEWO_NLU_VERSION}"

build_gh_release: check_release_notes ## Generate Github Release with CLI
	gh release create --repo $(GH_REPO) "$(ONDEWO_NLU_VERSION)" -n "$(CURRENT_RELEASE_NOTES)" -t "Release ${ONDEWO_NLU_VERSION}"

push_to_gh: login_to_gh build_gh_release ## Logs into GitHub CLI and releases
	@echo 'Released to Github'

# README.md is packed as the package readme, so its install snippets are what the nuget.org page of
# a version tells people to install - and release_all_clients in ondewo-nlu-api rewrites nothing
# but this Makefile and RELEASE.md. `release` therefore derives them from ONDEWO_NLU_VERSION before
# anything is packed, and stages README.md with the release commit. A snippet the patterns no longer
# find is an error, not a README that silently keeps advertising the previous version.
update_readme_version: ## Point the install snippets in README.md (the packed package readme) at ONDEWO_NLU_VERSION
	perl -i -p \
		-e 's/(Ondewo\.NLU\.Client --version )\S+/$${1}${ONDEWO_NLU_VERSION}/;' \
		-e 's/(Include="Ondewo\.NLU\.Client" Version=")[^"]*/$${1}${ONDEWO_NLU_VERSION}/;' \
		-e 's/(Install-Package Ondewo\.NLU\.Client -Version )\S+/$${1}${ONDEWO_NLU_VERSION}/;' \
		README.md
	@for snippet in 'Ondewo.NLU.Client --version ${ONDEWO_NLU_VERSION}' \
		'Include="Ondewo.NLU.Client" Version="${ONDEWO_NLU_VERSION}"' \
		'Install-Package Ondewo.NLU.Client -Version ${ONDEWO_NLU_VERSION}' ; do \
		grep -qF "$$snippet" README.md || { \
			echo "$(RED)[ERROR]$(NC) README.md has no '$$snippet' - adapt update_readme_version to it" ; \
			exit 1 ; \
		} ; \
	done
	@echo "$(GREEN)[SUCCESS]$(NC) README.md installs ${OndewoPackageId} ${ONDEWO_NLU_VERSION}"

########################################################
#		NUGET

# The credential-free half of publishing. It exercises the whole packaging path - pack, metadata,
# installability - so a package that nuget.org would reject, or that nobody could consume, fails
# before `release` pushes anything instead of after the tag. `release` runs it in the utils image
# (publish_dry_run_via_docker_image), and the package it leaves in nupkg/ is the one push_to_nuget
# uploads.
publish_dry_run: pack verify_nupkg_metadata verify_nupkg_installs ## Exercise the full packaging path without any credential
	@echo "$(GREEN)[SUCCESS]$(NC) ${NUPKG} is ready to publish - make release uploads it with push_to_nuget_via_docker_image"

# Asserts on the PACKED artefact rather than on the .csproj: a property can be silently dropped on
# the way into the nuspec (PackageReadmeFile behind a false Exists() condition is exactly that), and
# the nuspec is what nuget.org reads. Every element below is one nuget.org warns about - or, for
# licence and description, rejects the upload over - when it is missing.
verify_nupkg_metadata: ## Assert the packed .nupkg/.snupkg carry the metadata nuget.org expects
	@test -f "${NUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing - run 'make pack' first"; \
		exit 1; \
	}
	@test -f "${SNUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${SNUPKG} is missing - IncludeSymbols and SymbolPackageFormat=snupkg"; \
		echo "        must stay set in ${OndewoPackageId}.csproj"; \
		exit 1; \
	}
	@mkdir -p ${DRY_RUN_DIR}
	@unzip -p "${NUPKG}" "${OndewoPackageId}.nuspec" > ${DRY_RUN_DIR}/packed.nuspec
	@unzip -l "${NUPKG}"  > ${DRY_RUN_DIR}/nupkg.list
	@unzip -l "${SNUPKG}" > ${DRY_RUN_DIR}/snupkg.list
	@echo "$(BLUE)[INFO]$(NC) Checking the nuspec of ${NUPKG} ..."
	@rc=0 ; \
	require() { \
		if grep -Eq "$$2" "$$3" ; then \
			echo "  $(GREEN)ok$(NC)      $$1" ; \
		else \
			echo "  $(RED)MISSING$(NC) $$1 - no match for /$$2/ in $$3" ; \
			rc=1 ; \
		fi ; \
	} ; \
	nuspec=${DRY_RUN_DIR}/packed.nuspec ; \
	require "PackageId"              "<id>${OndewoPackageId}</id>"                    "$$nuspec" ; \
	require "Version"                "<version>${ONDEWO_NLU_VERSION}</version>"       "$$nuspec" ; \
	require "Authors"                "<authors>.+</authors>"                          "$$nuspec" ; \
	require "Description"            "<description>.+</description>"                  "$$nuspec" ; \
	require "PackageLicenseExpression" "<license type=\"expression\">.+</license>"     "$$nuspec" ; \
	require "PackageProjectUrl"      "<projectUrl>https?://.+</projectUrl>"            "$$nuspec" ; \
	require "RepositoryType"         "<repository[^>]+type=\"git\""                    "$$nuspec" ; \
	require "RepositoryUrl"          "<repository[^>]+url=\"https?://.+\""             "$$nuspec" ; \
	require "PackageReadmeFile"      "<readme>README.md</readme>"                      "$$nuspec" ; \
	require "readme in the package"  "[[:space:]]README.md$$"                          "${DRY_RUN_DIR}/nupkg.list" ; \
	require "assembly in the package" "lib/.+/${OndewoPackageId}.dll$$"                "${DRY_RUN_DIR}/nupkg.list" ; \
	require "portable pdb in the symbols" "lib/.+/${OndewoPackageId}.pdb$$"            "${DRY_RUN_DIR}/snupkg.list" ; \
	if [ $$rc -ne 0 ]; then \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing metadata nuget.org expects - fix ${OndewoPackageId}.csproj" ; \
		exit 1 ; \
	fi ; \
	echo "$(GREEN)[SUCCESS]$(NC) ${NUPKG} and ${SNUPKG} carry the expected metadata"

# The strongest proof available without an API key: resolve the freshly packed .nupkg out of a local
# folder feed into a throwaway consumer project. A malformed nuspec, a wrong id or version, a target
# framework no consumer can use (NU1202) or a dependency that does not exist all fail here - on
# nuget.org the same defects are a rejected upload or a package nobody can install.
#   * --packages points the restore at an EMPTY private packages folder, so the resolution cannot be
#     satisfied from a previous run left in ~/.nuget/packages and pass without touching the feed.
#   * restore only, never build: a build would emit obj/**/*AssemblyInfo.cs under the repository
#     root, where the library project's default Compile glob would pick it up (CS0579).
verify_nupkg_installs: ## Restore the packed package into a throwaway consumer project
	@test -f "${NUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing - run 'make pack' first"; \
		exit 1; \
	}
	@rm -rf ${DRY_RUN_DIR}/consumer ${DRY_RUN_DIR}/packages
	@mkdir -p ${DRY_RUN_DIR}/consumer
	@printf '%s\n' \
		'<?xml version="1.0" encoding="utf-8"?>' \
		'<configuration>' \
		'  <packageSources>' \
		'    <clear />' \
		'    <add key="dry-run-local" value="${CURDIR}/${NUPKG_DIR}" />' \
		'    <add key="nuget.org" value="${NUGET_SOURCE}" />' \
		'  </packageSources>' \
		'</configuration>' > ${DRY_RUN_DIR}/consumer/nuget.config
	@printf '%s\n' \
		'<Project Sdk="Microsoft.NET.Sdk">' \
		'  <PropertyGroup>' \
		'    <TargetFramework>netstandard2.0</TargetFramework>' \
		'  </PropertyGroup>' \
		'  <ItemGroup>' \
		'    <PackageReference Include="${OndewoPackageId}" Version="${ONDEWO_NLU_VERSION}" />' \
		'  </ItemGroup>' \
		'</Project>' > ${DRY_RUN_DIR}/consumer/consumer.csproj
	@echo "$(BLUE)[INFO]$(NC) Restoring ${OndewoPackageId} ${ONDEWO_NLU_VERSION} from ${CURDIR}/${NUPKG_DIR} into a throwaway consumer ..."
	dotnet restore ${DRY_RUN_DIR}/consumer/consumer.csproj \
		--configfile ${CURDIR}/${DRY_RUN_DIR}/consumer/nuget.config \
		--packages ${CURDIR}/${DRY_RUN_DIR}/packages
	@test -d "${DRY_RUN_DIR}/packages/$$(echo ${OndewoPackageId} | tr 'A-Z' 'a-z')/${ONDEWO_NLU_VERSION}" || { \
		echo "$(RED)[ERROR]$(NC) the restore succeeded but did not install ${OndewoPackageId} ${ONDEWO_NLU_VERSION}"; \
		exit 1; \
	}
	@echo "$(GREEN)[SUCCESS]$(NC) a consumer can install ${OndewoPackageId} ${ONDEWO_NLU_VERSION} and its dependency graph"

# `@`-prefixed so the API key never reaches the build log, and read from the environment rather
# than written into the recipe (the `export` at the top of this file exports every variable here).
# It IS still passed to `dotnet nuget push` as an --api-key ARGUMENT, so for the seconds the upload
# takes it is readable in the machine's process table. That is not an oversight, it is the only
# thing the tool supports on Linux; both alternatives were measured against a local push endpoint:
#   * the <apikeys> section of a NuGet.Config is read through EncryptionUtility.DecryptString, which
#     fails outright with "Encryption is not supported on non-Windows platforms";
#   * a response file (`dotnet nuget push ... @file`) is expanded by the `dotnet` muxer, which then
#     re-execs NuGet.CommandLine.XPlat.dll with the key spelled out in the CHILD's argv anyway.
# Keep the key narrowly scoped instead (Push only, glob Ondewo.*) so it is worth little if it leaks.
# Pushing the .nupkg also uploads the .snupkg sitting beside it.
push_to_nuget: ## Publish the packed NuGet package to nuget.org
	@if [ -z "${NUGET_API_KEY}" ] || [ "${NUGET_API_KEY}" = "ENTER_HERE_YOUR_NUGET_API_KEY" ]; then \
		echo "$(RED)[ERROR]$(NC) NUGET_API_KEY is not set - make ondewo_release reads it from"; \
		echo "        ondewo-devops-accounts/account_nuget.env"; \
		exit 1; \
	fi
	@test -f "${NUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${NUPKG} is missing - run 'make publish_dry_run_via_docker_image' first"; \
		exit 1; \
	}
	@test -f "${SNUPKG}" || { \
		echo "$(RED)[ERROR]$(NC) ${SNUPKG} is missing - it is published together with ${NUPKG}"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) Pushing ${OndewoPackageId} ${ONDEWO_NLU_VERSION} (+ symbols) to ${NUGET_SOURCE} ..."
	@dotnet nuget push "${NUPKG}" \
		--api-key "$$NUGET_API_KEY" \
		--source ${NUGET_SOURCE} \
		--skip-duplicate
	@echo "$(GREEN)[SUCCESS]$(NC) Released to NuGet"

########################################################
#		UTILS IMAGE - dotnet and gh without installing either on the host

build_utils_docker_image: ## Build the utils image (.NET SDK + gh) that every *_via_docker_image target runs in
	docker build -f Dockerfile.utils -t ${IMAGE_UTILS_NAME} .

test_via_docker_image: build_utils_docker_image ## Run `make test` inside the utils image - no local .NET SDK needed
	${UTILS_DOCKER_RUN} ${IMAGE_UTILS_NAME} make test

publish_dry_run_via_docker_image: build_utils_docker_image ## Run `make publish_dry_run` inside the utils image - no local .NET SDK needed
	${UTILS_DOCKER_RUN} ${IMAGE_UTILS_NAME} make publish_dry_run

validate_release_credentials_via_docker_image: build_utils_docker_image ## Run `make validate_release_credentials` inside the utils image (read-only, before any push)
	@${UTILS_DOCKER_RUN} -e GITHUB_GH_TOKEN ${IMAGE_UTILS_NAME} make validate_release_credentials

# The two publishing steps do NOT rebuild the image: `release` builds it before anything is pushed,
# and a rebuild failing once the tag is on origin would leave a half-published release behind.
# `release` uploads to nuget.org only through this target - no CI workflow publishes. --skip-duplicate
# in push_to_nuget makes a re-run for a version nuget.org already has a no-op, so a release that
# stopped at either step can be finished by running both steps again.
push_to_nuget_via_docker_image: ## Publish the packed package to nuget.org inside the utils image (make push_to_nuget)
	@${UTILS_DOCKER_RUN} -e NUGET_API_KEY ${IMAGE_UTILS_NAME} make push_to_nuget

# The last step of `release`, so a GitHub release exists only for a version that is complete.
release_to_github_via_docker_image: ## Create the GitHub release inside the utils image (make push_to_gh)
	@${UTILS_DOCKER_RUN} -e GITHUB_GH_TOKEN ${IMAGE_UTILS_NAME} make push_to_gh

########################################################
#		DEVOPS-ACCOUNTS

ondewo_release: spc clone_devops_accounts run_release_with_devops ## Release with credentials from devops-accounts repo
	@rm -rf ${DEVOPS_ACCOUNT_GIT}

clone_devops_accounts: ## Clones devops-accounts repo
	if [ -d $(DEVOPS_ACCOUNT_GIT) ]; then rm -Rf $(DEVOPS_ACCOUNT_GIT); fi
	git clone git@bitbucket.org:ondewo/${DEVOPS_ACCOUNT_GIT}.git

# Exactly the two credentials this client needs, and each from its own file. The greps are ANCHORED on
# `^NAME=`: the devops files carry '#' comment lines that mention variable names, and an unanchored
# grep hands such a line to the command line below, where its '#' comments out every credential
# after it. `@` so make never echoes the expanded line - it carries the secrets.
run_release_with_devops: ## Read credentials from the cloned devops-accounts repo and run the full release
	$(eval info:= $(shell grep -E '^GITHUB_GH_TOKEN=' ${DEVOPS_ACCOUNT_DIR}/account_github.env; grep -E '^NUGET_API_KEY=' ${DEVOPS_ACCOUNT_DIR}/account_nuget.env))
	@make release $(info)

spc: ## Checks if the Release Branch and Tag already exist
	$(eval filtered_branches:= $(shell git branch --all | grep -E "(^|[ /])release/$(subst .,\.,${ONDEWO_NLU_VERSION})$$"))
	$(eval filtered_tags:= $(shell git tag --list | grep -Fx "${ONDEWO_NLU_VERSION}"))
	@if test "$(filtered_branches)" != ""; then echo "-- Test 1: Branch exists!!" && exit 1; else echo "-- Test 1: Branch is fine";fi
	@if test "$(filtered_tags)" != ""; then echo "-- Test 2: Tag exists!!" && exit 1; else echo "-- Test 2: Tag is fine";fi
