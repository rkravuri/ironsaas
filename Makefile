# Foundation developer commands. Run `make` to list them.
# Every target calls the real tool, so every command can also be typed by hand.
.DEFAULT_GOAL := help
CONFIG ?= Release
TENANTS ?= acme bravo
SERVICE ?=

COMPOSE_DIR := infra/compose
COMPOSE := docker compose --file $(COMPOSE_DIR)/compose.yaml --env-file $(COMPOSE_DIR)/.env
ALL_PROFILES := --profile telemetry --profile ci --profile control --profile sso

.PHONY: help build test check prd-check prd-csv format format-check trace verify catalogue apply-plan flag kill-switch clean run-api run-worker migrate seed token secret-dev run-api-secrets secret-control migrate-control seed-control run-control run-fleet provision update-unit deprovision recover-admin smoke e2e test-provider break-glass-request break-glass-approve break-glass-link break-glass-end break-glass-show impersonate platform-document accept-documents verify-audit audit-chain audit-accept-restore up-control down-control integration shell-node shell-install shell-dev shell-check images ef-add ef-script env up up-slim up-ci down reset ps logs certs hosts shell-a11y installation-record validation-pack

help: ## Show this list
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-10s %s\n", $$1, $$2}'

# ---------------------------------------------------------------- code
build: ## Build every project (warnings are errors)
	dotnet build Foundation.slnx --configuration $(CONFIG)

# One PostgreSQL, one Azurite and one OpenBao for the whole run, each project in its own database, containers and mount
# (tests/test-services.sh, tests/Iron.Testing), and at most four test projects at once. Every project at once (one per
# CPU thread), each with containers of its own, started some forty containers beside the stack, and a starved thread
# failed Testcontainers' five-second image name check before any test ran. The services go whatever the result.
TEST_PARALLEL ?= 4
TEST_ARGS ?=

# Every test run leaves its result files under artifacts/test-results (not committed), one folder per kind, which make
# trace joins to the tests and what they prove (ADR-0044). A kind's folder holds its last run only.
RESULTS := $(CURDIR)/artifacts/test-results

test: ## Run every test project (Microsoft Testing Platform, see global.json) on the run's three services, four at a time; TRX into artifacts/test-results/dotnet
	@rm -rf $(RESULTS)/dotnet; \
	services=$$(sh tests/test-services.sh up) || { sh tests/test-services.sh down; exit 1; }; \
	eval "$$services"; \
	dotnet test --solution Foundation.slnx --configuration $(CONFIG) --max-parallel-test-modules $(TEST_PARALLEL) \
	  --results-directory $(RESULTS)/dotnet --report-xunit-trx $(TEST_ARGS); \
	status=$$?; sh tests/test-services.sh down; exit $$status

check: ## Run only the architecture checks (module boundaries, layout)
	dotnet test --project tests/Iron.Architecture.Tests/Iron.Architecture.Tests.csproj --configuration $(CONFIG)

# The requirement documents in docs/prd (ADR-0041): every identifier once, every FF identifier in the map, criteria in
# the Given, When, Then form with an error case, the product PRDs' sections, and each CSV equal to its markdown; the
# process reference packs' section 2 and their BPMN maps against the BPMN 2.0 schema (ADR-0045). The maps' check is
# .NET, whose schema validation needs nothing installed; its own tests run with make test.
prd-check: ## Check the requirement documents, their CSVs, the map and the packs' BPMN maps (ADR-0041, ADR-0045); the check's own tests first
	python3 tools/prd_check_test.py --junit $(RESULTS)/python/prd-check.xml
	python3 tools/prd_check.py
	dotnet run --project tools/Iron.ProcessMaps --configuration $(CONFIG) -- docs/prd

prd-csv: ## Write every requirement document's CSV, and the map's, from its markdown
	python3 tools/prd_check.py --write-csv

format: shell-node ## Apply the code style: dotnet format from .editorconfig, Prettier in the shell (ADR-0040)
	dotnet format Foundation.slnx
	cd shell && npm run format --silent

format-check: shell-node ## Check the code style without changing a file, as CI does: dotnet format and Prettier
	dotnet format Foundation.slnx --verify-no-changes
	cd shell && npm run format:check --silent

# The traceability matrix (TF-218, ADR-0044): every test's identifiers read from the sources, its result from the last
# run's files under artifacts/test-results, the matrix written to artifacts/trace. It fails on an identifier no document
# holds, and on a criterion of a done slice without a test that docs/trace/exceptions.md does not list.
trace: ## Build the traceability matrix into artifacts/trace from the last results (ADR-0044); its own tests and the validation pack's first
	python3 tools/trace_test.py --junit $(RESULTS)/python/trace.xml
	python3 tools/validation_pack_test.py --junit $(RESULTS)/python/validation-pack.xml
	python3 tools/installation_record_test.py --junit $(RESULTS)/python/installation-record.xml
	python3 tools/trace.py

VALIDATION := $(CURDIR)/artifacts/validation

installation-record: env ## Record the installation of a laptop tenant from the local images and check it against the code (TF-218, ADR-0052): make installation-record TENANT=echo, after make smoke and make e2e
	python3 tools/installation_record.py --tenant $(TEST_TENANT) --repository foundation --tag $(TAG) --version development \
	  --smoke-report $(RESULTS)/smoke/smoke-report.json --e2e-junit $(RESULTS)/e2e/junit.xml --output $(VALIDATION)/installation-record.json

validation-pack: env ## Build the development validation pack from the last runs and the installation record, and publish it where the next make update-unit places it (ADR-0052)
	@status=0; python3 tools/validation_pack.py --version development --installation $(VALIDATION)/installation-record.json --output $(VALIDATION) || status=$$?; \
	test -f $(VALIDATION)/validation-pack-development.zip || exit 1; \
	IMAGE_TAG=$(TAG) $(COMPOSE) --profile control run --rm --no-deps --volume $(VALIDATION):/packs:ro fleet publish-validation-pack development /packs/validation-pack-development.zip || exit 1; \
	exit $$status

# Every check in order, each on its own, so the shell's tests never run beside make test (ADR-0044): the requirement
# documents, the .NET suite, the shell (lint, format, tests, build), the accessibility scan, the format check, and the
# matrix from what those runs wrote. With the stack running it is the check before a push.
verify: ## Run prd-check, test, shell-check, shell-a11y, the format check and trace, in that order, stopping at the first failure
	$(MAKE) prd-check
	$(MAKE) test
	$(MAKE) shell-check
	$(MAKE) shell-a11y
	$(MAKE) format-check
	$(MAKE) trace

CATALOGUE_FILE := src/Kernel/Iron.Kernel.Tenancy/Services/licensing-catalogue.json
SETTING_KEYS_FILE := shell/src/api/setting-keys.ts

catalogue: ## Write what the units can license, switch and set to the file the control plane carries, and the shell's typed setting keys (ADR-0034, ADR-0036)
	dotnet run --project src/Hosts/Iron.Host.Api --configuration $(CONFIG) -- catalogue $(CURDIR)/$(CATALOGUE_FILE) $(CURDIR)/$(SETTING_KEYS_FILE)

clean: ## Remove build output (the .NET folders only; shell/node_modules has bin folders of its own)
	dotnet clean Foundation.slnx
	find src tests tools -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

DEV_DATABASE = ConnectionStrings__Database="Host=localhost;Port=5432;Database=postgres;Username=$$POSTGRES_SUPERUSER;Password=$$POSTGRES_SUPERUSER_PASSWORD"
DEV_STORAGE = Storage__ConnectionString="DefaultEndpointsProtocol=http;AccountName=$$BLOB_ACCOUNT_NAME;AccountKey=$$BLOB_ACCOUNT_KEY;BlobEndpoint=http://localhost:10000/$$BLOB_ACCOUNT_NAME;" Storage__Container=dev
DEV_TENANT = Tenant__Id=dev Tenant__Hostnames__0=localhost Tenant__Hostnames__1=dev.localhost Tokens__SigningKeyPath=$(COMPOSE_DIR)/.dev-signing-key.pem
DEV_SMOKE_SECRET = dev-smoke-client-secret-not-for-real-tenants
DEV_OPERATOR_SECRET = dev-operator-secret-not-for-real-deployments
DEV_SMOKE_READER_SECRET = dev-smoke-reader-secret-change-me
# A second operator, so break-glass has someone to approve it who is not the one who asked (TF-026, ADR-0049).
DEV_SECOND_OPERATOR_SECRET = dev-second-operator-secret-not-for-real-deployments
CONTROL_URL ?= http://localhost:5200
CONTROL_TENANT = Tenant__Id=control Tenant__Hostnames__0=localhost Tenant__Hostnames__1=control.localhost
CONTROL_SECRETS = Secrets__Address=http://localhost:8200 Secrets__Token=$$OPENBAO_ROOT_TOKEN Secrets__Path=control
FLEET_LAPTOP = Fleet__Units__Runtime=compose Fleet__Units__ComposeDirectory=$(CURDIR)/$(COMPOSE_DIR)/units Fleet__Units__IngressUrl=http://localhost Fleet__Units__ImageTag=$(TAG) \
	Fleet__Database__UnitHost=postgres Fleet__Storage__UnitBlobEndpoint=http://blob:10000/$$BLOB_ACCOUNT_NAME Fleet__Secrets__UnitAddress=http://openbao:8200 Fleet__PollSeconds=3
TAG ?= dev

token: ## Get an access token for the laptop smoke client from the running API host
	@curl -s -X POST http://localhost:5100/connect/token -u smoke-client:$(DEV_SMOKE_SECRET) -d grant_type=client_credentials

run-api: env ## Run the tenant API host on the laptop against the platform (http://localhost:5100)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5100 ASPNETCORE_ENVIRONMENT=Development $(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) \
	dotnet run --project src/Hosts/Iron.Host.Api

run-worker: env ## Run the tenant worker host on the laptop against the platform (http://localhost:5101)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5101 ASPNETCORE_ENVIRONMENT=Development $(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) \
	Heartbeat__Interval=00:00:15 dotnet run --project src/Hosts/Iron.Host.Worker

migrate: env ## Run the tenant API image command "migrate" against the laptop database (dotnet run)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) dotnet run --project src/Hosts/Iron.Host.Api -- migrate

# A module is named by the last segment of its project name, whatever its layer or suite (ADR-0029, ADR-0043):
# MODULE=Identity is src/Kernel/Iron.Kernel.Identity, MODULE=Orders is src/Suites/Procurement/Iron.Suites.Procurement.Orders.
# Hosts are not modules. A segment that names no module, or more than one, is refused.
MODULE_DIR = $(patsubst %/,%,$(dir $(wildcard src/Kernel/Iron.Kernel.$(MODULE)/Iron.Kernel.$(MODULE).csproj src/Business/Iron.Business.$(MODULE)/Iron.Business.$(MODULE).csproj src/Suites/*/Iron.Suites.*.$(MODULE)/Iron.Suites.*.$(MODULE).csproj)))
MODULE_CHECK = test -n "$(MODULE)" && test $(words $(MODULE_DIR)) -eq 1 || (echo "MODULE=$(MODULE) must be the last segment of exactly one module's name (Identity for Iron.Kernel.Identity); found: $(or $(MODULE_DIR),none)" && exit 1)

ef-add: ## Add a migration for a module context: make ef-add MODULE=Authorization NAME=Initial
	@test -n "$(MODULE)" && test -n "$(NAME)" || (echo "usage: make ef-add MODULE=Authorization NAME=Initial" && exit 1)
	@$(MODULE_CHECK)
	dotnet tool restore
	dotnet ef migrations add $(NAME) --project $(MODULE_DIR) --startup-project $(MODULE_DIR) --context $(MODULE)DbContext --output-dir Data/Migrations

ef-script: ## Print the SQL of a module's migrations: make ef-script MODULE=Authorization
	@$(MODULE_CHECK)
	dotnet tool restore
	dotnet ef migrations script --project $(MODULE_DIR) --startup-project $(MODULE_DIR) --context $(MODULE)DbContext --idempotent

seed: env ## Run the tenant API image command "seed" against the laptop database (first administrator admin@dev.localhost)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(DEV_TENANT) $(DEV_DATABASE) $(DEV_STORAGE) Seed__Administrator__LoginId=admin@dev.localhost Seed__Administrator__DisplayName="Dev Administrator" \
	Seed__ServiceAccounts__0__ClientId=smoke-client Seed__ServiceAccounts__0__Secret=$(DEV_SMOKE_SECRET) \
	dotnet run --project src/Hosts/Iron.Host.Api -- seed

secret-control: env ## Write the control plane's unit secret to OpenBao (control database, fleet admin connections, signing key)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; $(COMPOSE_DIR)/scripts/write-control-secret.sh

migrate-control: env ## Migrate the control plane database (created on first run); needs make secret-control
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(CONTROL_TENANT) $(CONTROL_SECRETS) dotnet run --project src/Hosts/Iron.Host.ControlPlane -- migrate

seed-control: env ## Seed the control plane: system roles, the operator service account and a second operator for break-glass
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	$(CONTROL_TENANT) $(CONTROL_SECRETS) \
	Seed__ServiceAccounts__0__ClientId=operator Seed__ServiceAccounts__0__Secret=$(DEV_OPERATOR_SECRET) Seed__ServiceAccounts__0__Role=tenant-administrator Seed__ServiceAccounts__0__DisplayName="Local operator" \
	Seed__ServiceAccounts__1__ClientId=smoke-reader Seed__ServiceAccounts__1__Secret=$(DEV_SMOKE_READER_SECRET) Seed__ServiceAccounts__1__Role=service-default Seed__ServiceAccounts__1__DisplayName="Smoke reader (denial case)" \
	Seed__ServiceAccounts__2__ClientId=operator-two Seed__ServiceAccounts__2__Secret=$(DEV_SECOND_OPERATOR_SECRET) Seed__ServiceAccounts__2__Role=tenant-administrator Seed__ServiceAccounts__2__DisplayName="Second local operator" \
	dotnet run --project src/Hosts/Iron.Host.ControlPlane -- seed

run-control: env ## Run the control plane API on the laptop (http://localhost:5200)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5200 ASPNETCORE_ENVIRONMENT=Development $(CONTROL_TENANT) $(CONTROL_SECRETS) \
	dotnet run --project src/Hosts/Iron.Host.ControlPlane

run-fleet: env ## Run the fleet worker on the laptop with the Compose runtime (it drives docker on this machine)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	ASPNETCORE_URLS=http://localhost:5201 ASPNETCORE_ENVIRONMENT=Development $(CONTROL_TENANT) $(CONTROL_SECRETS) $(FLEET_LAPTOP) Heartbeat__Interval=00:01:00 \
	dotnet run --project src/Hosts/Iron.Host.Fleet

update-unit: ## Ask the fleet to re-render and re-apply an active tenant's unit (new images, new template): make update-unit TENANT=acme
	@test -n "$(TENANT)" || (echo "usage: make update-unit TENANT=acme" && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) ACTION=update $(COMPOSE_DIR)/scripts/provision.sh $(TENANT) -

recover-admin: ## Ask the fleet for a one-time, 15-minute recovery link for a tenant's administrator, with a reason both audit trails keep (ADR-0046), and RESET_FACTORS=1 to reset their second factors too (ADR-0047): make recover-admin TENANT=acme REASON="..." [ADMIN=admin@acme.localhost] [RESET_FACTORS=1]
	@test -n "$(TENANT)" && test -n "$(REASON)" || (echo 'usage: make recover-admin TENANT=acme REASON="why, for the audit trails" [ADMIN=admin@acme.localhost] [RESET_FACTORS=1]' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) ACTION=administrator-recovery REASON="$(REASON)" RESET_FACTORS=$(or $(RESET_FACTORS),0) $(COMPOSE_DIR)/scripts/provision.sh $(TENANT) "$(ADMIN)"

impersonate: ## Ask the fleet for a one-time link to sign in as a person of a tenant that allows it, with a reason both audit trails keep (TF-025, ADR-0049): make impersonate TENANT=acme LOGIN=pat@acme.localhost REASON="..." [MINUTES=30]
	@test -n "$(TENANT)" && test -n "$(LOGIN)" && test -n "$(REASON)" || (echo 'usage: make impersonate TENANT=acme LOGIN=pat@acme.localhost REASON="why, for the audit trails" [MINUTES=30]' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) ACTION=impersonation REASON="$(REASON)" MINUTES=$(MINUTES) $(COMPOSE_DIR)/scripts/provision.sh $(TENANT) "$(LOGIN)"

break-glass-request: ## Ask for emergency access to a tenant as the operator, with a ticket and a reason; a second operator approves within 30 minutes (TF-026, ADR-0049): make break-glass-request TENANT=acme TICKET=INC-1 REASON="..." [MINUTES=60]
	@test -n "$(TENANT)" && test -n "$(TICKET)" && test -n "$(REASON)" || (echo 'usage: make break-glass-request TENANT=acme TICKET=INC-1 REASON="why, for the audit trails" [MINUTES=60]' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) TICKET="$(TICKET)" REASON="$(REASON)" MINUTES=$(MINUTES) $(COMPOSE_DIR)/scripts/break-glass.sh request $(TENANT)

break-glass-approve: ## Approve a break-glass request as the second operator (operator-two), with a fresh token: make break-glass-approve ID=<id>
	@test -n "$(ID)" || (echo 'usage: make break-glass-approve ID=<the request id>' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_CLIENT_ID=operator-two OPERATOR_SECRET=$(DEV_SECOND_OPERATOR_SECRET) $(COMPOSE_DIR)/scripts/break-glass.sh approve $(ID)

break-glass-link: ## The approved access's one-time sign-in link, once, to the operator who asked: make break-glass-link ID=<id>
	@test -n "$(ID)" || (echo 'usage: make break-glass-link ID=<the request id>' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) $(COMPOSE_DIR)/scripts/break-glass.sh link $(ID)

break-glass-end: ## End an access before its time; the account closes and its review follows: make break-glass-end ID=<id>
	@test -n "$(ID)" || (echo 'usage: make break-glass-end ID=<the request id>' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) $(COMPOSE_DIR)/scripts/break-glass.sh end $(ID)

break-glass-show: ## Where a break-glass access stands, and its review once it ended: make break-glass-show ID=<id>
	@test -n "$(ID)" || (echo 'usage: make break-glass-show ID=<the request id>' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) $(COMPOSE_DIR)/scripts/break-glass.sh show $(ID)

platform-document: ## Publish a new version of the platform's terms of service or privacy notice; every person accepts it at their next request (TF-028, ADR-0049): make platform-document KEY=terms_of_service TITLE="Terms of service" FILE=path/to/text.md
	@test -n "$(KEY)" && test -n "$(TITLE)" && test -n "$(FILE)" || (echo 'usage: make platform-document KEY=terms_of_service|privacy_notice TITLE="..." FILE=path/to/text.md' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) python3 $(COMPOSE_DIR)/scripts/platform-document.py "$(KEY)" "$(TITLE)" "$(FILE)"

accept-documents: ## Accept the current document versions as a tenant's stored test administrator, deliberately, after make platform-document; the smoke and the browser tests never accept one (ADR-0049): make accept-documents TENANT=echo
	@test -n "$(TENANT)" || (echo 'usage: make accept-documents TENANT=echo' && exit 1)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	secret=$$(python3 -c 'import json,sys,urllib.request; r=urllib.request.Request("http://localhost:8200/v1/secret/data/tenants/" + sys.argv[2] + "/smoke", headers={"X-Vault-Token": sys.argv[1]}); d=json.load(urllib.request.urlopen(r))["data"]["data"]; print(d["administrator_password"] + "\n" + d.get("administrator_totp_secret", ""))' "$$OPENBAO_ROOT_TOKEN" "$(TENANT)" 2>/dev/null) \
	  || { echo "No administrator password at secret/tenants/$(TENANT)/smoke: run make smoke TENANT=$(TENANT) first."; exit 1; }; \
	ACCEPT_TENANT=$(TENANT) ACCEPT_ADMIN_PASSWORD="$$(printf '%s\n' "$$secret" | sed -n 1p)" ACCEPT_ADMIN_TOTP_SECRET="$$(printf '%s\n' "$$secret" | sed -n 2p)" \
	  python3 $(COMPOSE_DIR)/scripts/accept-documents.py

verify-audit: ## Ask the fleet to verify a tenant's audit chain now and print the record it keeps (TF-061, ADR-0050): make verify-audit TENANT=echo [FULL=1]
	@test -n "$(TENANT)" || (echo 'usage: make verify-audit TENANT=echo [FULL=1]' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) FULL=$(or $(FULL),0) $(COMPOSE_DIR)/scripts/audit-chain.sh verify $(TENANT)

audit-accept-restore: ## Accept the restore point a tenant's latest verification names, with a ticket and a reason; the chain continues as a new segment (ADR-0050): make audit-accept-restore TENANT=echo TICKET=INC-1 REASON="..."
	@test -n "$(TENANT)" && test -n "$(TICKET)" && test -n "$(REASON)" || (echo 'usage: make audit-accept-restore TENANT=echo TICKET=INC-1 REASON="why, for both audit trails"' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) TICKET="$(TICKET)" REASON="$(REASON)" $(COMPOSE_DIR)/scripts/audit-chain.sh accept $(TENANT)

audit-chain: ## A tenant's latest audit chain verifications, accepted restores and anchors, as the control plane keeps them (ADR-0050): make audit-chain TENANT=echo
	@test -n "$(TENANT)" || (echo 'usage: make audit-chain TENANT=echo' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) $(COMPOSE_DIR)/scripts/audit-chain.sh show $(TENANT)

apply-plan: ## Preview a plan for a tenant, then apply it (DRY_RUN=1 previews only; STARTS=yyyy-mm-dd ENDS=yyyy-mm-dd set the dates): make apply-plan TENANT=echo PLAN=Enterprise
	@test -n "$(TENANT)" && test -n "$(PLAN)" || (echo "usage: make apply-plan TENANT=echo PLAN=Enterprise [DRY_RUN=1] [STARTS=2026-11-01] [ENDS=2027-10-31]" && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) DRY_RUN=$(or $(DRY_RUN),0) $(COMPOSE_DIR)/scripts/apply-plan.sh $(TENANT) $(PLAN) "$(STARTS)" "$(ENDS)"

flag: ## Set a flag's rule for a tenant, or for the platform without TENANT, and print it (ADR-0035): make flag KEY=flags.example_tone VALUE=warm TENANT=echo [REASON="..."]
	@test -n "$(KEY)" && test -n "$(VALUE)" || (echo 'usage: make flag KEY=flags.example_tone VALUE=warm [TENANT=echo] [REASON="..."]' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) /bin/bash $(COMPOSE_DIR)/scripts/flag-rule.sh flag "$(KEY)" '$(VALUE)' "$(TENANT)" "$(REASON)"

# PATH=imports on the command line replaces the recipe's PATH, so the recipe names /bin/bash and the script resets PATH.
KILL_SWITCH = $(if $(filter command line,$(origin PATH)),$(PATH),$(SWITCH))
kill-switch: ## Turn a kill switch on or off for the platform, or TENANT=x, with a reason (only an operator): make kill-switch PATH=imports ON=true REASON="..."
	@test -n "$(KILL_SWITCH)" && test -n "$(ON)" && test -n "$(REASON)" || (echo 'usage: make kill-switch PATH=imports ON=true REASON="why, for the administrators" [TENANT=echo]' && exit 1)
	@CONTROL_URL=$(CONTROL_URL) OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) /bin/bash $(COMPOSE_DIR)/scripts/flag-rule.sh kill "$(KILL_SWITCH)" "$(ON)" "$(REASON)" "$(TENANT)"

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
	dotnet run --project src/Hosts/Iron.Host.Api

# The shell needs a Linux Node: from WSL, a Windows npm on the PATH runs through cmd.exe, which cannot use a WSL path.
shell-node:
	@npm_path="$$(command -v npm || true)"; case "$$npm_path" in \
	  "") echo "npm is not installed in WSL. Install Node 26 here: nvm install 26 (https://github.com/nvm-sh/nvm)."; exit 1;; \
	  /mnt/*|*.cmd|*.exe) echo "npm resolves to the Windows build ($$npm_path). Install Node 26 inside WSL (nvm install 26) and remove shell/node_modules."; exit 1;; \
	esac

# The tenant the smoke and the browser tests run against: TENANT=x, else the smoke tenant. Not a global default, so
# provision, update-unit and deprovision still refuse to run without a TENANT.
TEST_TENANT = $(or $(TENANT),smoke)

smoke: env test-provider ## Run the smoke test against the local stack (control plane, fleet and telemetry up): provisions or reuses tenant smoke, or TENANT=x
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	SMOKE_CONTROL_URL=$(CONTROL_URL) SMOKE_OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) SMOKE_READER_SECRET=$(DEV_SMOKE_READER_SECRET) SMOKE_STORE_TOKEN=$$OPENBAO_ROOT_TOKEN \
	SMOKE_TENANT=$(TEST_TENANT) SMOKE_REPORT=$${SMOKE_REPORT:-$(RESULTS)/smoke/smoke-report.json} dotnet run --project tools/Iron.Smoke --configuration Release

e2e: shell-node env test-provider ## Set up the run's own unit with its people, run the Playwright tests in it against the smoke tenant, or TENANT=x, then deactivate its people and units whatever the result (make smoke with the same TENANT first; browsers: cd shell && npm run e2e:install)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; \
	password=$$(python3 -c 'import json,sys,urllib.request; r=urllib.request.Request("http://localhost:8200/v1/secret/data/tenants/" + sys.argv[2] + "/smoke", headers={"X-Vault-Token": sys.argv[1]}); print(json.load(urllib.request.urlopen(r))["data"]["data"]["administrator_password"])' "$$OPENBAO_ROOT_TOKEN" "$(TEST_TENANT)" 2>/dev/null) \
	  || { echo "No administrator password at secret/tenants/$(TEST_TENANT)/smoke: run make smoke TENANT=$(TEST_TENANT) first."; exit 1; }; \
	totp=$$(python3 -c 'import json,sys,urllib.request; r=urllib.request.Request("http://localhost:8200/v1/secret/data/tenants/" + sys.argv[2] + "/smoke", headers={"X-Vault-Token": sys.argv[1]}); print(json.load(urllib.request.urlopen(r))["data"]["data"].get("administrator_totp_secret", ""))' "$$OPENBAO_ROOT_TOKEN" "$(TEST_TENANT)" 2>/dev/null); \
	scoped=$$(E2E_TENANT=$(TEST_TENANT) E2E_ADMIN_PASSWORD="$$password" E2E_ADMIN_TOTP_SECRET="$$totp" python3 $(COMPOSE_DIR)/scripts/e2e-data.py setup) \
	  || { echo "The run's unit for the browser tests could not be set up on $(TEST_TENANT)."; exit 1; }; \
	eval "$$scoped"; \
	(cd shell && E2E_BASE_URL=$${E2E_BASE_URL:-https://$(TEST_TENANT).localhost} E2E_LOGIN_ID=admin@$(TEST_TENANT).localhost E2E_PASSWORD="$$password" E2E_TOTP_SECRET="$$totp" \
	  E2E_TENANT=$(TEST_TENANT) E2E_CONTROL_URL=$(CONTROL_URL) E2E_OPERATOR_SECRET="$${OPERATOR_SECRET:-$(DEV_OPERATOR_SECRET)}" npx playwright test); \
	status=$$?; \
	E2E_TENANT=$(TEST_TENANT) E2E_ADMIN_PASSWORD="$$password" E2E_ADMIN_TOTP_SECRET="$$totp" python3 $(COMPOSE_DIR)/scripts/e2e-data.py teardown || status=1; \
	exit $$status

shell-install: shell-node ## Install the shell's packages (shell/)
	cd shell && npm ci --no-audit --no-fund

shell-dev: shell-node ## Run the shell with Vite on http://localhost:5173, proxying the API paths to make run-api
	cd shell && npm run dev

shell-check: shell-node ## Lint, check the formatting of, test and build the shell
	cd shell && npm run check

shell-a11y: shell-node ## Build the gallery and run the accessibility scan on it (browsers once: cd shell && npm run e2e:install)
	cd shell && npm run a11y

images: ## Build the four host images and the shell image (foundation/iron-*:$(TAG))
	docker build -f src/Hosts/Iron.Host.Api/Dockerfile -t foundation/iron-api:$(TAG) .
	docker build -f src/Hosts/Iron.Host.Worker/Dockerfile -t foundation/iron-worker:$(TAG) .
	docker build -f src/Hosts/Iron.Host.ControlPlane/Dockerfile -t foundation/iron-control-plane:$(TAG) .
	docker build -f src/Hosts/Iron.Host.Fleet/Dockerfile -t foundation/iron-fleet:$(TAG) .
	docker build -t foundation/iron-shell:$(TAG) shell

# ---------------------------------------------------------------- laptop platform (infra/compose)
env: ## Create infra/compose/.env with random local credentials (runs once)
	@sh $(COMPOSE_DIR)/scripts/init-env.sh

up: env ## Start the platform with the telemetry stack, initialise the store on first use, and wait until everything is healthy
	$(COMPOSE) --profile telemetry up --detach --wait
	@bash $(COMPOSE_DIR)/scripts/init-store.sh
	@sh $(COMPOSE_DIR)/scripts/wait-ready.sh tempo loki
	@sh $(COMPOSE_DIR)/scripts/print-endpoints.sh full

up-slim: env ## Start the platform without the telemetry stack (the store is initialised on first use)
	$(COMPOSE) up --detach --wait
	@bash $(COMPOSE_DIR)/scripts/init-store.sh
	@sh $(COMPOSE_DIR)/scripts/print-endpoints.sh slim

up-ci: env ## Start the slim platform plus the collector and Tempo (what the pipeline uses)
	$(COMPOSE) --profile ci up --detach --wait
	@bash $(COMPOSE_DIR)/scripts/init-store.sh
	@sh $(COMPOSE_DIR)/scripts/wait-ready.sh tempo

up-control: env ## Run the control plane API and the fleet worker as containers from the images, migrated and seeded first (make images first; secret with container hosts)
	@set -a; . $(COMPOSE_DIR)/.env; set +a; CONTROL_DB_HOST=postgres CONTROL_BLOB_HOST=blob $(COMPOSE_DIR)/scripts/write-control-secret.sh
	@# As the pipeline does: a new image may bring a migration or a permission, and the API must not start without them.
	IMAGE_TAG=$(TAG) $(COMPOSE) --profile control run --rm --no-deps control-api migrate
	IMAGE_TAG=$(TAG) $(COMPOSE) --profile control run --rm --no-deps \
	  -e Seed__ServiceAccounts__0__ClientId=operator -e Seed__ServiceAccounts__0__Secret=$(DEV_OPERATOR_SECRET) -e Seed__ServiceAccounts__0__Role=tenant-administrator -e "Seed__ServiceAccounts__0__DisplayName=Local operator" \
	  -e Seed__ServiceAccounts__1__ClientId=smoke-reader -e Seed__ServiceAccounts__1__Secret=$(DEV_SMOKE_READER_SECRET) -e Seed__ServiceAccounts__1__Role=service-default -e "Seed__ServiceAccounts__1__DisplayName=Smoke reader (denial case)" \
	  -e Seed__ServiceAccounts__2__ClientId=operator-two -e Seed__ServiceAccounts__2__Secret=$(DEV_SECOND_OPERATOR_SECRET) -e Seed__ServiceAccounts__2__Role=tenant-administrator -e "Seed__ServiceAccounts__2__DisplayName=Second local operator" \
	  control-api seed
	IMAGE_TAG=$(TAG) $(COMPOSE) --profile control up --detach --wait --force-recreate control-api fleet

test-provider: env ## Start the single sign-on test provider at http://idp.localhost (profile sso, ADR-0048), which make smoke and make e2e start themselves
	@$(COMPOSE) --profile sso up --detach --wait idp
	@sh $(COMPOSE_DIR)/scripts/wait-ready.sh idp

down-control: env ## Stop the containerised control plane (make secret-control again before make run-control)
	$(COMPOSE) --profile control stop control-api fleet

integration: ## The pipeline's integration stage on this machine: platform and control plane from images, then the smoke (stop make run-control first)
	@OPERATOR_SECRET=$(DEV_OPERATOR_SECRET) SMOKE_READER_SECRET=$(DEV_SMOKE_READER_SECRET) \
	IMAGE_REPOSITORY=$${IMAGE_REPOSITORY:-foundation} IMAGE_TAG=$${IMAGE_TAG:-$(TAG)} $(COMPOSE_DIR)/scripts/ci-integration.sh $${TENANT:-acme}

down: env ## Stop and remove the platform containers; data volumes are kept
	$(COMPOSE) $(ALL_PROFILES) down

reset: env ## Stop the platform and delete all its data volumes (the store's root token and recovery key go with its volume)
	$(COMPOSE) $(ALL_PROFILES) down --volumes
	@sed -i '/^OPENBAO_ROOT_TOKEN=/d;/^OPENBAO_RECOVERY_KEY=/d' $(COMPOSE_DIR)/.env
	@echo "Removed the store's root token and recovery key from $(COMPOSE_DIR)/.env; the next make up initialises a new store."

ps: env ## Show the platform containers
	$(COMPOSE) $(ALL_PROFILES) ps

logs: env ## Follow platform logs (SERVICE=name for one service)
	$(COMPOSE) $(ALL_PROFILES) logs --follow --tail=100 $(SERVICE)

certs: ## Create a locally trusted TLS certificate with mkcert; optional (TENANTS="acme bravo")
	@sh $(COMPOSE_DIR)/scripts/make-certs.sh $(TENANTS)

hosts: ## Print the /etc/hosts line for command line tools (TENANTS="acme bravo")
	@sh $(COMPOSE_DIR)/scripts/hosts-line.sh $(TENANTS)
