#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${FRPDESK_APP_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
VAR_DIR="${FRPDESK_VAR_DIR:-${TRIM_PKGVAR:-/var/apps/frpdesk/var}}"
PID_FILE="${FRPDESK_PID_FILE:-$VAR_DIR/run/frpdesk.pid}"
SOCKET="${FRPDESK_SOCKET:-$APP_DIR/app.sock}"

is_frpdesk_pid() {
  local pid="$1"
  [ -n "$pid" ] || return 1
  kill -0 "$pid" 2>/dev/null || return 1
  if command -v ps >/dev/null 2>&1; then
    ps -p "$pid" -o args= 2>/dev/null | grep -q "frpdesk-core" || return 1
  fi
  return 0
}

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
if is_frpdesk_pid "$PID"; then
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
