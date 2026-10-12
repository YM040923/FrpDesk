$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $root 'dist\core'
New-Item -ItemType Directory -Force $out | Out-Null
$env:CGO_ENABLED = '0'; $env:GOOS = 'linux'; $env:GOARCH = 'amd64'
Push-Location (Join-Path $root 'core')
try { go build -trimpath -ldflags='-s -w' -o (Join-Path $out 'frpdesk-core-linux-amd64') './cmd/frpdesk-core' }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'Linux core build failed.' }
Write-Host "Built $out\frpdesk-core-linux-amd64"
