#!/usr/bin/env bash
# The security alerts the control plane holds (ADR-0056), as an operator: its own and the units' copies.
#   security-alerts.sh list           [TENANT], [STATE=open|acknowledged|resolved]: newest first;
#   security-alerts.sh resolve <id>   NOTE: resolves one of the control plane's own alerts; a unit's alert is resolved
#                                     in the unit, where its events are, and its copy follows.
set -euo pipefail

action="${1:?usage: security-alerts.sh list|resolve [id]}"
id="${2:-}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"

python3 - "$control" "$client_id" "$secret" "$action" "$id" <<'PY'
import base64, json, os, sys, urllib.error, urllib.parse, urllib.request

control, client_id, secret, action, alert = sys.argv[1:]

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
access_token = token["access_token"]

if action == "list":
    filters = [("page_size", "50")]
    if os.environ.get("TENANT"):
        filters.append(("filter", f"tenant:eq:{os.environ['TENANT']}"))
    if os.environ.get("STATE"):
        filters.append(("filter", f"state:eq:{os.environ['STATE']}"))
    status, page = call("GET", "/api/v1/alerts?" + urllib.parse.urlencode(filters), token=access_token)
    if status != 200:
        sys.exit(f"Listing the alerts was refused ({status}): {page}")
    for item in page["data"]:
        what = item.get("reason") if item["kind"] == "failing" else f"{item['count']} for {item['group_kind']} {item['group_value']} within {item['window_minutes']} min"
        print(f"{item['id']}  {item['raised_at'][:19]}  {item['tenant']:12} {item['severity']:8} {item['state']:12} {item['rule']}: {what}")
    sys.exit(0)

if action == "resolve":
    if not alert or not os.environ.get("NOTE"):
        sys.exit("usage: make security-alert-resolve ID=<id> NOTE=\"what you found and did\"")
    status, item = call("POST", f"/api/v1/alerts/{alert}/resolve", body={"note": os.environ["NOTE"]}, token=access_token)
    if status != 200:
        detail = item.get("detail") if isinstance(item, dict) else item
        sys.exit(f"Resolving the alert was refused ({status}): {detail}")
    print(f"Resolved {item['id']} ({item['rule']}).")
    sys.exit(0)

sys.exit(f"Unknown action '{action}': list or resolve.")
PY
