#!/usr/bin/env bash
# Writes the dev unit's secret to OpenBao, the way the fleet writes one per tenant (T14).
# Needs the laptop platform up and the variables of infra/compose/.env in the environment (make secret-dev does both).
set -euo pipefail

address="${OPENBAO_ADDRESS:-http://localhost:8200}"
key_file="$(dirname "$0")/../.dev-signing-key.pem"
if [ ! -f "$key_file" ]; then
  openssl ecparam -name prime256v1 -genkey -noout -out "$key_file"
fi

python3 - "$address" "$OPENBAO_ROOT_TOKEN" "$key_file" "$POSTGRES_SUPERUSER" "$POSTGRES_SUPERUSER_PASSWORD" "$BLOB_ACCOUNT_NAME" "$BLOB_ACCOUNT_KEY" <<'PY'
import json, sys, urllib.request
address, token, key_file, db_user, db_password, blob_account, blob_key = sys.argv[1:]
data = {
    "ConnectionStrings:Database": f"Host=localhost;Port=5432;Database=postgres;Username={db_user};Password={db_password}",
    "Storage:ConnectionString": f"DefaultEndpointsProtocol=http;AccountName={blob_account};AccountKey={blob_key};BlobEndpoint=http://localhost:10000/{blob_account};",
    "Storage:Container": "dev",
    "Tokens:SigningKeyPem": open(key_file).read(),
}
request = urllib.request.Request(f"{address}/v1/secret/data/tenants/dev/unit", data=json.dumps({"data": data}).encode(), method="POST")
request.add_header("X-Vault-Token", token)
request.add_header("Content-Type", "application/json")
with urllib.request.urlopen(request, timeout=10) as response:
    version = json.load(response)["data"]["version"]
print(f"Wrote secret/tenants/dev/unit (version {version}) with {len(data)} keys.")
PY
