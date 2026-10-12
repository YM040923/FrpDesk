#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["version"])' "$ROOT/package.json")"
RUNTIME="$ROOT/dist/fnos/frpdesk"
WORK="$ROOT/dist/fnos/fpk-work"
OUT="$ROOT/dist/fnos/FrpDesk-${VERSION}-x86_64.fpk"
FNPACK="${FNPACK:-$ROOT/tools-fnpack}"
STAGE="$(mktemp -d /tmp/frpdesk-fpk-work.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT
test -x "$FNPACK" || { echo "missing fnpack: $FNPACK" >&2; exit 1; }
test -x "$RUNTIME/bin/frpdesk-core" || { echo "build runtime first" >&2; exit 1; }
test -x "$RUNTIME/resources/frpc/linux/frpc" || { echo "missing Linux frpc" >&2; exit 1; }
rm -rf "$WORK"; mkdir -p "$WORK/app"
cp -R "$RUNTIME"/. "$WORK/app/"
sed "s/__VERSION__/$VERSION/g" "$ROOT/packaging/fnos/manifest" > "$WORK/manifest"
cp -R "$ROOT/packaging/fnos/cmd" "$WORK/cmd"
cp -R "$ROOT/packaging/fnos/config" "$WORK/config"
cp -R "$ROOT/packaging/fnos/wizard" "$WORK/wizard"
cp "$ROOT/packaging/fnos/app/scripts/"*.sh "$WORK/app/scripts/"
cp "$ROOT/packaging/fnos/app/ui/config" "$WORK/app/ui/config"
cp "$ROOT/packaging/fnos/app/config/env.example" "$WORK/app/config/env.example"
cp "$ROOT/packaging/fnos/app/ui/images/"* "$WORK/app/ui/images/"
cp "$ROOT/packaging/fnos/app/ui/images/icon_64.png" "$WORK/ICON.PNG"
cp "$ROOT/packaging/fnos/app/ui/images/icon_256.png" "$WORK/ICON_256.PNG"
find "$WORK" -type f -name '*.sh' -exec sed -i 's/\r$//' {} \;
find "$WORK" -type f \( -name '*.sh' -o -path '*/bin/*' -o -path '*/resources/frpc/linux/frpc' \) -exec chmod +x {} \;
find "$WORK/cmd" -type f -exec chmod +x {} \;
cp -a "$WORK"/. "$STAGE"/
find "$STAGE" -type d -exec chmod 755 {} +
find "$STAGE" -type f -exec chmod 644 {} +
find "$STAGE/cmd" -type f -exec chmod 755 {} +
chmod 755 "$STAGE/app/bin/frpdesk-core" "$STAGE/app/resources/frpc/linux/frpc" "$STAGE/app/scripts/"*.sh
(cd "$STAGE" && "$FNPACK" build)
test -f "$STAGE/frpdesk.fpk" || { echo "fnpack did not produce $STAGE/frpdesk.fpk" >&2; exit 1; }
rm -f "$OUT"
cp "$STAGE/frpdesk.fpk" "$OUT"
echo "Built $OUT"
