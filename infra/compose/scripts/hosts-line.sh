#!/bin/sh
# Prints the /etc/hosts line that lets command line tools in WSL resolve the laptop hostnames.
# Browsers resolve *.localhost on their own and do not need it.
set -eu
names="traefik.localhost grafana.localhost prometheus.localhost tempo.localhost openbao.localhost control.localhost"
for tenant in "$@"; do
  names="$names $tenant.localhost"
done

cat <<TEXT
Add this line to /etc/hosts in WSL (sudo nano /etc/hosts), or run the command below once:

  echo '127.0.0.1 $names' | sudo tee -a /etc/hosts

TEXT
