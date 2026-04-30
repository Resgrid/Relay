#!/bin/bash
# Entrypoint for the Resgrid Relay container.
#
# Configuration is driven entirely by environment variables (prefixed RESGRID__RELAY__).
# See the Dockerfile for the complete list of available settings and defaults.
# The bundled appsettings.json acts as a reference template only — it contains
# no operational values. Everything must come from docker run -e or docker-compose.
#
# Optional: LocalXpose TCP tunnel
#   LOCLX_ENABLED=true               enable the tunnel (default: false)
#   LOCLX_TOKEN=<token>              LocalXpose access token (required)
#   LOCLX_RESERVED_ENDPOINT=<h:p>   optional reserved endpoint, e.g. smtp.loclx.io:25
#
#   Alternatively, mount a tunnels YAML file at /etc/resgrid/loclx-tunnels.yaml
#   and set LOCLX_ENABLED=true.
set -e

# ─── Validation ───────────────────────────────────────────────────────────
echo "[relay] Resgrid Relay starting..."
echo "[relay] Mode: ${RESGRID__RELAY__Mode:-smtp}"
echo "[relay] API:   ${RESGRID__RELAY__Resgrid__BaseUrl:-(NOT SET — required)}"
echo "[relay] Port:  ${RESGRID__RELAY__Smtp__Port:-2525}"

if [ -z "${RESGRID__RELAY__Resgrid__BaseUrl}" ]; then
  echo "[relay] ERROR: RESGRID__RELAY__Resgrid__BaseUrl is required."
  exit 1
fi

if [ -z "${RESGRID__RELAY__Resgrid__ClientId}" ]; then
  echo "[relay] ERROR: RESGRID__RELAY__Resgrid__ClientId is required."
  exit 1
fi

if [ -z "${RESGRID__RELAY__Resgrid__ClientSecret}" ]; then
  echo "[relay] ERROR: RESGRID__RELAY__Resgrid__ClientSecret is required."
  exit 1
fi

grant_type="${RESGRID__RELAY__Resgrid__GrantType:-RefreshToken}"
case "${grant_type}" in
  RefreshToken)
    if [ -z "${RESGRID__RELAY__Resgrid__RefreshToken}" ]; then
      echo "[relay] ERROR: RESGRID__RELAY__Resgrid__RefreshToken is required when GrantType=RefreshToken."
      exit 1
    fi
    ;;
  SystemApiKey)
    if [ -z "${RESGRID__RELAY__Resgrid__SystemApiKey}" ]; then
      echo "[relay] ERROR: RESGRID__RELAY__Resgrid__SystemApiKey is required when GrantType=SystemApiKey."
      exit 1
    fi
    ;;
esac

# ─── LocalXpose tunnel ────────────────────────────────────────────────────
if [ "${LOCLX_ENABLED:-false}" = "true" ]; then
  SMTP_PORT="${RESGRID__RELAY__Smtp__Port:-2525}"

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
