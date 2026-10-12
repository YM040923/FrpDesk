#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${FRPDESK_APP_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
DEFAULT_ENV_FILE="$APP_DIR/config/env.example"
ENV_FILE="$APP_DIR/config/env"

if [ -f "$DEFAULT_ENV_FILE" ]; then
  # shellcheck disable=SC1090
  source "$DEFAULT_ENV_FILE"
fi
if [ -f "$ENV_FILE" ]; then
  # shellcheck disable=SC1090
  source "$ENV_FILE"
fi

VAR_DIR="${FRPDESK_VAR_DIR:-${TRIM_PKGVAR:-/var/apps/frpdesk/var}}"
PID_FILE="${FRPDESK_PID_FILE:-$VAR_DIR/run/frpdesk.pid}"
ADDR="${FRPDESK_ADDR:-127.0.0.1:47890}"
SOCKET="${FRPDESK_SOCKET:-$APP_DIR/app.sock}"
HEALTH_ADDR="$ADDR"
if [[ "$HEALTH_ADDR" == 0.0.0.0:* ]]; then
  HEALTH_ADDR="127.0.0.1:${HEALTH_ADDR##*:}"
fi

if command -v systemctl >/dev/null 2>&1 && [ -f /etc/systemd/system/frpdesk.service ] && systemctl is-active --quiet frpdesk.service; then
  if command -v curl >/dev/null 2>&1; then
    curl -fsS --unix-socket "$SOCKET" "http://frpdesk/api/health" || exit 3
    echo
  fi
  echo "FrpDesk is running under systemd"
  exit 0
fi

if [ -f "$PID_FILE" ] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
  echo "FrpDesk process running with PID $(cat "$PID_FILE")"
else
  echo "FrpDesk process not running"
  exit 3
fi

if command -v curl >/dev/null 2>&1; then
  if [ -n "$SOCKET" ]; then
    curl -fsS --unix-socket "$SOCKET" "http://frpdesk/api/health" || exit 3
  else
    curl -fsS "http://$HEALTH_ADDR/api/health" || exit 3
  fi
  echo
fi
