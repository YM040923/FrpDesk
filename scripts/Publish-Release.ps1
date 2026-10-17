param(
    [string]$Version = "0.1.1",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot ".." )).Path
$artifactRoot = Join-Path $root "artifacts"
$packageName = "FrpDesk-$Version-win-x64"
$packageRoot = Join-Path $artifactRoot $packageName
$zipPath = Join-Path $artifactRoot "$packageName.zip"
$hashPath = "$zipPath.sha256"

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "版本号必须是类似 0.1.0 或 0.1.0-beta.1 的格式。"
}

if (Test-Path $packageRoot) { Remove-Item $packageRoot -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (Test-Path $hashPath) { Remove-Item $hashPath -Force }
New-Item $packageRoot -ItemType Directory -Force | Out-Null

$publishArgs = @(
    "publish", $root,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "-o", $packageRoot,
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码：$LASTEXITCODE" }

Copy-Item (Join-Path $root "README-WINDOWS.md") (Join-Path $packageRoot "README.md") -Force
Copy-Item (Join-Path $root "RELEASE_NOTES.md") (Join-Path $packageRoot "RELEASE_NOTES.md") -Force
Copy-Item (Join-Path $PSScriptRoot "Install-FrpDesk.ps1") (Join-Path $packageRoot "Install-FrpDesk.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-FrpDesk.ps1") (Join-Path $packageRoot "Uninstall-FrpDesk.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "Install-FrpDesk.cmd") (Join-Path $packageRoot "Install-FrpDesk.cmd") -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-FrpDesk.cmd") (Join-Path $packageRoot "Uninstall-FrpDesk.cmd") -Force

$files = Get-ChildItem $packageRoot -File -Recurse | Where-Object { $_.Name -ne "release-manifest.json" } | Sort-Object FullName
$fileEntries = foreach ($file in $files) {
    [ordered]@{
        path = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        size = $file.Length
        sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifest = [ordered]@{
    product = "FrpDesk"
    version = $Version
    platform = "windows-x64"
    package = $packageName
    generatedAt = [DateTimeOffset]::UtcNow.ToString("o")
    files = @($fileEntries)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $packageRoot "release-manifest.json") -Encoding utf8

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $([IO.Path]::GetFileName($zipPath))" | Set-Content $hashPath -Encoding ascii

$issPath = Join-Path $PSScriptRoot "FrpDesk.iss"
if (Test-Path $issPath) {
    $issText = Get-Content $issPath -Raw
    $issText = $issText -replace '#define AppVersion "[^"]*"', ('#define AppVersion "' + $Version + '"')
    Set-Content $issPath $issText -Encoding utf8 -NoNewline
    $iscc = Get-ChildItem "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "C:\Program Files (x86)\Inno Setup 6\ISCC.exe", "C:\Program Files\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($iscc) {
        & $iscc.FullName $issPath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码：$LASTEXITCODE" }
        $setupPath = Join-Path $artifactRoot "FrpDesk-$Version-win-x64-setup.exe"
        $setupHash = (Get-FileHash $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$setupHash  $([IO.Path]::GetFileName($setupPath))" | Set-Content "$setupPath.sha256" -Encoding ascii
        Write-Host "Installer: $setupPath"
        Write-Host "SHA256: $setupHash"
    } else {
        Write-Host "未找到 Inno Setup，已跳过安装包。"
    }
}

Write-Host "Release package: $zipPath"
Write-Host "SHA256: $zipHash"
