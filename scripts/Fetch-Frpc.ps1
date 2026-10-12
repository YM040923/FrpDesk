$ErrorActionPreference = 'Stop'

$version = '0.71.0'
$archiveName = "frp_${version}_windows_amd64.zip"
$url = "https://github.com/fatedier/frp/releases/download/v${version}/${archiveName}"
$expectedSha256 = '9e5062e3e5cf07e67144a3a4acf175ef6a2486f3605dd6cf288bae34ab39819f'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "FrpDesk-frpc-$([guid]::NewGuid().ToString('N'))"
$archivePath = Join-Path $tempRoot $archiveName
$extractPath = Join-Path $tempRoot 'extract'
$assetsPath = Join-Path $PSScriptRoot '..\assets'

New-Item -ItemType Directory -Path $tempRoot, $assetsPath -Force | Out-Null
try {
    Invoke-WebRequest -Uri $url -OutFile $archivePath
    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "FRP archive SHA256 mismatch: $actualSha256"
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
    $frpc = Get-ChildItem -LiteralPath $extractPath -Filter 'frpc.exe' -File -Recurse | Select-Object -First 1
    $license = Get-ChildItem -LiteralPath $extractPath -Filter 'LICENSE' -File -Recurse | Select-Object -First 1
    if (-not $frpc -or -not $license) {
        throw 'The verified FRP archive did not contain frpc.exe and LICENSE.'
    }

    Copy-Item -LiteralPath $frpc.FullName -Destination (Join-Path $assetsPath 'frpc.exe') -Force
    Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $assetsPath 'frp-LICENSE') -Force
    Write-Output "Bundled frpc v$version ($actualSha256 verified)."
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
