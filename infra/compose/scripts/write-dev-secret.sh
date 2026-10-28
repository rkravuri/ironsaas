#!/usr/bin/env bash
# Writes the dev unit's secret to OpenBao, the way the fleet writes one per tenant (T14), and makes its keys in the
# transit engine (tenant-dev-data, -index and -signing, ADR-0054), as the fleet's secrets step makes a tenant's.
# Needs the laptop platform up and the variables of infra/compose/.env in the environment (make secret-dev does both).
set -euo pipefail

address="${OPENBAO_ADDRESS:-http://localhost:8200}"

python3 - "$address" "$OPENBAO_ROOT_TOKEN" "$POSTGRES_SUPERUSER" "$POSTGRES_SUPERUSER_PASSWORD" "$BLOB_ACCOUNT_NAME" "$BLOB_ACCOUNT_KEY" <<'PY'
import json, sys, urllib.error, urllib.request
address, token, db_user, db_password, blob_account, blob_key = sys.argv[1:]

def call(method, path, body=None):
    request = urllib.request.Request(f"{address}/v1/{path}", data=None if body is None else json.dumps(body).encode(), method=method)
    request.add_header("X-Vault-Token", token)
    request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            text = response.read().decode()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        return error.code, None

for name, kind in (("tenant-dev-data", {"type": "aes256-gcm96"}), ("tenant-dev-index", {"type": "hmac", "key_size": 32}), ("tenant-dev-signing", {"type": "ecdsa-p256"})):
    if call("GET", f"transit/keys/{name}")[0] == 404:
        call("POST", f"transit/keys/{name}", kind)

data = {
    "ConnectionStrings:Database": f"Host=localhost;Port=5432;Database=postgres;Username={db_user};Password={db_password}",
    "Storage:ConnectionString": f"DefaultEndpointsProtocol=http;AccountName={blob_account};AccountKey={blob_key};BlobEndpoint=http://localhost:10000/{blob_account};",
    "Storage:Container": "dev",
    "Keys:Prefix": "tenant-dev",
}
status, answer = call("POST", "secret/data/tenants/dev/unit", {"data": data})
print(f"Wrote secret/tenants/dev/unit (version {answer['data']['version']}) with {len(data)} keys; the dev unit's keys are tenant-dev-data, -index and -signing.")
PY
