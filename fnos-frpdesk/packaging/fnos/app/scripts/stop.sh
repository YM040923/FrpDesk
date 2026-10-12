#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${FRPDESK_APP_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
VAR_DIR="${FRPDESK_VAR_DIR:-${TRIM_PKGVAR:-/var/apps/frpdesk/var}}"
PID_FILE="${FRPDESK_PID_FILE:-$VAR_DIR/run/frpdesk.pid}"
SOCKET="${FRPDESK_SOCKET:-$APP_DIR/app.sock}"

if [ "$(id -u)" -eq 0 ] && command -v systemctl >/dev/null 2>&1 && [ -f /etc/systemd/system/frpdesk.service ] && systemctl is-active --quiet frpdesk.service; then
  rm -f "${FRPDESK_RUNTIME_MARKER:-/run/frpdesk/supervised}"
  systemctl stop frpdesk.service
  echo "FrpDesk stopped"
  exit 0
fi

if [ ! -f "$PID_FILE" ]; then
  echo "FrpDesk is not running: PID file not found"
  exit 0
fi

PID="$(cat "$PID_FILE")"
if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
  kill "$PID"
  for _ in $(seq 1 20); do
    if ! kill -0 "$PID" 2>/dev/null; then
      rm -f "$PID_FILE"
      echo "FrpDesk stopped"
      exit 0
    fi
    sleep 0.5
  done
  kill -9 "$PID" 2>/dev/null || true
fi
rm -f "$PID_FILE"
rm -f "$SOCKET"
echo "FrpDesk stopped"
