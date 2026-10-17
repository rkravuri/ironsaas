#!/bin/sh
# Tempo and Loki ship as distroless images without a shell, so Compose cannot health-check them.
# This script waits until their readiness endpoints answer. Used by `make up` and `make up-ci`.
set -eu

wait_for() {
  name=$1
  url=$2
  tries=0
  until curl -fsS -o /dev/null "$url" 2>/dev/null; do
    tries=$((tries + 1))
    if [ "$tries" -ge 45 ]; then
      echo "$name did not become ready at $url within 90 seconds. Check: make logs SERVICE=$name"
      return 1
    fi
    sleep 2
  done
  echo "$name is ready"
}

for name in "$@"; do
  case "$name" in
    tempo) wait_for tempo http://localhost:3200/ready ;;
    loki) wait_for loki http://localhost:3100/ready ;;
    *) echo "wait-ready.sh: unknown service '$name'"; exit 2 ;;
  esac
done
