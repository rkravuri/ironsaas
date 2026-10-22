#!/usr/bin/env bash
# Break-glass (TF-026, ADR-0049) as an operator, with client credentials against the control plane:
#   break-glass.sh request <tenant>   with TICKET, REASON and MINUTES (60 by default, 240 at most): prints the request's id;
#   break-glass.sh approve <id>       as a second operator (OPERATOR_CLIENT_ID=operator-two), never the one who asked;
#   break-glass.sh link <id>          the operator who asked: the account's one-time sign-in link, once, after approval;
#   break-glass.sh end <id>           ends the access before its time; the review follows;
#   break-glass.sh show <id>          where it stands, and its review once it ended.
# Every token is fresh, so the approval's step-up holds (ADR-0047). The link is printed once and kept nowhere (ADR-0046).
set -euo pipefail

action="${1:?usage: break-glass.sh request|approve|link|end|show <tenant or id>}"
subject="${2:?usage: break-glass.sh request|approve|link|end|show <tenant or id>}"
control="${CONTROL_URL:-http://localhost:5200}"
client_id="${OPERATOR_CLIENT_ID:-operator}"
secret="${OPERATOR_SECRET:?OPERATOR_SECRET is required}"

python3 - "$control" "$client_id" "$secret" "$action" "$subject" "${TICKET:-}" "${REASON:-}" "${MINUTES:-}" <<'PY'
import base64, json, sys, time, urllib.error, urllib.request

control, client_id, secret, action, subject, ticket, reason, minutes = sys.argv[1:]

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

def show(access):
    print(f"Break-glass {access['id']} for tenant {access['tenant_slug']}: {access['status']}.")
    print(f"  asked by {access['requested_by']} at {access['requested_at']}, ticket {access['ticket']}: {access['reason']}")
    if access.get("approved_by"):
        print(f"  approved by {access['approved_by']} at {access['approved_at']}; the account {access['account']} works until {access['access_until']}")
    elif access["status"] == "requested":
        print(f"  waiting for a second operator until {access['approval_deadline']}")
    if access.get("end_reason"):
        print(f"  ended: {access['end_reason']}" + (f" at {access['ended_at']}" if access.get("ended_at") else ""))
    for entry in access.get("review") or []:
        print(f"  {entry['at']}  {entry['event_type']:40} {entry.get('outcome') or ''} {entry.get('target') or ''}")
    if access.get("review_actions") is not None:
        print(f"  the review lists {access['review_actions']} action(s)")
    handover = access.get("handover") or {}
    if handover.get("state") == "given":
        # Reading the access as the operator who asked takes the link: it is shown here, once.
        print(f"  the sign-in link of {handover.get('subject')}, shown once and valid until {handover.get('expires_at')}:")
        print(f"    {handover.get('link')}")

status, token = call("POST", "/connect/token", form={"grant_type": "client_credentials"})
if status != 200:
    sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
access_token = token["access_token"]

if action == "request":
    body = {"reason": reason, "ticket": ticket, "minutes": int(minutes) if minutes else None}
    status, access = call("POST", f"/api/v1/tenants/{subject}/break-glass", body=body, token=access_token)
    if status != 201:
        refused("The request", status, access)
    show(access)
    print(f"A second operator approves it within 30 minutes: make break-glass-approve ID={access['id']}")
elif action == "approve":
    status, access = call("POST", f"/api/v1/break-glass/{subject}/approve", token=access_token)
    if status != 200:
        refused("The approval", status, access)
    show(access)
    print(f"The operator who asked takes the sign-in link once: make break-glass-link ID={access['id']}")
elif action == "link":
    deadline = time.time() + 300
    while time.time() < deadline:
        status, access = call("GET", f"/api/v1/break-glass/{subject}", token=access_token)
        if status != 200:
            refused("Reading the access", status, access)
        handover = access.get("handover") or {}
        if handover.get("state") == "given":
            print(f"The sign-in link of {handover.get('subject')}, shown once and valid until {handover.get('expires_at')}:")
            print(f"  {handover.get('link')}")
            print("Open it now in a private window; it is not shown again, and nothing keeps it.")
            break
        if handover.get("state") in ("taken", "expired", "not_yours"):
            sys.exit(f"No link: it was {handover['state'].replace('_', ' ')}. Only the operator who asked takes it, once, within 15 minutes.")
        if access["status"] not in ("active",):
            sys.exit(f"No link: the access is {access['status']}.")
        time.sleep(3)
    else:
        sys.exit("The fleet has not opened the account within 5 minutes; check the fleet worker's log.")
elif action == "end":
    status, access = call("POST", f"/api/v1/break-glass/{subject}/end", token=access_token)
    if status != 200:
        refused("Ending the access", status, access)
    show(access)
    print("The fleet closes the account within a minute or two; make break-glass-show shows its review then.")
elif action == "show":
    status, access = call("GET", f"/api/v1/break-glass/{subject}", token=access_token)
    if status != 200:
        refused("Reading the access", status, access)
    show(access)
else:
    sys.exit(f"Unknown action {action}: request, approve, link, end or show.")
PY
