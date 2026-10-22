#!/usr/bin/env sh
# The run's one PostgreSQL, Azurite and OpenBao (make test, CI). Every test project works in its own database, blob
# containers and store mount on them (tests/Iron.Testing), so a run starts three containers instead of one or more per
# project, which on the laptop beside the stack starved threads past Testcontainers' timeouts. The single sign-on test
# provider is the fourth (ADR-0048): every test names an issuer of its own on it.
#   up    pulls what is missing, starts the four on loopback ports, waits until each answers, and prints the
#         variables the fixtures read, for eval
#   down  removes them (also leftovers of an interrupted run)
# The images are the ones tests/Iron.Testing names, so a project run alone starts the same.
set -eu
cd "$(dirname "$0")/.."

postgres=iron-test-postgres
azurite=iron-test-azurite
openbao=iron-test-openbao
oidc=iron-test-oidc
password=iron-test
token=iron-test-root-token

image() {
  sed -nE 's/.*const string Image = "([^"]+)".*/\1/p' "tests/Iron.Testing/$1.cs"
}

port() {
  docker port "$1" "$2/tcp" | head -1 | sed -E 's/.*:([0-9]+)$/\1/'
}

wait_for() {
  name=$1
  shift
  attempt=0
  until "$@" > /dev/null 2>&1; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 120 ]; then
      echo "The test service $name did not answer within a minute:" >&2
      docker logs --tail 30 "$name" >&2 || true
      exit 1
    fi
    sleep 0.5
  done
}

down() {
  docker rm -f "$postgres" "$azurite" "$openbao" "$oidc" > /dev/null 2>&1 || true
}

up() {
  down
  for name in TestPostgres TestBlob TestOpenBao TestOidcProvider; do
    ref=$(image "$name")
    docker image inspect "$ref" > /dev/null 2>&1 || docker pull --quiet "$ref" > /dev/null
  done

  # A throw-away server: no durability, and enough connections for four projects at once, each with its own pools.
  docker run -d --name "$postgres" -e POSTGRES_PASSWORD="$password" -p 127.0.0.1::5432 "$(image TestPostgres)" \
    -c fsync=off -c synchronous_commit=off -c full_page_writes=off -c max_connections=500 > /dev/null
  docker run -d --name "$azurite" -p 127.0.0.1::10000 "$(image TestBlob)" \
    azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck --loose > /dev/null
  docker run -d --name "$openbao" --cap-add IPC_LOCK -e BAO_ADDR=http://127.0.0.1:8200 -p 127.0.0.1::8200 "$(image TestOpenBao)" \
    server -dev "-dev-root-token-id=$token" -dev-listen-address=0.0.0.0:8200 > /dev/null
  docker run -d --name "$oidc" -e 'JSON_CONFIG={"interactiveLogin": true}' -e LOG_LEVEL=WARN -p 127.0.0.1::8080 "$(image TestOidcProvider)" > /dev/null

  # Over TCP inside the container: the image's first start answers on its socket before the real server listens.
  wait_for "$postgres" docker exec -e PGPASSWORD="$password" "$postgres" psql -h 127.0.0.1 -U postgres -c "select 1"
  blob_port=$(port "$azurite" 10000)
  wait_for "$azurite" curl -s -o /dev/null "http://127.0.0.1:$blob_port/"
  bao_port=$(port "$openbao" 8200)
  wait_for "$openbao" curl -sf "http://127.0.0.1:$bao_port/v1/sys/health"
  oidc_port=$(port "$oidc" 8080)
  wait_for "$oidc" curl -sf "http://127.0.0.1:$oidc_port/isalive"

  # The blob endpoint only: the fixtures add Azurite's well-known development account and key, which Testcontainers carries.
  echo "export IRON_TEST_POSTGRES='Host=127.0.0.1;Port=$(port "$postgres" 5432);Username=postgres;Password=$password;Database=postgres'"
  echo "export IRON_TEST_BLOB='http://127.0.0.1:$blob_port/devstoreaccount1'"
  echo "export IRON_TEST_OPENBAO='http://127.0.0.1:$bao_port'"
  echo "export IRON_TEST_OPENBAO_TOKEN='$token'"
  echo "export IRON_TEST_OIDC='http://127.0.0.1:$oidc_port'"
}

case "${1:-}" in
  up) up ;;
  down) down ;;
  *) echo "usage: $0 up|down" >&2; exit 2 ;;
esac
