# OpenBao on the laptop in server mode (ADR-0028): the secrets survive `make down`, a container restart and a WSL
# restart. No secret lives in this file: the seal key comes from OPENBAO_UNSEAL_KEY in infra/compose/.env.

# Integrated storage, one node. OpenBao 2.7 has no "file" backend any more; raft on a single node is its own
# recommendation for a store that persists. The path is the image's own data directory, which belongs to the
# openbao user, so the named volume mounted there is writable without a chown.
storage "raft" {
  path    = "/openbao/file"
  node_id = "laptop"
}

api_addr     = "http://127.0.0.1:8200"
cluster_addr = "http://127.0.0.1:8201"

# Plain HTTP on the platform network, as before; TLS for the store arrives with Azure (Key Vault).
listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

# The static seal unseals the store at every start with a 32-byte key (base64) that never leaves the laptop's .env.
# A wrong key keeps the store sealed and says so in the log; nothing is lost.
seal "static" {
  current_key_id = "laptop-1"
  current_key    = "env://OPENBAO_UNSEAL_KEY"
}

# Integrated storage maps its files into memory, which memory locking does not suit; OpenBao recommends turning it
# off with raft, and the container then needs no IPC_LOCK capability.
disable_mlock = true

ui = true
