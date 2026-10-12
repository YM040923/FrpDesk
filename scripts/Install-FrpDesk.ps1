param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\FrpDesk")
)

$ErrorActionPreference = "Stop"
$sourceRoot = (Resolve-Path $PSScriptRoot).Path
$sourceFullPath = [IO.Path]::GetFullPath($sourceRoot)
$installFullPath = [IO.Path]::GetFullPath($InstallRoot)
$sourcePrefix = $sourceFullPath.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
$installPrefix = $installFullPath.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
$samePath = [string]::Equals($sourceFullPath.TrimEnd([char[]]@('\', '/')), $installFullPath.TrimEnd([char[]]@('\', '/')), [StringComparison]::OrdinalIgnoreCase)
$sourceInsideInstall = $sourceFullPath.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)
$installInsideSource = $installFullPath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)
if ($samePath -or $sourceInsideInstall -or $installInsideSource) {
    throw "安装目录不能与解压后的发布包目录相同或互相包含。"
}
$InstallRoot = $installFullPath
$exe = Join-Path $sourceRoot "FrpDesk.exe"
if (-not (Test-Path $exe)) { throw "找不到 FrpDesk.exe，请从完整发布包运行安装脚本。" }

$installedExe = [IO.Path]::GetFullPath((Join-Path $InstallRoot "FrpDesk.exe"))
$running = Get-Process FrpDesk -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path), $installedExe, [StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
}
if ($running) { throw "FrpDesk 正在运行，请先退出托盘中的 FrpDesk，再安装更新。" }

$backgroundServices = @(Get-Service -Name "FrpDesk-Instance-*" -ErrorAction SilentlyContinue)
if ($backgroundServices.Count -gt 0) {
    $names = ($backgroundServices | Select-Object -ExpandProperty Name) -join ", "
    throw "检测到仍有 FRP 后台服务：$names。请先在 FrpDesk 中关闭这些实例的后台服务，再安装更新。"
}

$sourceManifestPath = Join-Path $sourceRoot "release-manifest.json"
if (Test-Path $sourceManifestPath) {
    $manifest = Get-Content $sourceManifestPath -Raw | ConvertFrom-Json
    foreach ($entry in @($manifest.files)) {
        $relativePath = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath)) {
            throw "发布清单包含无效路径：$relativePath"
        }
        $sourceFile = [IO.Path]::GetFullPath((Join-Path $sourceRoot $relativePath.Replace('/', '\')))
        if (-not $sourceFile.StartsWith($sourceFullPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "发布清单路径越界：$relativePath"
        }
        if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
            throw "发布包文件缺失：$relativePath"
        }
        $actual = Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256
        if ($actual.Hash -ne ([string]$entry.sha256).ToUpperInvariant()) {
            throw "发布包校验失败：$relativePath"
        }
    }
}

New-Item $InstallRoot -ItemType Directory -Force | Out-Null
Get-ChildItem $sourceRoot -Force | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $InstallRoot $_.Name) -Recurse -Force
}

$manifestPath = Join-Path $InstallRoot "release-manifest.json"
$version = if (Test-Path $manifestPath) { (Get-Content $manifestPath -Raw | ConvertFrom-Json).version } else { "0.1.0" }
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\FrpDesk.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath = (Join-Path $InstallRoot "FrpDesk.exe")
$shortcut.WorkingDirectory = $InstallRoot
$shortcut.IconLocation = "$(Join-Path $InstallRoot 'assets\FrpDesk.ico'),0"
$shortcut.Description = "FrpDesk FRP 客户端"
$shortcut.Save()

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk"
New-Item $uninstallKey -Force | Out-Null
New-ItemProperty $uninstallKey -Name DisplayName -Value "FrpDesk" -PropertyType String -Force | Out-Null
New-ItemProperty $uninstallKey -Name DisplayVersion -Value $version -PropertyType String -Force | Out-Null
New-ItemProperty $uninstallKey -Name Publisher -Value "FrpDesk" -PropertyType String -Force | Out-Null
New-ItemProperty $uninstallKey -Name InstallLocation -Value $InstallRoot -PropertyType String -Force | Out-Null
New-ItemProperty $uninstallKey -Name DisplayIcon -Value (Join-Path $InstallRoot "assets\FrpDesk.ico") -PropertyType String -Force | Out-Null
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallRoot 'Uninstall-FrpDesk.ps1')`""
New-ItemProperty $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null

Write-Host "FrpDesk $version 已安装到 $InstallRoot"
Write-Host "开始菜单快捷方式已创建。用户配置会保留在 %LOCALAPPDATA%\FrpDesk。"
