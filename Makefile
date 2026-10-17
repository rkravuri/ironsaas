# Foundation developer commands. Run `make` to list them.
# Every target calls the real tool, so every command can also be typed by hand.
.DEFAULT_GOAL := help
CONFIG ?= Release

.PHONY: help build test check format clean

help: ## Show this list
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-10s %s\n", $$1, $$2}'

build: ## Build every project (warnings are errors)
	dotnet build Foundation.slnx --configuration $(CONFIG)

test: ## Run every test project (Microsoft Testing Platform, see global.json)
	dotnet test --solution Foundation.slnx --configuration $(CONFIG)

check: ## Run only the architecture checks (module boundaries, layout)
	dotnet test --project tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj --configuration $(CONFIG)

format: ## Apply the code style from .editorconfig
	dotnet format Foundation.slnx

clean: ## Remove build output
	dotnet clean Foundation.slnx
	find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
