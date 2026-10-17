# Foundation developer commands. Run `make` to list them.
# Every target calls the real tool, so every command can also be typed by hand.
.DEFAULT_GOAL := help
CONFIG ?= Release
TENANTS ?= acme bravo
SERVICE ?=

COMPOSE_DIR := infra/compose
COMPOSE := docker compose --file $(COMPOSE_DIR)/compose.yaml --env-file $(COMPOSE_DIR)/.env
ALL_PROFILES := --profile telemetry --profile ci

.PHONY: help build test check format clean env up up-slim up-ci down reset ps logs certs hosts

help: ## Show this list
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-10s %s\n", $$1, $$2}'

# ---------------------------------------------------------------- code
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

# ---------------------------------------------------------------- laptop platform (infra/compose)
env: ## Create infra/compose/.env with random local credentials (runs once)
	@sh $(COMPOSE_DIR)/scripts/init-env.sh

up: env ## Start the platform with the telemetry stack and wait until everything is healthy
	$(COMPOSE) --profile telemetry up --detach --wait
	@sh $(COMPOSE_DIR)/scripts/wait-ready.sh tempo loki
	@sh $(COMPOSE_DIR)/scripts/print-endpoints.sh full

up-slim: env ## Start the platform without the telemetry stack
	$(COMPOSE) up --detach --wait
	@sh $(COMPOSE_DIR)/scripts/print-endpoints.sh slim

up-ci: env ## Start the slim platform plus the collector and Tempo (what the pipeline uses)
	$(COMPOSE) --profile ci up --detach --wait
	@sh $(COMPOSE_DIR)/scripts/wait-ready.sh tempo

down: env ## Stop and remove the platform containers; data volumes are kept
	$(COMPOSE) $(ALL_PROFILES) down

reset: env ## Stop the platform and delete all its data volumes
	$(COMPOSE) $(ALL_PROFILES) down --volumes

ps: env ## Show the platform containers
	$(COMPOSE) $(ALL_PROFILES) ps

logs: env ## Follow platform logs (SERVICE=name for one service)
	$(COMPOSE) $(ALL_PROFILES) logs --follow --tail=100 $(SERVICE)

certs: ## Create a locally trusted TLS certificate with mkcert; optional (TENANTS="acme bravo")
	@sh $(COMPOSE_DIR)/scripts/make-certs.sh $(TENANTS)

hosts: ## Print the /etc/hosts line for command line tools (TENANTS="acme bravo")
	@sh $(COMPOSE_DIR)/scripts/hosts-line.sh $(TENANTS)
