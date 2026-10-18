#!/usr/bin/env bash
# Registers a tenant in the control plane and asks the fleet to provision it (ACTION=deprovision takes the unit down, ACTION=update re-applies it),
# as an operator (client credentials), then follows the job until it ends and prints every step with its duration.
# Usage: provision.sh <tenant> <administrator email>   with CONTROL_URL and OPERATOR_SECRET in the environment.
set -euo pipefail

tenant="$1"
administrator="${2:-}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"
action="${ACTION:-provision}"
timeout="${PROVISION_TIMEOUT_SECONDS:-900}"

python3 - "$control" "$client_id" "$secret" "$tenant" "$administrator" "$action" "$timeout" <<'PY'
import base64, json, sys, time, urllib.error, urllib.request

control, client_id, secret, tenant, administrator, action, timeout = sys.argv[1:]
timeout = int(timeout)

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

if action == "provision":
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

status, job = call("POST", f"/api/v1/tenants/{tenant}/{action}", token=access)
if status == 202:
    print(f"{action.capitalize()} requested: job {job['job_id']} is {job['status']}.")
elif status == 409:
    print(f"{action.capitalize()} not started: {job.get('detail') if job else status}")
    sys.exit(1)
else:
    sys.exit(f"Requesting {action} failed ({status}): {job}")

seen = {}
deadline = time.time() + timeout
while time.time() < deadline:
    status, job = call("GET", f"/api/v1/tenants/{tenant}/provisioning", token=access)
    if status == 200:
        for step in job.get("steps", []):
            key = (step["name"], step["status"])
            if step["status"] in ("completed", "failed") and key not in seen:
                seen[key] = True
                duration = step.get("duration_ms")
                suffix = f" ({duration} ms)" if duration is not None else ""
                detail = step.get("output") if step["status"] == "completed" else step.get("error")
                print(f"  {step['status']:9} {step['name']:9}{suffix}  {detail or ''}")
        if job["status"] in ("completed", "failed"):
            break
    time.sleep(3)
else:
    sys.exit(f"The job did not finish within {timeout} s; check the fleet worker's log.")

if job["status"] == "failed":
    sys.exit(f"Job {job['job_id']} failed at step {job.get('current_step')}: {job.get('error')}\nFix the cause and run the same command again; the job continues from that step.")
print(f"Job {job['job_id']} completed. {job.get('output') or ''}")
PY
