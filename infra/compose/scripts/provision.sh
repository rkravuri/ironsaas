#!/usr/bin/env bash
# Registers a tenant in the control plane and asks the fleet to provision it (ACTION=deprovision takes the unit down, ACTION=update re-applies it,
# ACTION=administrator-recovery asks for a recovery link for the tenant's administrator with REASON, ADR-0046, and
# RESET_FACTORS=1 also resets their second factors, ADR-0047; ACTION=impersonation asks for a one-time link to sign in
# as the person named second, with REASON and MINUTES, ADR-0049; ACTION=sample-records marks the tenant's unit to run the
# sample module for testing when SAMPLE_RECORDS=1, or unmarks it when SAMPLE_RECORDS=0, ADR-0057; a provisioning with
# SAMPLE_RECORDS=1 registers the tenant marked, and with SUITES=procurement registers it with those suites, each under
# its only plan, ADR-0059; ACTION=suite-add adds SUITE to an active tenant under PLAN, or the suite's only plan, and
# ACTION=suite-remove removes it, both with REASON, each an update job of the fleet, ADR-0059),
# as an operator (client credentials), then follows the job until it ends and prints every step with its duration.
# A link the job handed over (the first administrator's invitation, a recovery link) is printed once: the control plane
# gives it once, to the operator who asked, and deletes it; it is never in the job's output (ADR-0046).
# Usage: provision.sh <tenant> <administrator email>   with CONTROL_URL and OPERATOR_SECRET in the environment.
set -euo pipefail

tenant="$1"
administrator="${2:-}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"
action="${ACTION:-provision}"
timeout="${PROVISION_TIMEOUT_SECONDS:-900}"
reason="${REASON:-}"
reset_factors="${RESET_FACTORS:-0}"
minutes="${MINUTES:-}"
sample_records="${SAMPLE_RECORDS:-}"
suites="${SUITES:-}"
suite="${SUITE:-}"
plan="${PLAN:-}"

python3 - "$control" "$client_id" "$secret" "$tenant" "$administrator" "$action" "$timeout" "$reason" "$reset_factors" "$minutes" "$sample_records" "$suites" "$suite" "$plan" <<'PY'
import base64, json, sys, time, urllib.error, urllib.request

control, client_id, secret, tenant, administrator, action, timeout, reason, reset_factors, minutes, sample_records, suites, suite, plan = sys.argv[1:]
timeout = int(timeout)
suites = [name.strip() for name in suites.split(",") if name.strip()]

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
        body = {
            "slug": tenant,
            "display_name": tenant.capitalize(),
            "administrator_login_id": administrator,
            "sample_records": sample_records == "1",
            "suites": [{"suite": name, "plan": None} for name in suites],
            "reason": reason or None,
        }
        status, created = call("POST", "/api/v1/tenants", body=body, token=access)
        if status != 201:
            sys.exit(f"Creating the tenant failed ({status}): {created}")
        with_suites = f" and the suites {', '.join(created['suites'])}" if created.get("suites") else ""
        print(f"Registered tenant {tenant} with hostnames {', '.join(created['hostnames'])}, administrator {administrator}{with_suites}.")
    elif status == 200:
        print(f"Tenant {tenant} exists (status {existing['status']}).")
        missing = [name for name in suites if name not in (existing.get("suites") or [])]
        if missing:
            print(f"It does not have {', '.join(missing)}: an existing tenant gains a suite with make suite-add TENANT={tenant} SUITE=<suite> REASON=\"...\".")
    else:
        sys.exit(f"Reading the tenant failed ({status}): {existing}")

if action == "sample-records":
    # The unit follows at its next provisioning or update, when the fleet writes its secret.
    status, record = call("PUT", f"/api/v1/tenants/{tenant}/sample-records", body={"enabled": sample_records == "1"}, token=access)
    if status != 200:
        sys.exit(f"Marking the tenant failed ({status}): {record}")
    runs = "runs" if record["sample_records"] else "does not run"
    print(f"Tenant {tenant}'s unit {runs} the sample module from its next update: make update-unit TENANT={tenant}.")
    sys.exit(0)

if action == "administrator-recovery":
    # The reason goes to both audit trails; an empty administrator means the tenant's first one.
    body = {"reason": reason, "login_id": administrator if administrator not in ("", "-") else None, "reset_factors": reset_factors == "1"}
    status, job = call("POST", f"/api/v1/tenants/{tenant}/{action}", body=body, token=access)
elif action == "impersonation":
    # The person to sign in as, why, and for how long; the tenant decides whether its people may be signed in as at all.
    body = {"login_id": administrator, "reason": reason, "minutes": int(minutes) if minutes else None}
    status, job = call("POST", f"/api/v1/tenants/{tenant}/{action}", body=body, token=access)
elif action in ("suite-add", "suite-remove"):
    # The suite's entitlements change with the operator's reason, and the fleet's update job brings the unit along (ADR-0059).
    if action == "suite-add":
        status, change = call("POST", f"/api/v1/tenants/{tenant}/suites", body={"suite": suite, "plan": plan or None, "reason": reason}, token=access)
    else:
        status, change = call("POST", f"/api/v1/tenants/{tenant}/suites/{suite}/remove", body={"reason": reason}, token=access)
    if status == 202:
        entitlements = change["entitlements"]
        verb = "Added" if action == "suite-add" else "Removed"
        plan_named = entitlements["plan_after"] if action == "suite-add" else entitlements["plan_before"]
        print(f"{verb} the suite {suite} ({plan_named}) at revision {entitlements['revision']}.")
        for warning in entitlements.get("warnings", []):
            print(f"  {warning}")
        job = change["job"]
    else:
        job = change
else:
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
handover = job.get("handover") or {}
if handover.get("state") == "given":
    if handover.get("kind") == "impersonation":
        # The operator opens this link themselves: it signs them in as the person, so it goes to nobody else.
        print(f"Your link to sign in as {handover.get('subject')}, shown once and valid until {handover.get('expires_at')}:")
        print(f"  {handover.get('link')}")
        print("Open it now in a private window; it is not shown again, and nothing keeps it.")
    else:
        what = "invitation" if handover.get("kind") == "invitation" else "recovery link"
        print(f"The {what} of {handover.get('subject')}, shown once and valid until {handover.get('expires_at')}:")
        print(f"  {handover.get('link')}")
        print("Give it to them now; it is not shown again, and nothing keeps it.")
elif handover.get("state") == "expired":
    print("The job's link expired before it was taken; ask again.")
PY
