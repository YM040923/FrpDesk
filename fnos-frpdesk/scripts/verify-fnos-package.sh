#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if command -v python >/dev/null 2>&1; then
  PYTHON_BIN="python"
elif command -v python3 >/dev/null 2>&1; then
  PYTHON_BIN="python3"
else
  echo "Python 3 is required to read package.json" >&2
  exit 1
fi
native_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s\n' "$1"
  fi
}

VERSION="$("$PYTHON_BIN" -c "import json, sys; print(json.load(open(sys.argv[1], encoding='utf-8'))['version'])" "$ROOT/package.json")"
FPK="${1:-$ROOT/dist/fnos/FrpDesk-${VERSION}-x86_64.fpk}"
WORK="$ROOT/dist/fnos/verify-fpk"

if [ ! -f "$FPK" ]; then
  echo "Missing fpk: $FPK" >&2
  exit 1
fi

rm -rf "$WORK"
mkdir -p "$WORK"
tar -xzf "$FPK" -C "$WORK"

required=(
  "manifest"
  "app.tgz"
  "config/privilege"
  "config/resource"
  "cmd/common"
  "cmd/main"
  "cmd/boot"
  "cmd/install_init"
  "cmd/install_callback"
  "cmd/uninstall_init"
  "cmd/uninstall_callback"
  "cmd/upgrade_init"
  "cmd/upgrade_callback"
  "cmd/config_init"
  "cmd/config_callback"
  "wizard/install"
  "wizard/uninstall"
  "ICON.PNG"
  "ICON_256.PNG"
)
for item in "${required[@]}"; do
  if [ ! -e "$WORK/$item" ]; then
    echo "fpk missing $item" >&2
    exit 1
  fi
done

mkdir -p "$WORK/app-extract/app"
tar -xzf "$WORK/app.tgz" -C "$WORK/app-extract/app"
app_required=(
  "app/bin/frpdesk-core"
  "app/web/index.html"
  "app/resources/frpc/linux/frpc"
  "app/scripts/start.sh"
  "app/scripts/stop.sh"
  "app/scripts/status.sh"
  "app/config/env.example"
  "app/ui/config"
  "app/ui/images/icon.png"
  "app/ui/images/icon_64.png"
  "app/ui/images/icon_256.png"
  "app/README.md"
)
for item in "${app_required[@]}"; do
  if [ ! -e "$WORK/app-extract/$item" ]; then
    echo "app.tgz missing $item" >&2
    exit 1
  fi
done

"$PYTHON_BIN" - "$FPK" <<'PY'
import io
import sys
import tarfile
from pathlib import Path

fpk = Path(sys.argv[1])
with tarfile.open(fpk, 'r:gz') as outer:
    app = outer.extractfile('app.tgz').read()
    cmd_main = outer.getmember('cmd/main')
    if (cmd_main.mode & 0o111) == 0:
        raise SystemExit('cmd/main is not executable in fpk')
with tarfile.open(fileobj=io.BytesIO(app), mode='r:gz') as inner:
    checks = {
        'bin/frpdesk-core': 'frpdesk-core is not executable',
        'resources/frpc/linux/frpc': 'linux frpc is not executable',
        'scripts/start.sh': 'runtime script is not executable: start.sh',
        'scripts/stop.sh': 'runtime script is not executable: stop.sh',
        'scripts/status.sh': 'runtime script is not executable: status.sh',
    }
    for name, message in checks.items():
        mode = inner.getmember(name).mode
        if (mode & 0o111) == 0:
            raise SystemExit(message)
PY

for script in start.sh stop.sh status.sh; do
  if grep -q $'\r' "$WORK/app-extract/app/scripts/$script"; then
    echo "runtime script has CRLF line endings: $script" >&2
    exit 1
  fi
done
if ! grep -q "checkport             = false" "$WORK/manifest"; then
  echo "manifest should disable checkport for gateway socket native UI" >&2
  exit 1
fi
if ! grep -q "arch                  = x86_64" "$WORK/manifest"; then
  echo "manifest must target x86_64 fnOS devices" >&2
  exit 1
fi
if ! grep -q "platform              = x86" "$WORK/manifest"; then
  echo "manifest platform must be x86" >&2
  exit 1
fi
if ! grep -q "desktop_uidir         = ui" "$WORK/manifest"; then
  echo "manifest missing expected desktop_uidir" >&2
  exit 1
fi
if grep -q "__VERSION__" "$WORK/manifest"; then
  echo "manifest version placeholder was not replaced" >&2
  exit 1
fi
if grep -q "install_type" "$WORK/manifest"; then
  echo "manifest should not request nonstandard install_type" >&2
  exit 1
fi
for script in main boot install_init install_callback uninstall_init uninstall_callback upgrade_init upgrade_callback config_init config_callback; do
  if [ ! -x "$WORK/cmd/$script" ]; then
    echo "cmd script is not executable: $script" >&2
    exit 1
  fi
  if grep -q $'\r' "$WORK/cmd/$script"; then
    echo "cmd script has CRLF line endings: $script" >&2
    exit 1
  fi
done
if ! grep -q '"frpdesk.Application"' "$WORK/app-extract/app/ui/config"; then
  echo "app ui config missing frpdesk.Application entry" >&2
  exit 1
fi
if ! grep -q '"gatewaySocket"[[:space:]]*:[[:space:]]*"app.sock"' "$WORK/app-extract/app/ui/config"; then
  echo "app ui config missing gatewaySocket app.sock" >&2
  exit 1
fi
if ! grep -q '"gatewayPrefix"[[:space:]]*:[[:space:]]*"/app/frpdesk"' "$WORK/app-extract/app/ui/config"; then
  echo "app ui config missing gatewayPrefix /app/frpdesk" >&2
  exit 1
fi
if ! grep -q '"run-as"[[:space:]]*:[[:space:]]*"package"' "$WORK/config/privilege"; then
  echo "config/privilege should use the standard package user" >&2
  exit 1
fi
if ! grep -q 'runuser -u' "$WORK/app-extract/app/scripts/start.sh"; then
  echo "start.sh should drop privileges when running as root" >&2
  exit 1
fi
if ! grep -q "install-autostart" "$WORK/cmd/main"; then
  echo "cmd/main missing install-autostart command" >&2
  exit 1
fi
if ! grep -q "remove-autostart" "$WORK/cmd/main"; then
  echo "cmd/main missing remove-autostart command" >&2
  exit 1
fi
if ! grep -q "systemctl enable" "$WORK/cmd/common"; then
  echo "cmd/common missing systemd enable for boot autostart" >&2
  exit 1
fi
if ! grep -q "After=network-online.target" "$WORK/cmd/common"; then
  echo "cmd/common missing network-online boot ordering" >&2
  exit 1
fi
if ! grep -q "Type=simple" "$WORK/cmd/common" || ! grep -q "Restart=on-failure" "$WORK/cmd/common"; then
  echo "cmd/common should use a supervised systemd service for Core recovery" >&2
  exit 1
fi
if ! grep -q "frpdesk.service" "$WORK/cmd/common"; then
  echo "cmd/common missing frpdesk.service registration" >&2
  exit 1
fi
if ! grep -q "install-autostart" "$WORK/cmd/install_callback" || ! grep -q "install-autostart" "$WORK/cmd/upgrade_callback"; then
  echo "install and upgrade callbacks should register boot autostart" >&2
  exit 1
fi
if ! grep -q 'ExecCondition=$BOOT_PATH' "$WORK/cmd/common" || ! grep -q 'ExecStart=$APP_DIR/scripts/start.sh --foreground' "$WORK/cmd/common"; then
  echo "systemd service should gate boot autostart and supervise the foreground Core" >&2
  exit 1
fi
if ! grep -q 'ExecStop=/usr/bin/rm -f $RUNTIME_MARKER' "$WORK/cmd/common"; then
  echo "systemd service should clear the manual-run marker on stop" >&2
  exit 1
fi
if ! grep -q "remove-autostart" "$WORK/cmd/uninstall_init"; then
  echo "uninstall_init should remove boot autostart" >&2
  exit 1
fi
json_files=(
  "$WORK/config/privilege"
  "$WORK/config/resource"
  "$WORK/wizard/install"
  "$WORK/wizard/uninstall"
  "$WORK/app-extract/app/ui/config"
)
json_args=()
for path in "${json_files[@]}"; do
  json_args+=("$(native_path "$path")")
done
"$PYTHON_BIN" - "${json_args[@]}" <<'PY'
import json
import sys

for path in sys.argv[1:]:
    data = open(path, "rb").read()
    if data.startswith(b"\xef\xbb\xbf"):
        raise SystemExit(f"{path} must be UTF-8 without BOM")
    try:
        json.loads(data.decode("utf-8"))
    except Exception as exc:
        raise SystemExit(f"{path} is not valid JSON: {exc}")
PY

missing_dest="$WORK/missing-install-dest"
rm -rf "$missing_dest"
if ! TRIM_APPDEST="$missing_dest/target" TRIM_PKGVAR="$missing_dest/var" "$WORK/cmd/install_init" >/dev/null; then
  echo "cmd/install_init failed when TRIM_APPDEST did not exist" >&2
  exit 1
fi
if [ -e "$missing_dest" ]; then
  echo "cmd/install_init must not create package directories before fnOS prepares them" >&2
  exit 1
fi

installed_app="$WORK/installed-app"
rm -rf "$installed_app"
mkdir -p "$installed_app"
tar -xzf "$WORK/app.tgz" -C "$installed_app"
set +e
TRIM_APPDEST="$installed_app" TRIM_PKGVAR="$missing_dest/var" "$WORK/cmd/main" status >/dev/null
status_code="$?"
set -e
if [ "$status_code" -ne 3 ]; then
  echo "cmd/main status should return 3 for a stopped service with fnpack TRIM_APPDEST layout, got $status_code" >&2
  exit 1
fi

echo "fnOS fpk verified: $FPK"
