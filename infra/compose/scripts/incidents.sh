#!/usr/bin/env bash
# Incidents (TF-079 AC2, ADR-0056) as an operator, with client credentials against the control plane, by the runbook
# docs/security/incident-response.md:
#   incidents.sh declare            SEVERITY (1 to 4), SUMMARY, LEAD, [COMMS], [SCRIBE], [TENANTS=a,b], [PERSONAL_DATA=1],
#                                   [KNOWN_AT=2026-10-29T08:00:00Z]: declares it and prints its id and timeline;
#   incidents.sh note <id>          TEXT, [KIND=note|communication|notice], [TENANT]: a notice is the breach notice sent
#                                   to TENANT; with TENANT and PERSONAL_DATA=1 and no TEXT, names the tenant affected;
#   incidents.sh close <id>         SUMMARY: closes it once every tenant whose personal data was affected is told;
#   incidents.sh review <id>        WHAT, WHY, CHANGES: the post-incident review, within 10 days of the close;
#   incidents.sh show <id>          the incident, each tenant's 72-hour deadline, and the timeline;
#   incidents.sh list               the incidents, newest first.
# Every step is audited in the control plane with the operator.
set -euo pipefail

action="${1:?usage: incidents.sh declare|note|close|review|show|list [id]}"
id="${2:-}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"

python3 - "$control" "$client_id" "$secret" "$action" "$id" <<'PY'
import base64, json, os, sys, urllib.error, urllib.request

control, client_id, secret, action, incident = sys.argv[1:]
env = os.environ.get

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
    errors = answer.get("errors") if isinstance(answer, dict) else None
    sys.exit(f"{what} was refused ({status}): {detail}" + (f" {errors}" if errors else ""))

def need(name):
    value = env(name, "")
    if not value.strip():
        sys.exit(f"{name} is required for '{action}': see the usage in make help.")
    return value

def show(item):
    print(f"Incident {item['id']}: severity {item['severity']}, {item['status']}")
    print(f"  {item['summary']}")
    print(f"  Lead {item['lead']}; communications {item.get('communications') or 'not named'}; scribe {item.get('scribe') or 'not named'}")
    print(f"  Declared {item['declared_at']}" + (f", closed {item['closed_at']}: {item.get('close_summary')}" if item.get("closed_at") else ""))
    if item.get("review_due_at"):
        print(f"  Review due by {item['review_due_at']}: {item['review_state']}")
    for tenant in item["tenants"]:
        if tenant["personal_data"]:
            told = f", told {tenant['notified_at']}" if tenant.get("notified_at") else ""
            print(f"  Tenant {tenant['tenant']}: personal data affected since {tenant['known_at']}; notice due by {tenant['notice_due_at']}{told} ({tenant['notice_state']})")
        else:
            print(f"  Tenant {tenant['tenant']}: affected, not its personal data")
    print("  Timeline:")
    for entry in item["timeline"]:
        where = f" [{entry['tenant']}]" if entry.get("tenant") else ""
        print(f"    {entry['at']}  {entry['kind']:13} {entry['by']}{where}: {entry['text']}")
    if item.get("review"):
        review = item["review"]
        print(f"  Review ({'within' if review['on_time'] else 'after'} its 10 days, {review['recorded_at']}):")
        print(f"    What happened: {review['what_happened']}")
        print(f"    Why: {review['why']}")
        print(f"    What changes: {review['what_changes']}")

status, token = call("POST", "/connect/token", form={"grant_type": "client_credentials"})
if status != 200:
    sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
access_token = token["access_token"]

if action == "list":
    status, page = call("GET", "/api/v1/incidents?page_size=50", token=access_token)
    if status != 200:
        refused("Listing the incidents", status, page)
    for item in page["data"]:
        print(f"{item['id']}  severity {item['severity']}  {item['status']:6}  {item['declared_at']}  {item['summary']}" + ("  (reviewed)" if item["reviewed"] else ""))
    sys.exit(0)

if action == "declare":
    severity = need("SEVERITY")
    if not severity.isdigit():
        sys.exit("SEVERITY is 1, the most severe, to 4.")
    personal = env("PERSONAL_DATA", "0") in ("1", "true", "yes")
    tenants = [{"tenant": t.strip(), "personal_data": personal, "known_at": env("KNOWN_AT") or None} for t in env("TENANTS", "").split(",") if t.strip()]
    body = {"severity": int(severity), "summary": need("SUMMARY"), "lead": need("LEAD"), "communications": env("COMMS") or None, "scribe": env("SCRIBE") or None, "tenants": tenants}
    status, item = call("POST", "/api/v1/incidents", body=body, token=access_token)
    if status != 201:
        refused("Declaring the incident", status, item)
    show(item)
    sys.exit(0)

if not incident:
    sys.exit(f"'{action}' needs the incident's id: ID=<id>.")

if action == "show":
    status, item = call("GET", f"/api/v1/incidents/{incident}", token=access_token)
elif action == "note":
    tenant = env("TENANT") or None
    if tenant and not env("TEXT"):
        body = {"tenant": tenant, "personal_data": env("PERSONAL_DATA", "0") in ("1", "true", "yes"), "known_at": env("KNOWN_AT") or None}
        status, item = call("POST", f"/api/v1/incidents/{incident}/tenants", body=body, token=access_token)
    else:
        status, item = call("POST", f"/api/v1/incidents/{incident}/entries", body={"kind": env("KIND", "note"), "text": need("TEXT"), "tenant": tenant}, token=access_token)
elif action == "close":
    status, item = call("POST", f"/api/v1/incidents/{incident}/close", body={"summary": need("SUMMARY")}, token=access_token)
elif action == "review":
    status, item = call("POST", f"/api/v1/incidents/{incident}/review", body={"what_happened": need("WHAT"), "why": need("WHY"), "what_changes": need("CHANGES")}, token=access_token)
else:
    sys.exit(f"Unknown action '{action}': declare, note, close, review, show or list.")

if status != 200:
    refused(f"The incident's {action}", status, item)
show(item)
PY
