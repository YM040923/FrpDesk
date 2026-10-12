#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${FRPDESK_APP_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
ENV_FILE="$APP_DIR/config/env"
DEFAULT_ENV_FILE="$APP_DIR/config/env.example"

if [ -f "$DEFAULT_ENV_FILE" ]; then
  # shellcheck disable=SC1090
  source "$DEFAULT_ENV_FILE"
fi
if [ -f "$ENV_FILE" ]; then
  # shellcheck disable=SC1090
  source "$ENV_FILE"
fi

export FRPDESK_SOCKET="${FRPDESK_SOCKET:-$APP_DIR/app.sock}"
export FRPDESK_GATEWAY_PREFIX="${FRPDESK_GATEWAY_PREFIX:-/app/frpdesk}"
VAR_DIR="${FRPDESK_VAR_DIR:-${TRIM_PKGVAR:-/var/apps/frpdesk/var}}"
export FRPDESK_DATA_DIR="${FRPDESK_DATA_DIR:-$VAR_DIR/data}"
export FRPDESK_WEB_DIR="${FRPDESK_WEB_DIR:-$APP_DIR/web}"
if [ -z "${FRPDESK_FRPC_PATH:-}" ] && [ -x "$APP_DIR/resources/frpc/linux/frpc" ]; then
  export FRPDESK_FRPC_PATH="$APP_DIR/resources/frpc/linux/frpc"
fi

APP_RUNTIME_USER="${FRPDESK_RUNTIME_USER:-${TRIM_RUN_USERNAME:-${TRIM_USERNAME:-frpdesk}}}"

PID_FILE="${FRPDESK_PID_FILE:-$VAR_DIR/run/frpdesk.pid}"
LOG_FILE="${FRPDESK_LOG_FILE:-$VAR_DIR/log/frpdesk-core.log}"

is_frpdesk_pid() {
  local pid="$1"
  [ -n "$pid" ] || return 1
  kill -0 "$pid" 2>/dev/null || return 1
  if command -v ps >/dev/null 2>&1; then
    ps -p "$pid" -o args= 2>/dev/null | grep -q "frpdesk-core" || return 1
  fi
  return 0
}

if [ ! -x "$APP_DIR/bin/frpdesk-core" ]; then
  echo "FrpDesk core not found or not executable: $APP_DIR/bin/frpdesk-core" >&2
  exit 1
fi
if [ ! -d "$FRPDESK_WEB_DIR" ]; then
  echo "FrpDesk Web directory not found: $FRPDESK_WEB_DIR" >&2
  exit 1
fi

if [ "${1:-}" = "--foreground" ]; then
  mkdir -p "$(dirname "$PID_FILE")" "$(dirname "$LOG_FILE")" "$FRPDESK_DATA_DIR"
  touch "${FRPDESK_RUNTIME_MARKER:-/run/frpdesk/supervised}"
  trap 'rm -f "$PID_FILE"' EXIT
  "$APP_DIR/bin/frpdesk-core" >> "$LOG_FILE" 2>&1 &
  pid=$!
  echo "$pid" > "$PID_FILE"
  set +e
  wait "$pid"
  code=$?
  rm -f "$PID_FILE"
  exit "$code"
fi

if [ -f "$PID_FILE" ] && is_frpdesk_pid "$(cat "$PID_FILE")"; then
  echo "FrpDesk is already running with PID $(cat "$PID_FILE")"
  exit 0
fi
rm -f "$PID_FILE"

mkdir -p "$(dirname "$PID_FILE")" "$(dirname "$LOG_FILE")" "$FRPDESK_DATA_DIR"
if [ -n "$APP_RUNTIME_USER" ] && [ "$(id -u)" -eq 0 ] && id "$APP_RUNTIME_USER" >/dev/null 2>&1; then
  nohup runuser -u "$APP_RUNTIME_USER" -- "$APP_DIR/bin/frpdesk-core" >> "$LOG_FILE" 2>&1 &
else
  nohup "$APP_DIR/bin/frpdesk-core" >> "$LOG_FILE" 2>&1 &
fi
echo $! > "$PID_FILE"
echo "FrpDesk started on socket $FRPDESK_SOCKET with PID $(cat "$PID_FILE")"
