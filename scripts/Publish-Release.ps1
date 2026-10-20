param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$SignThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$artifactRoot = Join-Path $root "artifacts"
$projectPath = Join-Path $root "FrpDesk.WinUI\FrpDesk.WinUI.csproj"
$frpcPath = Join-Path $root "assets\frpc.exe"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectText = Get-Content -LiteralPath $projectPath -Raw
    $versionMatch = [regex]::Match($projectText, '<VersionPrefix>([^<]+)</VersionPrefix>')
    if (-not $versionMatch.Success) { throw "FrpDesk.WinUI.csproj 中找不到 VersionPrefix。" }
    $Version = $versionMatch.Groups[1].Value.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "版本号必须是类似 0.1.0 或 0.1.0-beta.1 的格式。"
}
if (-not (Test-Path -LiteralPath $frpcPath -PathType Leaf)) {
    throw "缺少 assets\frpc.exe，请先运行 scripts\Fetch-Frpc.ps1。"
}

$packageName = "FrpDesk-$Version-win-x64"
$packageRoot = Join-Path $artifactRoot $packageName
$zipPath = Join-Path $artifactRoot "$packageName.zip"
$hashPath = "$zipPath.sha256"
$setupPath = Join-Path $artifactRoot "$packageName-setup.exe"

foreach ($path in @($packageRoot, $zipPath, $hashPath, $setupPath, "$setupPath.sha256")) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

function Find-SignTool {
    if ($env:SIGNTOOL_PATH -and (Test-Path -LiteralPath $env:SIGNTOOL_PATH -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $env:SIGNTOOL_PATH).Path
    }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kits) {
        return Get-ChildItem -LiteralPath $kits -Filter signtool.exe -File -Recurse |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    return $null
}

function Sign-File([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($SignThumbprint)) {
        Write-Warning "未提供 -SignThumbprint，产物未签名；SmartScreen 仍可能拦截下载。"
        return
    }
    $signTool = Find-SignTool
    if (-not $signTool) { throw "找不到 signtool.exe，请设置 SIGNTOOL_PATH。" }
    & $signTool sign /sha1 $SignThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$Path" }
    & $signTool verify /pa $Path
    if ($LASTEXITCODE -ne 0) { throw "签名验证失败：$Path" }
}

$publishArgs = @(
    "publish", $projectPath,
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

Sign-File (Join-Path $packageRoot "FrpDesk.exe")

Copy-Item (Join-Path $root "README-WINDOWS.md") (Join-Path $packageRoot "README.md") -Force
Copy-Item (Join-Path $root "RELEASE_NOTES.md") (Join-Path $packageRoot "RELEASE_NOTES.md") -Force
Copy-Item (Join-Path $PSScriptRoot "Verify-Release.ps1") (Join-Path $packageRoot "Verify-Release.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-Services.ps1") (Join-Path $packageRoot "Uninstall-Services.ps1") -Force

$files = Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
    Where-Object { $_.Name -ne "release-manifest.json" } |
    Sort-Object FullName
$fileEntries = foreach ($file in $files) {
    [ordered]@{
        path = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
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
$manifestPath = Join-Path $packageRoot "release-manifest.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot "Verify-Release.ps1") -PackageRoot $packageRoot -ManifestPath $manifestPath | Out-Host

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $hashPath -Encoding ascii

$issPath = Join-Path $PSScriptRoot "FrpDesk.iss"
$iscc = Get-ChildItem "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $iscc) { throw "未找到 Inno Setup 6 的 ISCC.exe，无法生成安装包。" }

& $iscc.FullName "/DAppVersion=$Version" $issPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码：$LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "安装包未生成：$setupPath" }

Sign-File $setupPath
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$setupHash  $([IO.Path]::GetFileName($setupPath))" | Set-Content -LiteralPath "$setupPath.sha256" -Encoding ascii

Write-Host "Portable ZIP: $zipPath"
Write-Host "ZIP SHA256:  $zipHash"
Write-Host "Installer:   $setupPath"
Write-Host "Setup SHA256: $setupHash"
