#!/usr/bin/env bash
# The laptop's OpenBao, initialised once and checked on every later start (ADR-0028). make up, up-slim and up-ci run
# it after the containers are healthy; the pipeline's integration script does too.
#
# A new store: initialised with one recovery share; the root token and the recovery key go into infra/compose/.env
# (replacing a development placeholder) before anything else; KV version 2 is enabled at secret/.
# An initialised store: it must be unsealed (the static seal does that at start) and the root token in .env must
# open it. Nothing is written then, except KV version 2 at secret/ if an earlier first run stopped before enabling it.
set -euo pipefail

here=$(cd "$(dirname "$0")/.." && pwd)
env_file="$here/.env"
address="${OPENBAO_ADDRESS:-http://localhost:8200}"

python3 - "$address" "$env_file" <<'PY'
import json, sys, time, urllib.error, urllib.request

address, env_file = sys.argv[1:]


def call(method, path, token=None, body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(f"{address}{path}", data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("X-Vault-Token", token)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            text = response.read().decode()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        text = error.read().decode()
        try:
            return error.code, (json.loads(text) if text else None)
        except ValueError:
            return error.code, {"errors": [text]}


def read_env():
    values = {}
    with open(env_file) as handle:
        for line in handle:
            if "=" in line and not line.lstrip().startswith("#"):
                key, _, value = line.rstrip("\n").partition("=")
                values[key] = value
    return values


def write_env(updates):
    """Replaces the lines of these keys, or appends them; every other line stays as it was."""
    with open(env_file) as handle:
        lines = handle.read().splitlines()
    kept = [line for line in lines if line.partition("=")[0] not in updates]
    kept += [f"{key}={value}" for key, value in updates.items()]
    with open(env_file, "w") as handle:
        handle.write("\n".join(kept) + "\n")


def wait_for(condition, seconds):
    deadline = time.time() + seconds
    while True:
        try:
            result = condition()
            if result:
                return result
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        if time.time() >= deadline:
            return None
        time.sleep(1)


answer = wait_for(lambda: call("GET", "/v1/sys/init"), 60)
if answer is None or answer[0] != 200:
    sys.exit(f"OpenBao at {address} does not answer. make logs SERVICE=openbao says why.")

if not answer[1]["initialized"]:
    status, result = call("PUT", "/v1/sys/init", body={"recovery_shares": 1, "recovery_threshold": 1})
    if status != 200:
        sys.exit(f"Initialising the store failed ({status}): {result}")
    # The token goes into .env first and on its own: if anything after this line fails, the store is still reachable.
    write_env({"OPENBAO_ROOT_TOKEN": result["root_token"]})
    # The HTTP API names the field recovery_keys_base64 (the CLI's JSON says recovery_keys_b64).
    recovery = (result.get("recovery_keys_base64") or result.get("recovery_keys_b64") or [None])[0]
    if recovery:
        write_env({"OPENBAO_RECOVERY_KEY": recovery})
    print("Initialised the store: the root token and the recovery key are in infra/compose/.env.")

unsealed = wait_for(lambda: (lambda s: s[0] == 200 and not s[1]["sealed"])(call("GET", "/v1/sys/seal-status")), 30)
if not unsealed:
    sys.exit(
        "The store stays sealed: OPENBAO_UNSEAL_KEY in infra/compose/.env is not the key the store was initialised with "
        "(make logs SERVICE=openbao shows 'failed to unseal'). Put the right key back, or make reset to start over with a new store."
    )

token = read_env().get("OPENBAO_ROOT_TOKEN", "")
if not token:
    sys.exit(
        "The store is initialised but infra/compose/.env has no OPENBAO_ROOT_TOKEN. With OPENBAO_RECOVERY_KEY, "
        "'bao operator generate-root' makes a new one; otherwise make reset starts over (the volume and the token go together)."
    )
status, _ = call("GET", "/v1/auth/token/lookup-self", token=token)
if status != 200:
    sys.exit(
        f"The root token in infra/compose/.env does not open the store ({status}): it belongs to an earlier store. "
        "make reset starts over (the volume and the token go together)."
    )

status, mounts = call("GET", "/v1/sys/mounts", token=token)
if status != 200:
    sys.exit(f"Reading the store's mounts failed ({status}): {mounts}")
if "secret/" not in (mounts.get("data") or mounts):
    status, result = call("POST", "/v1/sys/mounts/secret", token=token, body={"type": "kv", "options": {"version": "2"}})
    if status not in (200, 204):
        sys.exit(f"Enabling KV version 2 at secret/ failed ({status}): {result}")
    print("Enabled KV version 2 at secret/.")

print(f"The store at {address} is initialised and unsealed, and the root token in infra/compose/.env opens it.")
PY
