#!/usr/bin/env bash
# Writes the fleet's bootstrap secret (secret/fleet/unit) to OpenBao: the platform superuser's connections to the
# database server and the storage account, which the fleet's prepare-control needs, run next by make secret-control and
# make up-control with the store's root token. prepare-control gives the control plane its own database roles and keys
# and writes the three secrets the control plane runs on (ADR-0053, ADR-0054): control/unit for its API, fleet/unit for
# the fleet, each with a store token of its own, and control-migrate/unit for migrate and seed. No key is written: the
# control plane's keys are the key service's (control-data, control-index, control-signing).
set -euo pipefail

address="${OPENBAO_ADDRESS:-http://localhost:8200}"
# From the laptop (make run-control) the platform is on localhost; from the control profile's containers it is postgres and blob.
db_host="${CONTROL_DB_HOST:-localhost}"
blob_host="${CONTROL_BLOB_HOST:-localhost}"

python3 - "$address" "$OPENBAO_ROOT_TOKEN" "$db_host" "$blob_host" "$POSTGRES_SUPERUSER" "$POSTGRES_SUPERUSER_PASSWORD" "$BLOB_ACCOUNT_NAME" "$BLOB_ACCOUNT_KEY" <<'PY'
import json, sys, urllib.request
address, token, db_host, blob_host, db_user, db_password, blob_account, blob_key = sys.argv[1:]
data = {
    # Only until prepare-control writes the app role's connection in its place: the fleet's host needs one to start.
    "ConnectionStrings:Database": f"Host={db_host};Port=5432;Database=postgres;Username={db_user};Password={db_password}",
    "Fleet:Database:AdminConnectionString": f"Host={db_host};Port=5432;Database=postgres;Username={db_user};Password={db_password}",
    "Fleet:Storage:AccountConnectionString": f"DefaultEndpointsProtocol=http;AccountName={blob_account};AccountKey={blob_key};BlobEndpoint=http://{blob_host}:10000/{blob_account};",
}
request = urllib.request.Request(f"{address}/v1/secret/data/fleet/unit", data=json.dumps({"data": data}).encode(), method="POST")
request.add_header("X-Vault-Token", token)
request.add_header("Content-Type", "application/json")
with urllib.request.urlopen(request, timeout=10) as response:
    version = json.load(response)["data"]["version"]
print(f"Wrote secret/fleet/unit (version {version}) with {len(data)} keys, for prepare-control.")
PY
