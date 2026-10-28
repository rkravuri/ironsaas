#!/usr/bin/env bash
# A tenant's keys (ADR-0054) as an operator, with client credentials against the control plane:
#   keys.sh rotate <tenant>   rotates KEY (data, index, signing or database) with REASON and a fresh token, as the key
#                             officer; waits until the fleet has rotated it, then until the unit has moved every value and
#                             the old version is retired (a signing key waits half an hour for its tokens), at most three
#                             minutes, and prints where it stands;
#   keys.sh status <tenant>   each key's version, last and next rotation, and the latest rotations with their progress.
# The rotation is audited in the control plane and the tenant's own trail, with the operator and the reason.
set -euo pipefail

action="${1:?usage: keys.sh rotate|status <tenant>}"
tenant="${2:?usage: keys.sh rotate|status <tenant>}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"

python3 - "$control" "$client_id" "$secret" "$action" "$tenant" "${KEY:-}" "${REASON:-}" <<'PY'
import base64, json, sys, time, urllib.error, urllib.request

control, client_id, secret, action, tenant, key, reason = sys.argv[1:]

def call(method, path, body=None, token=None, form=None):
    data = None
    headers = {"Accept": "application/json"}
    if form is not None:
        data = "&".join(f"{k}={v}" for k, v in form.items()).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded"
        headers["Authorization"] = "Basic " + base64.b64encode(f"{client_id}:{secret}".encode()).decode()
    elif body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(control + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            text = response.read().decode()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        text = error.read().decode()
        return error.code, (json.loads(text) if text else None)

def refused(what, status, answer):
    detail = answer.get("detail") if isinstance(answer, dict) else answer
    sys.exit(f"{what} was refused ({status}): {detail}")

def show_rotation(rotation):
    versions = f"version {rotation['from_version']} to {rotation['to_version']}" if rotation.get("to_version") else "not rotated yet"
    moved = f", {rotation['done']} of {rotation['total']} values moved" if rotation["key"] in ("data", "index") and rotation.get("to_version") else ""
    print(f"  {rotation['key']:9} {rotation['state']:9} {versions}{moved}; asked by {rotation['requested_by']} ({rotation['trigger']}) at {rotation['requested_at']}: {rotation['reason']}")
    if rotation.get("error"):
        print(f"            error: {rotation['error']}")

status, token = call("POST", "/connect/token", form={"grant_type": "client_credentials"})
if status != 200:
    sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
access_token = token["access_token"]

if action == "status":
    status, keys = call("GET", f"/api/v1/tenants/{tenant}/keys", token=access_token)
    if status != 200:
        refused("Reading the keys", status, keys)
    print(f"Keys of {tenant}:")
    for item in keys["keys"]:
        rotated = item.get("rotated_at") or "never"
        print(f"  {item['key']:9} version {item['version']} (from {item['minimum_version']}), rotated {rotated}, next {item['next_rotation_at']}, every {item['rotation_days']} days")
    print("Latest rotations:")
    for rotation in keys["rotations"]:
        show_rotation(rotation)
    sys.exit(0)

if action != "rotate":
    sys.exit(f"Unknown action '{action}': rotate or status.")

status, rotation = call("POST", f"/api/v1/tenants/{tenant}/keys/{key}/rotations", body={"reason": reason}, token=access_token)
if status != 202:
    refused(f"Rotating the {key} key of {tenant}", status, rotation)
print(f"Asked for a rotation of the {key} key of {tenant} ({rotation['id']}).")

deadline = time.time() + 180
last = None
while time.time() < deadline:
    time.sleep(3)
    status, rotation = call("GET", f"/api/v1/tenants/{tenant}/keys/rotations/{rotation['id']}", token=access_token)
    if status != 200:
        refused("Reading the rotation", status, rotation)
    if rotation["state"] != last:
        show_rotation(rotation)
        last = rotation["state"]
    if rotation["state"] == "failed":
        sys.exit(2)
    if rotation["state"] == "retired" or (key == "signing" and rotation["state"] == "rotated"):
        if key == "signing" and rotation["state"] == "rotated":
            print("  The new version signs; the old one keeps validating tokens for half an hour, then the fleet retires it.")
        sys.exit(0)
sys.exit("The rotation did not finish within three minutes: is the fleet running (make up-control), and the tenant's unit up?")
PY
