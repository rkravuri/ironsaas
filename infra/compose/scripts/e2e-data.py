#!/usr/bin/env python3
"""The browser test's data on a tenant, through the API (A2): a child unit, a user created in it with a role that
reads users at the unit and its children, and a colleague in the same unit. Since A7 two more roles exist: one that
reviews a unit's access, which the delegation test gives the child user while it runs, and one for the test to grant
while acting for the administrator and to revoke in a review. Safe to run again: what exists is reused.

Reads E2E_TENANT, E2E_ADMIN_PASSWORD and STORE_TOKEN (and E2E_INGRESS, E2E_STORE) from the environment, keeps the
child administrator's login id and password at secret/tenants/<tenant>/e2e, and prints shell exports for make e2e.
"""

import http.client
import json
import os
import secrets
import shlex
import string
import sys
import urllib.parse

TENANT = os.environ["E2E_TENANT"]
HOST = f"{TENANT}.localhost"
ADMIN = f"admin@{TENANT}.localhost"
INGRESS = urllib.parse.urlsplit(os.environ.get("E2E_INGRESS", "http://localhost"))
STORE = urllib.parse.urlsplit(os.environ.get("E2E_STORE", "http://localhost:8200"))
STORE_PATH = f"/v1/secret/data/tenants/{TENANT}/e2e"
UNIT_CODE = "e2e-branch"
UNIT_NAME = "E2E branch"
ROLE_CODE = "e2e-unit-user-reader"
REVIEWER_ROLE_CODE = "e2e-unit-reviewer"
REVOCABLE_ROLE_CODE = "e2e-revocable"


def log(message):
    print(message, file=sys.stderr)


def send(target, method, path, body=None, headers=None):
    """One request; the ingress picks the tenant by the Host header, as the smoke does."""
    connection = http.client.HTTPConnection(target.hostname, target.port or 80, timeout=30)
    try:
        payload = None if body is None else json.dumps(body)
        sent = {"Accept": "application/json", **(headers or {})}
        if payload is not None:
            sent["Content-Type"] = "application/json"
        connection.request(method, path, body=payload, headers=sent)
        response = connection.getresponse()
        raw = response.read()
        data = json.loads(raw) if raw else None
        return response.status, data, response.getheader("Set-Cookie")
    finally:
        connection.close()


def api(method, path, cookie, body=None, expect=(200,)):
    status, data, _ = send(INGRESS, method, path, body, {"Host": HOST, "Cookie": cookie})
    if status not in expect:
        raise SystemExit(f"{method} {path} answered {status}: {data}")
    return data


def login(login_id, password):
    status, _, cookie = send(INGRESS, "POST", "/auth/login", {"login_id": login_id, "password": password}, {"Host": HOST})
    return cookie.split(";", 1)[0] if status == 200 and cookie else None


def store_read():
    status, data, _ = send(STORE, "GET", STORE_PATH, headers={"X-Vault-Token": os.environ["STORE_TOKEN"]})
    return data["data"]["data"] if status == 200 else None


def store_write(values):
    status, data, _ = send(STORE, "POST", STORE_PATH, {"data": values}, {"X-Vault-Token": os.environ["STORE_TOKEN"]})
    if status not in (200, 204):
        raise SystemExit(f"the store refused the e2e secret: {status} {data}")


def password():
    alphabet = string.ascii_letters + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(24))


def create_user(cookie, login_id, display_name, unit_id):
    created = api("POST", "/api/v1/users", cookie, {"login_id": login_id, "display_name": display_name, "home_unit_id": unit_id}, expect=(201,))
    return created["user"]["id"], created["invitation_link"]


def ensure_role(cookie, code, name, description, permissions):
    """The role with this code, created once."""
    found = api("GET", f"/api/v1/roles?filter=code:eq:{code}", cookie)["data"]
    if found:
        return found[0]
    created = api("POST", "/api/v1/roles", cookie, {"code": code, "name": name, "description": description, "principal_type": "user", "permissions": permissions}, expect=(201,))
    log(f"Created the role {code}.")
    return created


def main():
    admin = login(ADMIN, os.environ["E2E_ADMIN_PASSWORD"])
    if admin is None:
        raise SystemExit(f"{ADMIN} cannot sign in on {HOST}: run make smoke TENANT={TENANT} first.")
    me = api("GET", "/api/v1/me", admin)

    units = api("GET", "/api/v1/organisation-units", admin)["data"]
    root = next(unit for unit in units if unit["parent_id"] is None)
    child = next((unit for unit in units if unit["code"] == UNIT_CODE), None)
    if child is None:
        child = api("POST", f"/api/v1/organisation-units/{root['id']}/children", admin, {"name": UNIT_NAME, "code": UNIT_CODE, "kind": "unit"}, expect=(201,))
        log(f"Created the unit {UNIT_NAME} under {root['name']}.")

    role = ensure_role(admin, ROLE_CODE, "Unit user reader", "Reads the users of a unit and the units below it.", ["identity.users.read"])
    reviewer = ensure_role(admin, REVIEWER_ROLE_CODE, "Unit access reviewer", "Confirms or revokes the roles held in a unit and the units below it.", ["authz.reviews.decide"])
    revocable = ensure_role(admin, REVOCABLE_ROLE_CODE, "Revocable in review", "Granted and revoked by the browser test of access reviews.", ["authz.units.read"])

    kept = store_read()
    if kept and login(kept["login_id"], kept["password"]):
        child_login, child_password = kept["login_id"], kept["password"]
        log(f"Reusing {child_login}.")
    else:
        # A user cannot be given a password again before S3's reset flow, so a lost one means a new user.
        child_login, child_password = f"child-admin-{secrets.token_hex(3)}@{TENANT}.localhost", password()
        _, link = create_user(admin, child_login, "Child administrator", child["id"])
        status, data, _ = send(INGRESS, "POST", f"/auth{link}/accept", {"password": child_password}, {"Host": HOST})
        if status != 200:
            raise SystemExit(f"the invitation of {child_login} was not accepted: {status} {data}")
        store_write({"login_id": child_login, "password": child_password})
        log(f"Created {child_login} in {UNIT_NAME} and kept the password at secret/tenants/{TENANT}/e2e.")

    found = api("GET", f"/api/v1/users?filter=login_id:eq:{urllib.parse.quote(child_login)}&fields=id", admin)["data"]
    child_id = found[0]["id"]
    colleagues = api("GET", f"/api/v1/users?filter=login_id:startswith:colleague-&filter=home_unit_id:eq:{child['id']}&fields=id", admin)["data"]
    if not colleagues:
        colleague_id, _ = create_user(admin, f"colleague-{secrets.token_hex(3)}@{TENANT}.localhost", "A colleague", child["id"])
        log(f"Created a colleague in {UNIT_NAME}.")
    else:
        colleague_id = colleagues[0]["id"]

    api("POST", "/api/v1/role-assignments", admin, {"principal_id": child_id, "role_id": role["id"], "scope_kind": "unit_with_children", "unit_id": child["id"]}, expect=(200, 201))
    log(f"{child_login} reads the users of {UNIT_NAME} and below.")

    exports = {
        "E2E_CHILD_LOGIN_ID": child_login,
        "E2E_CHILD_PASSWORD": child_password,
        "E2E_OUTSIDE_USER_ID": me["id"],
        "E2E_CHILD_UNIT": UNIT_NAME,
        "E2E_CHILD_UNIT_ID": child["id"],
        "E2E_CHILD_ID": child_id,
        "E2E_COLLEAGUE_ID": colleague_id,
        "E2E_REVOCABLE_ROLE_ID": revocable["id"],
        "E2E_REVIEWER_ROLE_ID": reviewer["id"],
    }
    for name, value in exports.items():
        print(f"export {name}={shlex.quote(value)}")


if __name__ == "__main__":
    main()
