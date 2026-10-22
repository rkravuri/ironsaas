"""Publishes a new version of one of the platform's documents (TF-028, ADR-0049) as the operator, with client credentials.

Usage: platform-document.py <terms_of_service|privacy_notice> <title> <file>, with CONTROL_URL and OPERATOR_SECRET in the
environment (OPERATOR_CLIENT_ID defaults to operator). Every person of every tenant accepts it at their next request.
"""
import base64
import json
import os
import sys
import urllib.error
import urllib.request


def call(control, method, path, body=None, token=None, basic=None):
    headers = {"Accept": "application/json"}
    data = None
    if basic is not None:
        data = b"grant_type=client_credentials"
        headers["Content-Type"] = "application/x-www-form-urlencoded"
        headers["Authorization"] = "Basic " + base64.b64encode(basic.encode()).decode()
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


def main():
    key, title, path = sys.argv[1:4]
    control = os.environ.get("CONTROL_URL", "http://localhost:5200")
    client_id = os.environ.get("OPERATOR_CLIENT_ID", "operator")
    secret = os.environ["OPERATOR_SECRET"]
    with open(path, encoding="utf-8") as file:
        body = file.read()
    status, token = call(control, "POST", "/connect/token", basic=f"{client_id}:{secret}")
    if status != 200:
        sys.exit(f"The control plane refused the operator credentials ({status}): {token}")
    status, document = call(control, "POST", "/api/v1/platform-documents", body={"key": key, "title": title, "body": body}, token=token["access_token"])
    if status != 201:
        sys.exit(f"Publishing was refused ({status}): {document.get('detail') if isinstance(document, dict) else document}")
    print(f"Published {document['title']} ({document['key']}) version {document['version']}: every person accepts it at their next request, within a minute.")


if __name__ == "__main__":
    main()
