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
#   make test                  # compile the generated library and run the test suite
#
# Versioning: ONDEWO_NLU_VERSION (below) is the single source of truth. It MUST
# match the ONDEWO NLU API in major and minor version. It is handed to MSBuild as
# the $(OndewoPackageVersion) property, so no file has to be rewritten when it changes - only
# this Makefile and RELEASE.md.
#
# Overriding variables: pass on the command line, e.g. `make build ONDEWO_NLU_VERSION=1.2.3`,
# or export in the environment. Credentials (GITHUB_GH_TOKEN, NUGET_API_KEY) are only ever read
# at runtime and must never be committed.
# =====================================================================================

# ---------------- BEFORE RELEASE ----------------
# 1 - Update Version Number (ONDEWO_NLU_VERSION)
# 2 - Update RELEASE.md
# 3 - make build
# -------------- Release Process Steps --------------
# 1 - Get Credentials from devops-accounts repo
# 2 - Create Release Branch and push
# 3 - Create Release Tag and push
# 4 - GitHub Release
# 5 - NuGet Release

########################################################
# 		Variables
########################################################

# MUST BE THE SAME AS THE API in Major and Minor Version Number
# example: API 1.2.0 --> Client 1.2.X
ONDEWO_NLU_VERSION=7.1.0

# Submodule pins. Both are checked out by `make checkout_defined_submodule_versions`, so the
# generated code is always reproducible from this file alone.
ONDEWO_NLU_API_GIT_BRANCH=tags/7.1.0
# PROVISIONAL PIN. The six compiled-language targets this client depends on landed on the
# branch below; ondewo-proto-compiler 5.15.0 is not tagged yet. Once that release is cut this
# becomes tags/5.15.0 - `make release_update_proto_compiler_dependency_<lang>` in the compiler
# repo rewrites it, so do not hand-edit it then.
ONDEWO_PROTO_COMPILER_GIT_BRANCH=feature/rust-php-go-cpp-java-csharp

# You need to set up an access token at https://github.com/settings/tokens - permissions are important
GITHUB_GH_TOKEN?=ENTER_YOUR_TOKEN_HERE
# You need to set up an API key at https://www.nuget.org/account/apikeys
NUGET_API_KEY?=ENTER_YOUR_NUGET_API_KEY_HERE
NUGET_SOURCE?=https://api.nuget.org/v3/index.json

# Terminate the release-notes slice on the ***** separator that delimits release entries, NOT on
# /\*\*/ - that matches the first markdown **bold** span inside the entry and silently truncates
# the notes there, with no error from `gh release create`.
CURRENT_RELEASE_NOTES=`cat RELEASE.md \
	| perl -ne 'print if /Release ONDEWO NLU Csharp Client ${ONDEWO_NLU_VERSION}/../^\*{5}/'`

GH_REPO="https://github.com/ondewo/ondewo-nlu-client-csharp"
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

# --- MSBuild properties read by the generated project file
# The generated <PackageId>.csproj deliberately carries no literal version: it reads
# $(OndewoPackageId), $(OndewoPackageVersion), $(OndewoTargetFramework), $(GoogleProtobufVersion),
# $(GrpcDotnetVersion) and $(GoogleApiCommonProtosVersion). Inside the compiler image those come
# from its Dockerfile ARG lines; on the host they are read back OUT of that same pinned Dockerfile
# here, so a host build can never drift from the image. `export` (top of this file) hands every
# one of them to MSBuild, which reads environment variables as properties.
OndewoPackageId=Ondewo.NLU.Client
OndewoPackageVersion=${ONDEWO_NLU_VERSION}
# Keeps the .proto submodule out of the SDK's default Compile glob on a host build.
OndewoProtosDir=${ONDEWO_API_DIR}
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
	@echo "NuGet package id:     ${OndewoPackageId}"
	@echo "Target framework:     ${OndewoTargetFramework}"
	@echo "Google.Protobuf:      ${GoogleProtobufVersion}"
	@echo "Grpc.Net.Client:      ${GrpcDotnetVersion}"
	@echo "Google.Api.CommonProtos: ${GoogleApiCommonProtosVersion}"
	@echo "GITHUB_GH_TOKEN set:  $(if $(filter-out ENTER_YOUR_TOKEN_HERE,$(GITHUB_GH_TOKEN)),yes,no)"
	@echo "NUGET_API_KEY set:    $(if $(filter-out ENTER_YOUR_NUGET_API_KEY_HERE,$(NUGET_API_KEY)),yes,no)"
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
generate_ondewo_protos: ## Generate the csharp gRPC client stubs and the NuGet package from the API protos
	@test -d ${ONDEWO_PROTOS_DIR} || { \
		echo "$(RED)[ERROR]$(NC) ${ONDEWO_PROTOS_DIR} is missing - run 'make update_submodules' first"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) Generating csharp stubs from ${ONDEWO_PROTOS_DIR} into api/ ..."
	docker run --rm \
		-e OndewoPackageVersion=${ONDEWO_NLU_VERSION} \
		-v ${shell pwd}:/input-volume \
		-v ${shell pwd}:/output-volume \
		${PROTO_COMPILER_IMAGE} "${ONDEWO_API_DIR}" "${ONDEWO_PROTOS_SUBDIR}" "${OndewoPackageId}"
	@$(MAKE) fix_generated_file_ownership
	@echo "$(GREEN)[SUCCESS]$(NC) Generated api/, ${OndewoPackageId}.csproj, artifacts/ and nupkg/"

# The csharp image writes under root-owned /image-data, so - unlike the python target - its
# container must NOT run with --user, and everything it copies to the output volume lands owned
# by root. Only the paths the image owns are touched, never the whole working tree.
fix_generated_file_ownership: ## Take back ownership of the files the compiler container wrote as root
	@for path in api artifacts nupkg ${OndewoPackageId}.csproj ; do \
		if [ -e "$$path" ] && [ "$$(ls -ld "$$path" | awk '{print $$3}')" != "$$(id -un)" ]; then \
			echo "$(BLUE)[INFO]$(NC) Taking ownership of $$path ..." ; \
			sudo chown -R "$$(id -un):$$(id -gn)" "$$path" || exit 1 ; \
		fi ; \
	done

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

check_dotnet_properties: ## Fail early when the MSBuild pins cannot be read from the pinned compiler submodule
	@if [ -z "${OndewoTargetFramework}" ] || [ -z "${GoogleProtobufVersion}" ] || \
	    [ -z "${GrpcDotnetVersion}" ] || [ -z "${GoogleApiCommonProtosVersion}" ]; then \
		echo "$(RED)[ERROR]$(NC) could not read the MSBuild pins from ${PROTO_COMPILER_DOCKERFILE}." ; \
		echo "        The generated project file has no literal versions - it reads them as MSBuild" ; \
		echo "        properties. Run 'make update_submodules' so they can be read from the pinned" ; \
		echo "        ondewo-proto-compiler submodule." ; \
		exit 1 ; \
	fi

build_library: check_dotnet_properties ## Compile the generated library on the host (no docker)
	@test -f ${OndewoPackageId}.csproj || { \
		echo "$(RED)[ERROR]$(NC) ${OndewoPackageId}.csproj is missing - run 'make build' first"; \
		exit 1; \
	}
	dotnet build ${OndewoPackageId}.csproj -c Release

test: build_library ## Compile the generated library and run the csharp test suite
	@projects=$$(find tests -type f -name '*.csproj' 2>/dev/null) ; \
	if [ -z "$$projects" ]; then \
		echo "$(YELLOW)[NOOP]$(NC) no test project under tests/ - only the generated library was compiled" ; \
	else \
		for project in $$projects ; do \
			echo "$(BLUE)[INFO]$(NC) dotnet test $$project" ; \
			dotnet test "$$project" -c Release || exit 1 ; \
		done ; \
		echo "$(GREEN)[SUCCESS]$(NC) test suite passed" ; \
	fi

pack: build_library ## Pack the NuGet package on the host into nupkg/
	dotnet pack ${OndewoPackageId}.csproj -c Release --no-build -o nupkg

clean: ## Remove the generated stubs, the build output and the packed packages
	rm -rf api artifacts nupkg bin obj build_check.txt

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

release: ## Automate the entire release process
	@echo "$(BLUE)[INFO]$(NC) Start release ${ONDEWO_NLU_VERSION}"
	make build
	-make precommit_hooks_run_all_files
	git status
	make check_build
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
	-git commit --no-verify -m "Preparing for release ${ONDEWO_NLU_VERSION}"
	git push
	make create_release_branch
	make create_release_tag
	make push_to_gh
	make push_to_nuget
	@echo "$(GREEN)[SUCCESS]$(NC) Release finished"

create_release_branch: ## Create Release Branch and push it to origin
	git checkout -b "release/${ONDEWO_NLU_VERSION}"
	git push -u origin "release/${ONDEWO_NLU_VERSION}"

create_release_tag: ## Create Release Tag and push it to origin
	git tag -a ${ONDEWO_NLU_VERSION} -m "release/${ONDEWO_NLU_VERSION}"
	git push origin ${ONDEWO_NLU_VERSION}

login_to_gh: ## Login to Github CLI with Access Token
	@if [ -z "${GITHUB_GH_TOKEN}" ] || [ "${GITHUB_GH_TOKEN}" = "ENTER_YOUR_TOKEN_HERE" ]; then \
		echo "$(RED)[ERROR]$(NC) GITHUB_GH_TOKEN is not set - create one at https://github.com/settings/tokens"; \
		exit 1; \
	fi
	@echo "${GITHUB_GH_TOKEN}" | gh auth login -p ssh --with-token

build_gh_release: ## Generate Github Release with CLI
	gh release create --repo $(GH_REPO) "$(ONDEWO_NLU_VERSION)" -n "$(CURRENT_RELEASE_NOTES)" -t "Release ${ONDEWO_NLU_VERSION}"

push_to_gh: login_to_gh build_gh_release ## Logs into GitHub CLI and releases
	@echo 'Released to Github'

########################################################
#		NUGET

# `@`-prefixed so the API key never reaches the build log.
push_to_nuget: ## Publish the packed NuGet package to nuget.org
	@if [ -z "${NUGET_API_KEY}" ] || [ "${NUGET_API_KEY}" = "ENTER_YOUR_NUGET_API_KEY_HERE" ]; then \
		echo "$(RED)[ERROR]$(NC) NUGET_API_KEY is not set - create one at https://www.nuget.org/account/apikeys"; \
		exit 1; \
	fi
	@test -f "nupkg/${OndewoPackageId}.${ONDEWO_NLU_VERSION}.nupkg" || { \
		echo "$(RED)[ERROR]$(NC) nupkg/${OndewoPackageId}.${ONDEWO_NLU_VERSION}.nupkg is missing - run 'make build' first"; \
		exit 1; \
	}
	@echo "$(BLUE)[INFO]$(NC) Pushing ${OndewoPackageId} ${ONDEWO_NLU_VERSION} to ${NUGET_SOURCE} ..."
	@dotnet nuget push "nupkg/${OndewoPackageId}.${ONDEWO_NLU_VERSION}.nupkg" \
		--api-key ${NUGET_API_KEY} \
		--source ${NUGET_SOURCE} \
		--skip-duplicate
	@echo "$(GREEN)[SUCCESS]$(NC) Released to NuGet"

########################################################
#		DEVOPS-ACCOUNTS

ondewo_release: spc clone_devops_accounts run_release_with_devops ## Release with credentials from devops-accounts repo
	@rm -rf ${DEVOPS_ACCOUNT_GIT}

clone_devops_accounts: ## Clones devops-accounts repo
	if [ -d $(DEVOPS_ACCOUNT_GIT) ]; then rm -Rf $(DEVOPS_ACCOUNT_GIT); fi
	git clone git@bitbucket.org:ondewo/${DEVOPS_ACCOUNT_GIT}.git

run_release_with_devops: ## Read credentials from the cloned devops-accounts repo and run the full release
	$(eval info:= $(shell cat ${DEVOPS_ACCOUNT_DIR}/account_github.env | grep GITHUB_GH & cat ${DEVOPS_ACCOUNT_DIR}/account_nuget.env | grep NUGET_API_KEY))
	@make release $(info)

spc: ## Checks if the Release Branch and Tag already exist
	$(eval filtered_branches:= $(shell git branch --all | grep -E "(^|[ /])release/$(subst .,\.,${ONDEWO_NLU_VERSION})$$"))
	$(eval filtered_tags:= $(shell git tag --list | grep -Fx "${ONDEWO_NLU_VERSION}"))
	@if test "$(filtered_branches)" != ""; then echo "-- Test 1: Branch exists!!" && exit 1; else echo "-- Test 1: Branch is fine";fi
	@if test "$(filtered_tags)" != ""; then echo "-- Test 2: Tag exists!!" && exit 1; else echo "-- Test 2: Tag is fine";fi
