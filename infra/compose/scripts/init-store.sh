#!/usr/bin/env bash
# The laptop's OpenBao, initialised once and checked on every later start (ADR-0028). make up, up-slim and up-ci run
# it after the containers are healthy; the pipeline's integration script does too.
#
# First it waits for the store by its health endpoint, up to 90 seconds, saying every 10 seconds where it stands: a
# container Docker calls healthy may still be starting its storage, and a busy runner is slower than a laptop. If the
# store is not ready in time, the container's health state and its last 50 log lines are printed before the failure.
#
# A new store: initialised with one recovery share; the root token and the recovery key go into infra/compose/.env
# (replacing a development placeholder) before anything else; KV version 2 is enabled at secret/.
# An initialised store: it must be unsealed (the static seal does that at start) and the root token in .env must
# open it. Nothing is written then, except KV version 2 at secret/ if an earlier first run stopped before enabling it.
set -euo pipefail

here=$(cd "$(dirname "$0")/.." && pwd)
env_file="$here/.env"
address="${OPENBAO_ADDRESS:-http://localhost:8200}"

# -u: the progress lines reach the terminal and the pipeline's log as they are printed, not when the script ends.
python3 -u - "$address" "$env_file" "$here/compose.yaml" <<'PY'
import json, subprocess, sys, time, urllib.error, urllib.request

address, env_file, compose_file = sys.argv[1:]

WAIT_SECONDS = 90
PROGRESS_SECONDS = 10
# A new store answers its first request, the initialisation, only once raft has elected its one node leader, which
# takes its heartbeat timeout of 5 seconds randomised up to 10, and longer on a busy runner. The release run of
# 2026-10-21 gave up after 10 seconds just as the store initialised, and the root token was lost with the answer.
INIT_SECONDS = 60
NOT_ANSWERING = (urllib.error.URLError, ConnectionError, TimeoutError)
# What the health endpoint's status code says, in the words of the progress lines.
STATES = {200: "initialised, unsealed and active", 429: "unsealed but a standby", 501: "not initialised", 503: "sealed"}
SEALED = (
    "The store stays sealed: OPENBAO_UNSEAL_KEY in infra/compose/.env is not the key the store was initialised with "
    "(the log above says 'failed to unseal'). Put the right key back, or make reset to start over with a new store."
)


def call(method, path, token=None, body=None, timeout=10):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(f"{address}{path}", data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("X-Vault-Token", token)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            text = response.read().decode()
            return response.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as error:
        text = error.read().decode()
        try:
            return error.code, (json.loads(text) if text else None)
        except ValueError:
            return error.code, {"errors": [text]}


def read_env():
    values = {}
    with open(env_file) as handle:
        for line in handle:
            if "=" in line and not line.lstrip().startswith("#"):
                key, _, value = line.rstrip("\n").partition("=")
                values[key] = value
    return values


def write_env(updates):
    """Replaces the lines of these keys, or appends them; every other line stays as it was."""
    with open(env_file) as handle:
        lines = handle.read().splitlines()
    kept = [line for line in lines if line.partition("=")[0] not in updates]
    kept += [f"{key}={value}" for key, value in updates.items()]
    with open(env_file, "w") as handle:
        handle.write("\n".join(kept) + "\n")


def reason(error):
    return str(getattr(error, "reason", None) or error)


def health():
    """The health endpoint's status code, None when the store does not answer, and what it means."""
    try:
        status, _ = call("GET", "/v1/sys/health", timeout=5)
    except NOT_ANSWERING as error:
        return None, f"no answer ({reason(error)})"
    return status, STATES.get(status, f"answers {status}")


def docker(*arguments):
    """Docker's output, or None when it cannot say, with the reason printed: the diagnosis must not hide the failure."""
    try:
        done = subprocess.run(["docker", *arguments], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30)
    except (OSError, subprocess.TimeoutExpired) as error:
        print(f"docker {arguments[0]} did not run: {error}", file=sys.stderr)
        return None
    if done.returncode != 0:
        print(f"docker {arguments[0]} failed: {done.stderr.strip()}", file=sys.stderr)
        return None
    return done.stdout


def diagnose():
    """The store's container as Docker sees it: its state, its health check, and its last 50 log lines."""
    found = docker("compose", "--file", compose_file, "--env-file", env_file, "ps", "--all", "--quiet", "openbao")
    if found is None:
        return
    if not found.split():
        print("Docker has no OpenBao container in this Compose project: make up starts it.", file=sys.stderr)
        return
    container = found.split()[0]
    inspected = docker("inspect", container)
    if inspected is not None:
        details = json.loads(inspected)[0]
        state = details["State"]
        check = state.get("Health") or {}
        line = f"Container {details['Name'].lstrip('/')}: {state['Status']}"
        if check:
            line += f", health {check['Status']} ({check.get('FailingStreak', 0)} failed checks in a row"
            if check.get("Log"):
                last = check["Log"][-1]
                line += f"; the last exited {last['ExitCode']}"
                if (last.get("Output") or "").strip():
                    line += f": {last['Output'].strip()}"
            line += ")"
        else:
            line += ", no health check"
        print(line, file=sys.stderr)
    # docker logs replays the container's error stream on its own; both streams go into one listing, in order.
    try:
        logs = subprocess.run(
            ["docker", "logs", "--tail", "50", container], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=30
        ).stdout
    except (OSError, subprocess.TimeoutExpired) as error:
        print(f"docker logs did not run: {error}", file=sys.stderr)
        return
    print("Its last 50 log lines:", file=sys.stderr)
    for log_line in logs.splitlines():
        print(f"  | {log_line}", file=sys.stderr)


def wait_until(ready):
    """Asks the health endpoint every second until its status code is in `ready`; after WAIT_SECONDS, fails with the diagnosis."""
    started = time.monotonic()
    next_progress = PROGRESS_SECONDS
    while True:
        status, state = health()
        if status in ready:
            return status
        waited = time.monotonic() - started
        if waited >= WAIT_SECONDS:
            print(f"OpenBao at {address} is not ready after {WAIT_SECONDS} seconds: {state}.", file=sys.stderr)
            diagnose()
            sys.exit(SEALED if status == 503 else "make logs SERVICE=openbao shows the store's whole log.")
        if waited >= next_progress:
            print(f"Waiting for OpenBao at {address}: {int(waited)} of {WAIT_SECONDS} seconds, {state}.")
            while next_progress <= waited:
                next_progress += PROGRESS_SECONDS
        time.sleep(1)


# 501 is a store to initialise and 200 one in use. Sealed (503) or a standby (429) is a store still starting: the
# static seal unseals it and raft makes the one node active, so the wait goes on.
if wait_until({200, 501}) == 501:
    try:
        status, result = call("PUT", "/v1/sys/init", body={"recovery_shares": 1, "recovery_threshold": 1}, timeout=INIT_SECONDS)
    except NOT_ANSWERING as error:
        print(f"Initialising the store at {address} did not answer within {INIT_SECONDS} seconds: {reason(error)}.", file=sys.stderr)
        diagnose()
        sys.exit(
            "The store may have initialised without handing over its root token. If the next make up says .env has no "
            "OPENBAO_ROOT_TOKEN, make reset starts over with a new store."
        )
    if status != 200:
        sys.exit(f"Initialising the store failed ({status}): {result}")
    # The token goes into .env first and on its own: if anything after this line fails, the store is still reachable.
    write_env({"OPENBAO_ROOT_TOKEN": result["root_token"]})
    # The HTTP API names the field recovery_keys_base64 (the CLI's JSON says recovery_keys_b64).
    recovery = (result.get("recovery_keys_base64") or result.get("recovery_keys_b64") or [None])[0]
    if recovery:
        write_env({"OPENBAO_RECOVERY_KEY": recovery})
    print("Initialised the store: the root token and the recovery key are in infra/compose/.env.")
    # Initialising seals the store for a moment before the static seal unseals it again.
    wait_until({200})

token = read_env().get("OPENBAO_ROOT_TOKEN", "")
if not token:
    sys.exit(
        "The store is initialised but infra/compose/.env has no OPENBAO_ROOT_TOKEN. With OPENBAO_RECOVERY_KEY, "
        "'bao operator generate-root' makes a new one; otherwise make reset starts over (the volume and the token go together)."
    )
status, _ = call("GET", "/v1/auth/token/lookup-self", token=token)
if status != 200:
    sys.exit(
        f"The root token in infra/compose/.env does not open the store ({status}): it belongs to an earlier store. "
        "make reset starts over (the volume and the token go together)."
    )

status, mounts = call("GET", "/v1/sys/mounts", token=token)
if status != 200:
    sys.exit(f"Reading the store's mounts failed ({status}): {mounts}")
if "secret/" not in (mounts.get("data") or mounts):
    status, result = call("POST", "/v1/sys/mounts/secret", token=token, body={"type": "kv", "options": {"version": "2"}})
    if status not in (200, 204):
        sys.exit(f"Enabling KV version 2 at secret/ failed ({status}): {result}")
    print("Enabled KV version 2 at secret/.")

print(f"The store at {address} is initialised and unsealed, and the root token in infra/compose/.env opens it.")
PY
