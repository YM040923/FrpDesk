#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA=/tmp/frpdesk-smoke
PORT=47990
rm -rf "$DATA"; mkdir -p "$DATA"
FRPDESK_ADDR="127.0.0.1:$PORT" \
FRPDESK_DATA_DIR="$DATA" \
FRPDESK_WEB_DIR="$ROOT/dist/fnos/frpdesk/web" \
FRPDESK_FRPC_PATH="$ROOT/dist/fnos/frpdesk/resources/frpc/linux/frpc" \
"$ROOT/dist/fnos/frpdesk/bin/frpdesk-core" >"$DATA/core.log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -fsS "http://127.0.0.1:$PORT/api/health" >"$DATA/health.json" && break || sleep .2; done
test -s "$DATA/health.json"
grep -q 'FrpDesk fnOS Core' "$DATA/health.json"
body='{"filename":"rdp.toml","content":"serverAddr = \"relay.example.com\"\nserverPort = 7000\n\n[[proxies]]\nname = \"rdp\"\ntype = \"tcp\"\nlocalIP = \"127.0.0.1\"\nlocalPort = 3389\nremotePort = 33389\n"}'
curl -fsS -X POST -H 'Content-Type: application/json' --data "$body" "http://127.0.0.1:$PORT/api/configs" >"$DATA/import.json"
id="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"])' "$DATA/import.json")"
curl -fsS "http://127.0.0.1:$PORT/api/configs/$id/endpoints" >"$DATA/endpoints.json"
grep -q '33389' "$DATA/endpoints.json"
curl -fsS "http://127.0.0.1:$PORT/" | grep -q 'FrpDesk'
echo "fnOS core smoke passed: $DATA"
