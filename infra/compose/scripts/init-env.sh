#!/bin/sh
# Creates infra/compose/.env with random credentials for the laptop platform. Runs once and keeps an existing file.
# The file is ignored by git (PRD P5: secrets never live in the repository).
set -eu
here=$(cd "$(dirname "$0")/.." && pwd)
env_file="$here/.env"

if [ -f "$env_file" ]; then
  exit 0
fi

random_token() {
  head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "$1"
}

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
OPENBAO_ROOT_TOKEN=$(random_token 32)
GRAFANA_ADMIN_PASSWORD=$(random_token 16)
ENV
chmod 600 "$env_file"
echo "Created infra/compose/.env with new local credentials."
