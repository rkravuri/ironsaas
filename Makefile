# Foundation developer commands. Run `make` to list them.
# Every target calls the real tool, so every command can also be typed by hand.
.DEFAULT_GOAL := help
CONFIG ?= Release
TENANTS ?= acme bravo
SERVICE ?=

COMPOSE_DIR := infra/compose
COMPOSE := docker compose --file $(COMPOSE_DIR)/compose.yaml --env-file $(COMPOSE_DIR)/.env
ALL_PROFILES := --profile telemetry --profile ci

.PHONY: help build test check format clean run-api run-worker migrate seed token secret-dev run-api-secrets secret-control migrate-control seed-control run-control run-fleet provision deprovision images ef-add ef-script env up up-slim up-ci down reset ps logs certs hosts

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

DEV_DATABASE = ConnectionStrings__Database="Host=localhost;Port=5432;Database=postgres;Username=$$POSTGRES_SUPERUSER;Password=$$POSTGRES_SUPERUSER_PASSWORD"
DEV_STORAGE = Storage__ConnectionString="DefaultEndpointsProtocol=http;AccountName=$$BLOB_ACCOUNT_NAME;AccountKey=$$BLOB_ACCOUNT_KEY;BlobEndpoint=http://localhost:10000/$$BLOB_ACCOUNT_NAME;" Storage__Container=dev
DEV_TENANT = Tenant__Id=dev Tenant__Hostnames__0=localhost Tenant__Hostnames__1=dev.localhost Tokens__SigningKeyPath=$(COMPOSE_DIR)/.dev-signing-key.pem
DEV_SMOKE_SECRET = dev-smoke-client-secret-not-for-real-tenants
DEV_OPERATOR_SECRET = dev-operator-secret-not-for-real-deployments
CONTROL_URL ?= http://localhost:5200
CONTROL_TENANT = Tenant__Id=control Tenant__Hostnames__0=localhost Tenant__Hostnames__1=control.localhost
CONTROL_SECRETS = Secrets__Address=http://localhost:8200 Secrets__Token=$$OPENBAO_ROOT_TOKEN Secrets__Path=control
FLEET_LAPTOP = Fleet__Units__Runtime=compose Fleet__Units__ComposeDirectory=$(COMPOSE_DIR)/units Fleet__Units__IngressUrl=http://localhost Fleet__Units__ImageTag=$(TAG) \
	Fleet__Database__UnitHost=postgres Fleet__Storage__UnitBlobEndpoint=http://blob:10000/$$BLOB_ACCOUNT_NAME Fleet__Secrets__UnitAddress=http://openbao:8200 Fleet__PollSeconds=3
TAG ?= dev

token: ## Get an access token for the laptop smoke client from the running API host
	@curl -s -X POST http://localhost:5100/connect/token -u smoke-client:$(DEV_SMOKE_SECRET) -d grant_type=client_credentials

run-api: env ## Run the tenant API host on the laptop against the platform (http://localhost:5100)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5100 ASPNETCORE_ENVIRONMENT=Development $(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) \
	dotnet run --project src/Iron.Host.Api

run-worker: env ## Run the tenant worker host on the laptop against the platform (http://localhost:5101)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5101 ASPNETCORE_ENVIRONMENT=Development $(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) \
	Heartbeat__Interval=00:00:15 dotnet run --project src/Iron.Host.Worker

migrate: env ## Run the tenant API image command "migrate" against the laptop database (dotnet run)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) dotnet run --project src/Iron.Host.Api -- migrate

ef-add: ## Add a migration for a module context: make ef-add MODULE=Authorization NAME=Initial
	@test -n "$(MODULE)" && test -n "$(NAME)" || (echo "usage: make ef-add MODULE=Authorization NAME=Initial" && exit 1)
	dotnet tool restore
	dotnet ef migrations add $(NAME) --project src/Iron.$(MODULE) --startup-project src/Iron.$(MODULE) --context $(MODULE)DbContext --output-dir Data/Migrations

ef-script: ## Print the SQL of a module's migrations: make ef-script MODULE=Authorization
	dotnet tool restore
	dotnet ef migrations script --project src/Iron.$(MODULE) --startup-project src/Iron.$(MODULE) --context $(MODULE)DbContext --idempotent

seed: env ## Run the tenant API image command "seed" against the laptop database (first administrator admin@dev.localhost)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) Seed__Administrator__LoginId=admin@dev.localhost Seed__Administrator__DisplayName="Dev Administrator" \
	Seed__ServiceAccounts__0__ClientId=smoke-client Seed__ServiceAccounts__0__Secret=$(DEV_SMOKE_SECRET) \
	dotnet run --project src/Iron.Host.Api -- seed

secret-control: env ## Write the control plane's unit secret to OpenBao (control database, fleet admin connections, signing key)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; $(COMPOSE_DIR)/scripts/write-control-secret.sh

migrate-control: env ## Migrate the control plane database (created on first run); needs make secret-control
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(CONTROL_TENANT) $(CONTROL_SECRETS) dotnet run --project src/Iron.Host.ControlPlane -- migrate

seed-control: env ## Seed the control plane: system roles and the operator service account
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(CONTROL_TENANT) $(CONTROL_SECRETS) \
	Seed__ServiceAccounts__0__ClientId=operator Seed__ServiceAccounts__0__Secret=$(DEV_OPERATOR_SECRET) Seed__ServiceAccounts__0__Role=tenant-administrator Seed__ServiceAccounts__0__DisplayName="Local operator" \
	dotnet run --project src/Iron.Host.ControlPlane -- seed

run-control: env ## Run the control plane API on the laptop (http://localhost:5200)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5200 ASPNETCORE_ENVIRONMENT=Development $(CONTROL_TENANT) $(CONTROL_SECRETS) \
	dotnet run --project src/Iron.Host.ControlPlane

run-fleet: env ## Run the fleet worker on the laptop with the Compose runtime (it drives docker on this machine)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5201 ASPNETCORE_ENVIRONMENT=Development $(CONTROL_TENANT) $(CONTROL_SECRETS) $(FLEET_LAPTOP) Heartbeat__Interval=00:01:00 \
	dotnet run --project src/Iron.Host.Fleet

deprovision: ## Ask the fleet to take a tenant's unit down (data is kept): make deprovision TENANT=acme
	@test -n "$(TENANT)" || (echo "usage: make deprovision TENANT=acme" && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) ACTION=deprovision $(COMPOSE_DIR)/scripts/provision.sh $(TENANT) -

provision: ## Register a tenant in the control plane and request its provisioning: make provision TENANT=acme [ADMIN=admin@acme.localhost]
	@test -n "$(TENANT)" || (echo "usage: make provision TENANT=acme [ADMIN=admin@acme.localhost]" && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) $(COMPOSE_DIR)/scripts/provision.sh $(TENANT) $(or $(ADMIN),admin@$(TENANT).localhost)

secret-dev: env ## Write the dev unit's secret to OpenBao (connection string, storage, signing key) as the fleet will per tenant
	@set -a; . $(COMPOSE_DIR)/.env; set +a; $(COMPOSE_DIR)/scripts/write-dev-secret.sh

run-api-secrets: env ## Run the tenant API host with only the store address, the unit token and the tenant in its environment
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5100 ASPNETCORE_ENVIRONMENT=Development \
	Tenant__Id=dev Tenant__Hostnames__0=localhost Tenant__Hostnames__1=dev.localhost \
	Secrets__Address=http://localhost:8200 Secrets__Token=$$OPENBAO_ROOT_TOKEN Secrets__Path=tenants/dev \
	dotnet run --project src/Iron.Host.Api

images: ## Build the four host images (foundation/iron-*:$(TAG))
	docker build -f src/Iron.Host.Api/Dockerfile -t foundation/iron-api:$(TAG) .
	docker build -f src/Iron.Host.Worker/Dockerfile -t foundation/iron-worker:$(TAG) .
	docker build -f src/Iron.Host.ControlPlane/Dockerfile -t foundation/iron-control-plane:$(TAG) .
	docker build -f src/Iron.Host.Fleet/Dockerfile -t foundation/iron-fleet:$(TAG) .

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
