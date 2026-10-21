#!/usr/bin/env python3
"""The browser tests' data on a tenant, through the API: every run works in a unit of its own (S2-close), so what an
earlier run left behind never meets a later one.

setup (the default) creates, under the root, the run's unit "E2E run <run>" and inside it three branches named with the
run's id: the first holds the child-unit administrator (who reads the users of the branch and below) and a colleague;
the second and third each hold a colleague, and a person homed at the second reads the users of both, so the header
offers them a choice of unit (A8). They hold no role in the first branch, whose access review the delegation test runs
(A7). In the run's unit itself, outside the branches, a person whose password the recovery test sets with a link and
whom it then deactivates (S3 T2). Every person is new and signs in with a password made here. It prints shell exports
for make e2e.

teardown deactivates the people homed in the run's units, those of earlier runs that are still active too (runs before
S3 T2 could not deactivate anyone), then the run's units, the deepest first (E2E_RUN_UNIT_ID). make e2e runs it whatever
the tests' result, so a run leaves no active people behind.

The three roles the tests grant are the tenant's, not a run's: they are created once and kept as they are.

Reads E2E_TENANT and E2E_ADMIN_PASSWORD (and E2E_INGRESS) from the environment.
"""

import http.client
import json
import os
import secrets
import shlex
import string
import sys
import time
import urllib.parse

TENANT = os.environ["E2E_TENANT"]
HOST = f"{TENANT}.localhost"
ADMIN = f"admin@{TENANT}.localhost"
INGRESS = urllib.parse.urlsplit(os.environ.get("E2E_INGRESS", "http://localhost"))
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


def administrator():
    admin = login(ADMIN, os.environ["E2E_ADMIN_PASSWORD"])
    if admin is None:
        raise SystemExit(f"{ADMIN} cannot sign in on {HOST}: run make smoke TENANT={TENANT} first.")
    return admin


def password():
    alphabet = string.ascii_letters + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(24))


def run_id():
    """The run's short id: base 36 of the time, as the browser tests stamp what they create."""
    digits = "0123456789abcdefghijklmnopqrstuvwxyz"
    value, text = int(time.time() * 1000), ""
    while value:
        value, digit = divmod(value, 36)
        text = digits[digit] + text
    return text


def create_unit(cookie, parent_id, name, code):
    return api("POST", f"/api/v1/organisation-units/{parent_id}/children", cookie, {"name": name, "code": code, "kind": "unit"}, expect=(201,))


def create_user(cookie, login_id, display_name, unit_id):
    created = api("POST", "/api/v1/users", cookie, {"login_id": login_id, "display_name": display_name, "home_unit_id": unit_id}, expect=(201,))
    return created["user"]["id"], created["invitation_link"]


def create_person(cookie, run, prefix, display_name, unit):
    """A person homed at the unit who signs in: invited, then accepted with a password made here."""
    login_id, secret = f"{prefix}-{run}@{TENANT}.localhost", password()
    person_id, link = create_user(cookie, login_id, display_name, unit["id"])
    # The token is in the link's fragment, which no server receives; it goes in the body (ADR-0046).
    token = link.split("#", 1)[1]
    status, data, _ = send(INGRESS, "POST", "/auth/invitations/accept", {"token": token, "password": secret}, {"Host": HOST})
    if status != 200:
        raise SystemExit(f"the invitation of {login_id} was not accepted: {status} {data}")
    log(f"Created {login_id} in {unit['name']}.")
    return person_id, login_id, secret


def ensure_role(cookie, code, name, description, permissions):
    """The role with this code, created once and kept as it is."""
    found = api("GET", f"/api/v1/roles?filter=code:eq:{code}", cookie)["data"]
    if found:
        return found[0]
    created = api("POST", "/api/v1/roles", cookie, {"code": code, "name": name, "description": description, "principal_type": "user", "permissions": permissions}, expect=(201,))
    log(f"Created the role {code}.")
    return created


def grant(cookie, principal_id, role_id, scope_kind, unit_id):
    api("POST", "/api/v1/role-assignments", cookie, {"principal_id": principal_id, "role_id": role_id, "scope_kind": scope_kind, "unit_id": unit_id}, expect=(200, 201))


def setup():
    admin = administrator()
    me = api("GET", "/api/v1/me", admin)
    run = run_id()

    units = api("GET", "/api/v1/organisation-units", admin)["data"]
    root = next(unit for unit in units if unit["parent_id"] is None)
    run_unit = create_unit(admin, root["id"], f"E2E run {run}", f"e2e-run-{run}")
    child = create_unit(admin, run_unit["id"], f"E2E branch {run}", f"e2e-branch-{run}")
    second = create_unit(admin, run_unit["id"], f"E2E second branch {run}", f"e2e-second-{run}")
    third = create_unit(admin, run_unit["id"], f"E2E third branch {run}", f"e2e-third-{run}")
    log(f"Created the run's unit {run_unit['name']} with its three branches.")

    role = ensure_role(admin, ROLE_CODE, "Unit user reader", "Reads the users of a unit and the units below it.", ["identity.users.read"])
    reviewer = ensure_role(admin, REVIEWER_ROLE_CODE, "Unit access reviewer", "Confirms or revokes the roles held in a unit and the units below it.", ["authz.reviews.decide"])
    revocable = ensure_role(admin, REVOCABLE_ROLE_CODE, "Revocable in review", "Granted and revoked by the browser test of access reviews.", ["authz.units.read"])

    child_id, child_login, child_password = create_person(admin, run, "child-admin", "Child administrator", child)
    two_id, two_login, two_password = create_person(admin, run, "two-units", "Two-unit reader", second)
    recover_id, recover_login, _ = create_person(admin, run, "recovering", "Recovering person", run_unit)
    colleague_id, _ = create_user(admin, f"colleague-{run}@{TENANT}.localhost", "A colleague", child["id"])
    for unit, prefix in ((second, "second-colleague"), (third, "third-colleague")):
        create_user(admin, f"{prefix}-{run}@{TENANT}.localhost", f"A colleague in {unit['name']}", unit["id"])
    log("Created a colleague in each branch.")

    grant(admin, child_id, role["id"], "unit_with_children", child["id"])
    # The two-unit reader's roles are in the second and third branches only: a role in the first would be an item of
    # its access review.
    for unit in (second, third):
        grant(admin, two_id, role["id"], "unit", unit["id"])
    log(f"{child_login} reads the users of {child['name']} and below; {two_login} those of the second and third branches.")

    exports = {
        "E2E_RUN": run,
        "E2E_RUN_UNIT": run_unit["name"],
        "E2E_RUN_UNIT_ID": run_unit["id"],
        "E2E_CHILD_LOGIN_ID": child_login,
        "E2E_CHILD_PASSWORD": child_password,
        "E2E_OUTSIDE_USER_ID": me["id"],
        "E2E_CHILD_UNIT": child["name"],
        "E2E_CHILD_UNIT_ID": child["id"],
        "E2E_CHILD_ID": child_id,
        "E2E_COLLEAGUE_ID": colleague_id,
        "E2E_REVOCABLE_ROLE_ID": revocable["id"],
        "E2E_REVIEWER_ROLE_ID": reviewer["id"],
        "E2E_TWO_UNITS_LOGIN_ID": two_login,
        "E2E_TWO_UNITS_PASSWORD": two_password,
        "E2E_SECOND_UNIT": second["name"],
        "E2E_THIRD_UNIT": third["name"],
        "E2E_RECOVER_ID": recover_id,
        "E2E_RECOVER_LOGIN_ID": recover_login,
    }
    for name, value in exports.items():
        print(f"export {name}={shlex.quote(value)}")


def deactivate_people(admin, units):
    """Every active or invited person homed at one of these units, deactivated: they cannot sign in, their sessions end."""
    ids = [unit["id"] for unit in units]
    left = 0
    for start in range(0, len(ids), 50):
        chosen = urllib.parse.quote("home_unit_id:in:" + ",".join(ids[start:start + 50]))
        people = api("GET", f"/api/v1/users?page_size=200&fields=id,status&filter={chosen}", admin)["data"]
        for person in people:
            if person["status"] != "disabled":
                api("POST", f"/api/v1/users/{person['id']}/deactivate", admin, expect=(200,))
                left += 1
    return left


def teardown():
    run_unit_id = os.environ.get("E2E_RUN_UNIT_ID", "")
    admin = administrator()
    units = api("GET", "/api/v1/organisation-units", admin)["data"]
    by_id = {unit["id"]: unit for unit in units}
    # The people of every run's units, this one's and earlier ones', by the codes setup gives them.
    runs = [unit for unit in units if unit["code"].startswith("e2e-")]
    log(f"Deactivated {deactivate_people(admin, runs)} person(s) homed in the browser tests' units.")
    if not run_unit_id:
        log("No run unit to deactivate.")
        return
    if run_unit_id not in by_id:
        log(f"The run unit {run_unit_id} is not in the tree.")
        return

    def inside(unit):
        while unit is not None:
            if unit["id"] == run_unit_id:
                return True
            unit = by_id.get(unit["parent_id"])
        return False

    # The deepest first: a unit with an active unit under it is not deactivated.
    for unit in sorted((unit for unit in units if inside(unit)), key=lambda unit: unit["depth"], reverse=True):
        if unit["status"] == "active":
            api("POST", f"/api/v1/organisation-units/{unit['id']}/deactivate", admin, expect=(200,))
    log(f"Deactivated {by_id[run_unit_id]['name']} and the units under it.")


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else "setup"
    if command == "setup":
        setup()
    elif command == "teardown":
        teardown()
    else:
        raise SystemExit("usage: e2e-data.py [setup|teardown]")
