param(
    [string]$InstallRoot = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
$scriptRoot = (Resolve-Path $PSScriptRoot).Path
$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
if (-not [string]::Equals(
    $resolvedInstallRoot.TrimEnd([char[]]@('\', '/')),
    $scriptRoot.TrimEnd([char[]]@('\', '/')),
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "为避免误删，此卸载程序只能删除它所在的 FrpDesk 安装目录。"
}

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk"
if (-not (Test-Path $uninstallKey)) { throw "找不到 FrpDesk 安装记录，未执行删除。" }
$registeredRoot = (Get-ItemProperty $uninstallKey -Name InstallLocation).InstallLocation
if (-not [string]::Equals([IO.Path]::GetFullPath($registeredRoot).TrimEnd([char[]]@('\', '/')),
    $resolvedInstallRoot.TrimEnd([char[]]@('\', '/')), [StringComparison]::OrdinalIgnoreCase)) {
    throw "卸载目录与 Windows 安装记录不一致，未执行删除。"
}

$installedExe = [IO.Path]::GetFullPath((Join-Path $resolvedInstallRoot "FrpDesk.exe"))
$running = Get-Process FrpDesk -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and [string]::Equals([IO.Path]::GetFullPath($_.Path), $installedExe, [StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
}
if ($running) { throw "FrpDesk 正在运行，请先退出托盘中的 FrpDesk，再卸载。" }

$serviceNames = @(Get-Service -Name "FrpDesk-Instance-*" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
if ($serviceNames.Count -gt 0) {
    throw '检测到仍有 FRP 后台服务。请先打开 FrpDesk，关闭所有实例的“后台服务”，再运行卸载。'
}

$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\FrpDesk.lnk"
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name "FrpDesk" -ErrorAction SilentlyContinue
}
if (Test-Path $resolvedInstallRoot) { Remove-Item $resolvedInstallRoot -Recurse -Force }
if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
Write-Host "FrpDesk 已卸载，开机启动项已移除。用户配置和日志保留在 %LOCALAPPDATA%\FrpDesk。"
