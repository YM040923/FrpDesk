$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runtime = Join-Path $root 'dist\fnos\frpdesk'
if (-not (Test-Path (Join-Path $root 'dist\core\frpdesk-core-linux-amd64'))) { & (Join-Path $PSScriptRoot 'build-core.ps1') }
if (Test-Path $runtime) { Remove-Item $runtime -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $runtime 'bin'),(Join-Path $runtime 'web'),(Join-Path $runtime 'resources\frpc\linux'),(Join-Path $runtime 'scripts'),(Join-Path $runtime 'config'),(Join-Path $runtime 'ui\images') | Out-Null
Copy-Item (Join-Path $root 'dist\core\frpdesk-core-linux-amd64') (Join-Path $runtime 'bin\frpdesk-core')
Copy-Item (Join-Path $root 'web\*') (Join-Path $runtime 'web') -Recurse -Force
Copy-Item (Join-Path $root 'resources\frpc\linux\frpc') (Join-Path $runtime 'resources\frpc\linux\frpc') -Force
Copy-Item (Join-Path $root 'resources\frp-LICENSE') (Join-Path $runtime 'resources\frp-LICENSE') -Force
Copy-Item (Join-Path $root 'packaging\fnos\app\scripts\*.sh') (Join-Path $runtime 'scripts') -Force
Copy-Item (Join-Path $root 'packaging\fnos\app\config\env.example') (Join-Path $runtime 'config\env.example')
Copy-Item (Join-Path $root 'packaging\fnos\app\ui\config') (Join-Path $runtime 'ui\config')
Copy-Item (Join-Path $root 'packaging\fnos\app\ui\images\*') (Join-Path $runtime 'ui\images') -Force
@'
# FrpDesk fnOS runtime

This runtime is built from the native fnOS edition in the FrpDesk repository.
The Linux amd64 frpc binary is bundled under resources/frpc/linux/frpc.
'@ | Set-Content (Join-Path $runtime 'README.md') -Encoding utf8
Write-Host "Prepared $runtime"
