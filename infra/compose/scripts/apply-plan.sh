#!/usr/bin/env bash
# Applies a plan to a tenant in the control plane as an operator (client credentials): first the preview (what is added,
# removed and changed, and the warnings), then the apply, then the revision the store holds (ADR-0034).
# Usage: apply-plan.sh <tenant> <plan> [starts_on] [ends_on]   with CONTROL_URL and OPERATOR_SECRET in the environment.
# DRY_RUN=1 shows the preview only.
set -euo pipefail

tenant="$1"
plan="$2"
starts_on="${3:-}"
ends_on="${4:-}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"
dry_run="${DRY_RUN:-0}"

python3 - "$control" "$client_id" "$secret" "$tenant" "$plan" "$starts_on" "$ends_on" "$dry_run" <<'PY'
import base64, json, sys, urllib.error, urllib.request

control, client_id, secret, tenant, plan, starts_on, ends_on, dry_run = sys.argv[1:]

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

def value(v):
    if v is None:
        return "none"
    parts = [v["status"]]
    if v.get("user_limit") is not None:
        parts.append(f"{v['user_limit']} seats")
    parts.append(f"from {v['starts_on']}")
    if v.get("ends_on"):
        parts.append(f"until {v['ends_on']}")
    return ", ".join(parts)

def show(preview):
    print(f"  plan        {preview['plan_before']} -> {preview['plan_after']}")
    if preview["grace_days_before"] != preview["grace_days_after"]:
        print(f"  grace days  {preview['grace_days_before']} -> {preview['grace_days_after']}")
    for change in preview["added"]:
        print(f"  added       {change['kind']:7} {change['code']}: {value(change['after'])}")
    for change in preview["removed"]:
        print(f"  removed     {change['kind']:7} {change['code']}: was {value(change['before'])}")
    for change in preview["changed"]:
        print(f"  changed     {change['kind']:7} {change['code']}: {value(change['before'])} -> {value(change['after'])}")
    if not (preview["added"] or preview["removed"] or preview["changed"]) and preview["plan_before"] == preview["plan_after"] and preview["grace_days_before"] == preview["grace_days_after"]:
        print("  nothing changes")
    for warning in preview["warnings"]:
        print(f"  warning     {warning}")

status, token = call("POST", "/connect/token", form={"grant_type": "client_credentials"})
if status != 200:
    sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
access = token["access_token"]

body = {"plan": plan.lower(), "dry_run": True}
if starts_on:
    body["starts_on"] = starts_on
if ends_on:
    body["ends_on"] = ends_on

status, preview = call("POST", f"/api/v1/tenants/{tenant}/plan", body=body, token=access)
if status != 200:
    detail = preview.get("detail") if isinstance(preview, dict) else preview
    sys.exit(f"The preview of plan {plan} for {tenant} was refused ({status}): {detail}")
print(f"Preview of plan {plan.lower()} for tenant {tenant}:")
show(preview)
if dry_run == "1":
    sys.exit(0)

body["dry_run"] = False
status, applied = call("POST", f"/api/v1/tenants/{tenant}/plan", body=body, token=access)
if status != 200:
    detail = applied.get("detail") if isinstance(applied, dict) else applied
    sys.exit(f"Applying plan {plan} to {tenant} was refused ({status}): {detail}")

status, current = call("GET", f"/api/v1/tenants/{tenant}/entitlements", token=access)
if status != 200:
    sys.exit(f"Reading {tenant}'s entitlements failed ({status}): {current}")
store = "the store holds it" if current["published_revision"] >= current["revision"] else "the store is behind; the fleet writes it within a minute"
print(f"Applied: {tenant} is on plan {current['plan']}, revision {current['revision']}, and {store}. The unit learns it within 30 seconds, the shell within a minute.")
PY
