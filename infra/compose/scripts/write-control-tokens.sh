#!/usr/bin/env bash
# Puts the store tokens of the control plane's API and of the fleet (ADR-0054), which prepare-control keeps at
# secret/control-tokens/unit where only the root token reads them, into infra/compose/.env as CONTROL_API_STORE_TOKEN and
# FLEET_STORE_TOKEN: Compose hands each to its own container, and make run-control and make run-fleet read them from
# there. The file is ignored by git and already holds the root token. Prints no token.
set -euo pipefail

address="${OPENBAO_ADDRESS:-http://localhost:8200}"
env_file="$(cd "$(dirname "$0")/.." && pwd)/.env"

python3 - "$address" "$OPENBAO_ROOT_TOKEN" "$env_file" <<'PY'
import json, re, sys, urllib.request
address, token, env_file = sys.argv[1:]
request = urllib.request.Request(f"{address}/v1/secret/data/control-tokens/unit")
request.add_header("X-Vault-Token", token)
with urllib.request.urlopen(request, timeout=10) as response:
    tokens = json.load(response)["data"]["data"]
values = {"CONTROL_API_STORE_TOKEN": tokens["api_token"], "FLEET_STORE_TOKEN": tokens["fleet_token"]}
with open(env_file, encoding="utf-8") as handle:
    text = handle.read()
for name, value in values.items():
    line = f"{name}={value}"
    text, count = re.subn(rf"^{name}=.*$", line, text, flags=re.MULTILINE)
    if count == 0:
        text = text.rstrip("\n") + f"\n# The {'control plane API' if name.startswith('CONTROL') else 'fleet'}'s own store token (ADR-0054), from prepare-control.\n{line}\n"
with open(env_file, "w", encoding="utf-8") as handle:
    handle.write(text)
print("Wrote the control plane API's and the fleet's store tokens into infra/compose/.env.")
PY
