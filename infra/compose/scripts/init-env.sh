#!/bin/sh
# Creates infra/compose/.env with random credentials for the laptop platform. Runs on every make target that needs
# the file: a new file gets every value, an existing one keeps its values and only gains the keys added since it was
# made. The file is ignored by git (PRD P5: secrets never live in the repository).
# The store's root token and recovery key are not made here: scripts/init-store.sh writes them when it initialises
# the store on the first make up (ADR-0028).
set -eu
here=$(cd "$(dirname "$0")/.." && pwd)
env_file="$here/.env"

random_token() {
  head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "$1"
}

# 32 random bytes in base64: OpenBao's static seal key (ADR-0028).
unseal_key() {
  head -c 32 /dev/urandom | base64 | tr -d '\n'
}

if [ ! -f "$env_file" ]; then
  # Azurite account keys are base64 strings, like real storage account keys.
  blob_key=$(head -c 64 /dev/urandom | base64 | tr -d '\n')
  created=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  cat > "$env_file" <<ENV
# Credentials of the local platform stack. Created by infra/compose/scripts/init-env.sh on $created.
# Ignored by git. To get new values: make reset (deletes the data volumes), delete this file, make env.
POSTGRES_SUPERUSER=foundation
POSTGRES_SUPERUSER_PASSWORD=$(random_token 32)
BLOB_ACCOUNT_NAME=foundation
BLOB_ACCOUNT_KEY=$blob_key
GRAFANA_ADMIN_PASSWORD=$(random_token 16)
# The key that unseals the store at every start. Losing it locks the store: keep it with the volume (make reset drops both).
OPENBAO_UNSEAL_KEY=$(unseal_key)
ENV
  chmod 600 "$env_file"
  echo "Created infra/compose/.env with new local credentials."
  exit 0
fi

# An existing file from before the store kept its data gains the seal key; nothing in it changes.
if ! grep -q '^OPENBAO_UNSEAL_KEY=' "$env_file"; then
  {
    echo "# The key that unseals the store at every start (added $(date -u +%Y-%m-%dT%H:%M:%SZ), ADR-0028). Keep it with the volume."
    echo "OPENBAO_UNSEAL_KEY=$(unseal_key)"
  } >> "$env_file"
  echo "Added OPENBAO_UNSEAL_KEY to infra/compose/.env."
fi
