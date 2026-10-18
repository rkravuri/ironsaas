#!/usr/bin/env bash
# Registers a tenant in the control plane and asks the fleet to provision it, as an operator (client credentials).
# Usage: provision.sh <tenant> <administrator email>   with CONTROL_URL and OPERATOR_SECRET in the environment.
set -euo pipefail

tenant="$1"
administrator="$2"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"

python3 - "$control" "$client_id" "$secret" "$tenant" "$administrator" <<'PY'
import base64, json, sys, urllib.error, urllib.request

control, client_id, secret, tenant, administrator = sys.argv[1:]

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

status, token = call("POST", "/connect/token", form={"grant_type": "client_credentials"})
if status != 200:
    sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
access = token["access_token"]

status, existing = call("GET", f"/api/v1/tenants/{tenant}", token=access)
if status == 404:
    status, created = call("POST", "/api/v1/tenants", body={"slug": tenant, "display_name": tenant.capitalize(), "administrator_login_id": administrator}, token=access)
    if status != 201:
        sys.exit(f"Creating the tenant failed ({status}): {created}")
    print(f"Registered tenant {tenant} with hostnames {', '.join(created['hostnames'])} and administrator {administrator}.")
elif status == 200:
    print(f"Tenant {tenant} exists (status {existing['status']}).")
else:
    sys.exit(f"Reading the tenant failed ({status}): {existing}")

status, job = call("POST", f"/api/v1/tenants/{tenant}/provision", token=access)
if status == 202:
    print(f"Provisioning requested: job {job['job_id']} is {job['status']}.")
elif status == 409:
    print(f"Provisioning not started: {job.get('detail') if job else status}")
else:
    sys.exit(f"Requesting provisioning failed ({status}): {job}")
PY
