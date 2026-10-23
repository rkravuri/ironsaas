#!/usr/bin/env bash
# A tenant's audit chain (TF-061, ADR-0050) as an operator, with client credentials against the control plane:
#   audit-chain.sh verify <tenant>   asks the fleet to verify the chain now (FULL=1 from the first position), waits for
#                                    the record, at most three minutes, and prints it;
#   audit-chain.sh show <tenant>     the latest verifications, accepted restores and anchors the control plane keeps;
#   audit-chain.sh accept <tenant>   accepts the restore point the latest verification names, with TICKET and REASON and a
#                                    fresh token: the chain continues as a new segment; then waits for the full
#                                    verification it asks for and prints it.
# The fleet runs the verification with a reader role of its own; the unit never verifies itself.
set -euo pipefail

action="${1:?usage: audit-chain.sh verify|show|accept <tenant>}"
tenant="${2:?usage: audit-chain.sh verify|show|accept <tenant>}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"

python3 - "$control" "$client_id" "$secret" "$action" "$tenant" "${FULL:-0}" "${TICKET:-}" "${REASON:-}" <<'PY'
import base64, json, sys, time, urllib.error, urllib.request

control, client_id, secret, action, tenant, full, ticket, reason = sys.argv[1:]

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

def show(verification):
    kind = "full" if verification["full"] else "daily"
    print(f"Verification of {verification['tenant_slug']} ({kind}) at {verification['verified_at']}: {verification['result'].upper()}")
    print(f"  {verification['events_checked']} event(s) checked, positions {verification['from_position']} to {verification['to_position']}")
    for problem in verification.get("problems") or []:
        event = problem.get("event_id") or "no event"
        print(f"  {problem['kind']:16} position {problem['position']:>8}  {event}  {problem['message']}")
    if verification.get("restore_position") is not None:
        print(f"  restore point: the chain is whole up to position {verification['restore_position']}, anchored at {verification['restore_at']}")

def show_restore(restore):
    print(f"Restore accepted by {restore['accepted_by']} at {restore['accepted_at']}, ticket {restore['ticket']}: {restore['reason']}")
    print(f"  the chain is whole up to position {restore['restore_position']} (sealed {restore['restore_at']}); positions {restore['lost_from']} to {restore['lost_to']} were lost")
    print(f"  it continues as segment {restore['segment']} from position {restore['start_position']}")

def wait_for(request_id):
    deadline = time.time() + 180
    while time.time() < deadline:
        time.sleep(5)
        status, request = call("GET", f"/api/v1/tenants/{tenant}/audit-verifications/requests/{request_id}", token=access_token)
        if status != 200:
            refused("Reading the request", status, request)
        if request.get("verification"):
            show(request["verification"])
            sys.exit(0 if request["verification"]["result"] != "failed" else 2)
    sys.exit("The fleet did not answer within three minutes: is it running (make up-control), and is the tenant's unit up?")

status, token = call("POST", "/connect/token", form={"grant_type": "client_credentials"})
if status != 200:
    sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
access_token = token["access_token"]

if action == "verify":
    status, order = call("POST", f"/api/v1/tenants/{tenant}/audit-verifications", body={"full": full == "1"}, token=access_token)
    if status != 202:
        refused("The verification request", status, order)
    print(f"Asked the fleet to verify {tenant}'s audit chain{' in full' if full == '1' else ''} (request {order['id']}); waiting for its record.")
    wait_for(order["id"])
elif action == "accept":
    # The token was asked for just now, so the step-up the endpoint needs holds (ADR-0047).
    status, accepted = call("POST", f"/api/v1/tenants/{tenant}/audit-restores", body={"ticket": ticket, "reason": reason}, token=access_token)
    if status != 201:
        refused("Accepting the restore", status, accepted)
    show_restore(accepted["restore"])
    print("The unit records the acceptance in its own audit trail within a minute. Waiting for the full verification it asked for.")
    wait_for(accepted["verification_request_id"])
elif action == "show":
    status, verifications = call("GET", f"/api/v1/tenants/{tenant}/audit-verifications?page_size=5", token=access_token)
    if status != 200:
        refused("Reading the verifications", status, verifications)
    if not verifications["data"]:
        print(f"No verification of {tenant}'s chain yet.")
    for verification in verifications["data"]:
        show(verification)
    status, restores = call("GET", f"/api/v1/tenants/{tenant}/audit-restores", token=access_token)
    if status != 200:
        refused("Reading the restores", status, restores)
    for restore in restores["restores"]:
        show_restore(restore)
    status, anchors = call("GET", f"/api/v1/tenants/{tenant}/audit-anchors?page_size=5", token=access_token)
    if status != 200:
        refused("Reading the anchors", status, anchors)
    print("Latest anchors:")
    for anchor in anchors["data"]:
        print(f"  {anchor['kind']:7} segment {anchor.get('segment', 1)} position {anchor['position']:>8}  {anchor['hash']}  at {anchor['at']}, received {anchor['received_at']}")
else:
    sys.exit("usage: audit-chain.sh verify|show|accept <tenant>")
PY
