#!/usr/bin/env bash
# Writes the control plane's unit secret (secret/control/unit) to OpenBao: the control database, the fleet's admin
# connections and a signing key. The control API and the fleet worker read it at start, like any unit (ADR-0016).
set -euo pipefail

address="${OPENBAO_ADDRESS:-http://localhost:8200}"
key_file="$(dirname "$0")/../.control-signing-key.pem"
if [ ! -f "$key_file" ]; then
  openssl ecparam -name prime256v1 -genkey -noout -out "$key_file"
fi
db_host="${CONTROL_DB_HOST:-localhost}"

python3 - "$address" "$OPENBAO_ROOT_TOKEN" "$key_file" "$db_host" "$POSTGRES_SUPERUSER" "$POSTGRES_SUPERUSER_PASSWORD" "$BLOB_ACCOUNT_NAME" "$BLOB_ACCOUNT_KEY" <<'PY'
import json, sys, urllib.request
address, token, key_file, db_host, db_user, db_password, blob_account, blob_key = sys.argv[1:]
data = {
    "ConnectionStrings:Database": f"Host={db_host};Port=5432;Database=control;Username={db_user};Password={db_password}",
    "Tokens:SigningKeyPem": open(key_file).read(),
    "Fleet:Database:AdminConnectionString": f"Host={db_host};Port=5432;Database=postgres;Username={db_user};Password={db_password}",
    "Fleet:Storage:AccountConnectionString": f"DefaultEndpointsProtocol=http;AccountName={blob_account};AccountKey={blob_key};BlobEndpoint=http://localhost:10000/{blob_account};",
}
request = urllib.request.Request(f"{address}/v1/secret/data/control/unit", data=json.dumps({"data": data}).encode(), method="POST")
request.add_header("X-Vault-Token", token)
request.add_header("Content-Type", "application/json")
with urllib.request.urlopen(request, timeout=10) as response:
    version = json.load(response)["data"]["version"]
print(f"Wrote secret/control/unit (version {version}) with {len(data)} keys.")
PY
