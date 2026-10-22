#!/usr/bin/env python3
"""Accepts the current versions of the documents a tenant asks of its people (TF-028, ADR-0049), as the laptop's stored
test administrator: admin@<tenant>.localhost, with the password and the authenticator app's key make smoke keeps at
secret/tenants/<tenant>/smoke.

Run it deliberately, after make platform-document or a tenant document published by hand, so the smoke and the browser
tests can sign the administrator in again: they never accept a document themselves. It accepts only what the
administrator is asked to accept now, prints each version, and accepts nothing for anyone else.

Reads ACCEPT_TENANT, ACCEPT_ADMIN_PASSWORD and ACCEPT_ADMIN_TOTP_SECRET (and ACCEPT_INGRESS) from the environment.
"""

import base64
import hashlib
import hmac
import http.client
import json
import os
import re
import struct
import time
import urllib.parse

TENANT = os.environ["ACCEPT_TENANT"]
HOST = f"{TENANT}.localhost"
ADMIN = f"admin@{TENANT}.localhost"
INGRESS = urllib.parse.urlsplit(os.environ.get("ACCEPT_INGRESS", "http://localhost"))


def send(method, path, body=None, cookie=None):
    """One request; the ingress picks the tenant by the Host header, as the smoke does."""
    connection = http.client.HTTPConnection(INGRESS.hostname, INGRESS.port or 80, timeout=30)
    try:
        headers = {"Accept": "application/json", "Host": HOST}
        if cookie:
            headers["Cookie"] = cookie
        payload = None if body is None else json.dumps(body)
        if payload is not None:
            headers["Content-Type"] = "application/json"
        connection.request(method, path, body=payload, headers=headers)
        response = connection.getresponse()
        raw = response.read()
        return response.status, (json.loads(raw) if raw else None), response.getheader("Set-Cookie")
    finally:
        connection.close()


def cookie_named(header, name):
    """One cookie's name=value from a Set-Cookie header, which may hold several (a cleared sign-in cookie and the session)."""
    found = re.search(rf"(?:^|[,\s]){re.escape(name)}=([^;,\s]+)", header or "")
    return f"{name}={found.group(1)}" if found else None


def totp(secret, step):
    """RFC 6238's code for a step: HMAC-SHA1 of the step, six digits (ADR-0047)."""
    key = base64.b32decode(secret.upper() + "=" * (-len(secret) % 8))
    digest = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF
    return f"{value % 1_000_000:06d}"


def sign_in():
    """The administrator's session: the password, then the app's code. A step another process used is refused as any
    wrong code is, so the next step is tried, at most three times, under the three that start a cooling period."""
    status, data, header = send("POST", "/auth/login", {"login_id": ADMIN, "password": os.environ["ACCEPT_ADMIN_PASSWORD"]})
    if status != 200:
        raise SystemExit(f"{ADMIN} cannot sign in on {HOST} ({status}): run make smoke TENANT={TENANT} first.")
    if not (data or {}).get("step"):
        return cookie_named(header, "iron.session")
    secret = os.environ.get("ACCEPT_ADMIN_TOTP_SECRET") or ""
    if data["step"]["kind"] != "second_factor" or not secret:
        raise SystemExit(f"{ADMIN} must give a second factor and no key for it is kept: run make smoke TENANT={TENANT} first.")
    pending = cookie_named(header, "iron.sign_in")
    tried = set()
    for _ in range(3):
        now = int(time.time()) // 30
        step = next((s for s in (now, now + 1) if s not in tried), None)
        if step is None:
            time.sleep(30 - time.time() % 30 + 1)
            step = int(time.time()) // 30 + 1
        tried.add(step)
        status, data, header = send("POST", "/auth/second-factor", {"code": totp(secret, step)}, pending)
        if status == 200:
            return cookie_named(header, "iron.session")
        if (data or {}).get("error_code") != "identity.second_factor.invalid":
            raise SystemExit(f"The second factor was refused: {status} {data}")
    raise SystemExit("The second factor was refused three times.")


def main():
    session = sign_in()
    try:
        status, data, _ = send("GET", "/api/v1/me/terms", cookie=session)
        if status != 200:
            raise SystemExit(f"GET /api/v1/me/terms answered {status}: {data}")
        pending = data["pending"]
        if not pending:
            print(f"{ADMIN} has nothing to accept on {HOST}.")
            return
        for document in pending:
            print(f"  {document['source']} {document['key']} version {document['version']}: {document['title']}")
        choices = [{"source": d["source"], "key": d["key"], "version": d["version"]} for d in pending]
        status, data, _ = send("POST", "/api/v1/me/terms/accept", {"documents": choices}, session)
        if status != 204:
            raise SystemExit(f"The acceptance was refused ({status}): {data}")
        print(f"{ADMIN} accepted {len(choices)} document version(s) on {HOST}.")
    finally:
        send("POST", "/auth/logout", cookie=session)


if __name__ == "__main__":
    main()
