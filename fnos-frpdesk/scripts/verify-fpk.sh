#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["version"])' "$ROOT/package.json")"
FPK="${1:-$ROOT/dist/fnos/FrpDesk-${VERSION}-x86_64.fpk}"
WORK="$ROOT/dist/fnos/verify-fpk"
test -f "$FPK" || { echo "missing fpk: $FPK" >&2; exit 1; }
rm -rf "$WORK"; mkdir -p "$WORK/app"
tar -xzf "$FPK" -C "$WORK"
for f in manifest app.tgz config/privilege config/resource cmd/common cmd/main cmd/boot cmd/install_init cmd/install_callback cmd/uninstall_init cmd/uninstall_callback cmd/upgrade_init cmd/upgrade_callback cmd/config_init cmd/config_callback wizard/install wizard/uninstall ICON.PNG ICON_256.PNG; do test -e "$WORK/$f" || { echo "missing $f" >&2; exit 1; }; done
tar -xzf "$WORK/app.tgz" -C "$WORK/app"
for f in bin/frpdesk-core web/index.html web/app.js web/app.css resources/frpc/linux/frpc scripts/start.sh scripts/stop.sh scripts/status.sh config/env.example ui/config ui/images/icon_64.png ui/images/icon_256.png; do test -e "$WORK/app/$f" || { echo "missing app/$f" >&2; exit 1; }; done
for f in cmd/main cmd/common cmd/boot cmd/install_init cmd/install_callback cmd/uninstall_init cmd/uninstall_callback cmd/upgrade_init cmd/upgrade_callback cmd/config_init cmd/config_callback app/bin/frpdesk-core app/resources/frpc/linux/frpc app/scripts/start.sh app/scripts/stop.sh app/scripts/status.sh; do mode=$(stat -c '%a' "$WORK/$f"); [[ "$mode" == *7 || "$mode" == *5 || "$mode" == *1 ]] || { echo "not executable: $f" >&2; exit 1; }; done
grep -q 'checkport             = false' "$WORK/manifest"
grep -q 'gatewaySocket' "$WORK/app/ui/config"
grep -q 'gatewayPrefix.*\/app\/frpdesk' "$WORK/app/ui/config"
if grep -R -i -q 'frppilot' "$WORK"; then echo 'old FrpPilot residue found' >&2; exit 1; fi
echo "verified current FrpDesk fnOS package: $FPK"
