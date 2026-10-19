#!/bin/bash
# Sets one flag rule in the control plane as an operator (client credentials), or flips a kill switch, and prints the rule.
#   flag-rule.sh flag <key> <value> [tenant] [reason]      the unconditional rule for the tenant, or for the platform
#   flag-rule.sh kill <path> <on: true|false> <reason> [tenant]
# CONTROL_URL and OPERATOR_SECRET come from the environment (make flag, make kill-switch; ADR-0035).
# make kill-switch PATH=imports overrides PATH in every recipe, so this script sets the one it needs first.
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH
set -euo pipefail

control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"

python3 - "$control" "$client_id" "$secret" "$@" <<'PY'
import base64, json, sys, urllib.error, urllib.request

control, client_id, secret, action, *args = sys.argv[1:]

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

def refused(what, status, body):
    detail = body.get("detail") if isinstance(body, dict) else body
    sys.exit(f"{what} was refused ({status}): {detail}")

def describe(rule):
    target = {"global": "the platform", "region": f"region {rule.get('region')}", "tenant": f"tenant {rule.get('tenant')}",
              "unit": f"unit {rule.get('unit_id')} of {rule.get('tenant')}", "user": f"user {rule.get('user_id')} of {rule.get('tenant')}"}[rule["scope"]]
    condition = rule.get("condition") or {}
    when = ""
    if condition.get("kind") == "percentage":
        when = f" for {condition['percentage']}% of {condition['of']}s"
    elif condition.get("kind") == "attribute":
        when = f" where {condition['attribute']} is one of {', '.join(condition['values'])}"
    reason = f" (reason: {rule['reason']})" if rule.get("reason") else ""
    return f"{rule['flag']} = {json.dumps(rule['value'])} for {target}{when}{reason}"

status, token = call("POST", "/connect/token", form={"grant_type": "client_credentials"})
if status != 200:
    sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
access = token["access_token"]

if action == "kill":
    path, on, reason, *rest = args
    tenant = rest[0] if rest and rest[0] else None
    status, body = call("PUT", f"/api/v1/kill-switches/{path}", body={"on": on.lower() == "true", "reason": reason, "tenant": tenant}, token=access)
    if status != 200:
        refused(f"Flipping the kill switch {path}", status, body)
    where = f"tenant {tenant}" if tenant else "the platform"
    print(f"Kill switch {path} is {'ON' if on.lower() == 'true' else 'off'} for {where}: {reason}")
else:
    key, raw, *rest = args
    tenant = rest[0] if rest and rest[0] else None
    reason = rest[1] if len(rest) > 1 and rest[1] else None
    try:
        value = json.loads(raw)
    except json.JSONDecodeError:
        value = raw  # a bare word is a string value
    status, current = call("GET", f"/api/v1/flags/{key}/rules", token=access)
    if status != 200:
        refused(f"Reading the rules of {key}", status, current)
    scope = "tenant" if tenant else "global"
    kept = [r for r in current["rules"] if not (r["scope"] == scope and r.get("tenant") == tenant and (r.get("condition") or {}).get("kind", "always") == "always")]
    wanted = {"scope": scope, "tenant": tenant, "value": value, "reason": reason}
    rules = [wanted] + [{k: r.get(k) for k in ("scope", "region", "tenant", "unit_id", "user_id", "condition", "value", "reason")} for r in kept]
    status, body = call("PUT", f"/api/v1/flags/{key}/rules", body={"revision": current["revision"], "reason": reason, "rules": rules}, token=access)
    if status != 200:
        refused(f"Setting the rules of {key}", status, body)
    print(f"Set: {describe(body['rules'][0])}. The flag has {len(body['rules'])} rule(s) at revision {body['revision']}; units read it within 30 seconds.")
PY
