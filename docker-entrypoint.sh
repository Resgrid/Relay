#!/bin/bash
# Entrypoint for the Resgrid Relay container.
#
# Optionally starts a LocalXpose TCP tunnel before launching the relay,
# allowing the SMTP port to be reached from outside a firewall.
#
# Configuration — choose one approach:
#
# APPROACH 1: environment variables
#   LOCLX_ENABLED=true               enable the tunnel (default: false)
#   LOCLX_TOKEN=<token>              LocalXpose access token (required)
#   LOCLX_RESERVED_ENDPOINT=<h:p>   optional reserved endpoint, e.g. smtp.loclx.io:25
#   RELAY_Smtp__Port=2525            SMTP port the relay listens on (default: 2525)
#
# APPROACH 2: config file (takes precedence over env vars when present)
#   Mount a tunnels YAML file into the container:
#     -v /host/loclx-tunnels.yaml:/etc/resgrid/loclx-tunnels.yaml
#   and set LOCLX_ENABLED=true and LOCLX_TOKEN=<token>.
#
#   Example loclx-tunnels.yaml:
#     tunnels:
#       - name: smtp
#         type: tcp
#         to: localhost:2525
#         reserved: smtp.loclx.io:25
#
set -e

if [ "${LOCLX_ENABLED:-false}" = "true" ]; then
  SMTP_PORT="${RELAY_Smtp__Port:-2525}"

  if [ -n "${LOCLX_TOKEN}" ]; then
    echo "[localxpose] Authenticating..."
    export LX_ACCESS_TOKEN="${LOCLX_TOKEN}"
    loclx auth login
  else
    echo "[localxpose] WARNING: LOCLX_TOKEN is not set; tunnel may fail to authenticate."
  fi

  if [ -f "/etc/resgrid/loclx-tunnels.yaml" ]; then
    echo "[localxpose] Starting tunnel from /etc/resgrid/loclx-tunnels.yaml..."
    loclx tunnel -c /etc/resgrid/loclx-tunnels.yaml &
  elif [ -n "${LOCLX_RESERVED_ENDPOINT}" ]; then
    echo "[localxpose] Starting reserved TCP tunnel to localhost:${SMTP_PORT} via ${LOCLX_RESERVED_ENDPOINT}..."
    loclx tunnel tcp --to "localhost:${SMTP_PORT}" --reserved-endpoint "${LOCLX_RESERVED_ENDPOINT}" &
  else
    echo "[localxpose] Starting ephemeral TCP tunnel to localhost:${SMTP_PORT}..."
    loclx tunnel tcp --to "localhost:${SMTP_PORT}" &
  fi

  echo "[localxpose] Tunnel started (PID: $!)."
fi

exec dotnet /app/Resgrid.Audio.Relay.Console.dll
