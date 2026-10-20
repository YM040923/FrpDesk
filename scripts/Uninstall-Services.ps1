param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot
)

$ErrorActionPreference = "Stop"
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([char[]]@('\', '/'))
$serviceHostRoot = Join-Path $env:ProgramData "FrpDesk\ServiceHosts"
$installPrefix = $install + [IO.Path]::DirectorySeparatorChar
$serviceHostPrefix = $serviceHostRoot.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar

$services = @(Get-CimInstance Win32_Service -Filter "Name LIKE 'FrpDesk-Instance-%'" -ErrorAction SilentlyContinue)
foreach ($service in $services) {
    $pathName = [string]$service.PathName
    $owned = $pathName.IndexOf($installPrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $pathName.IndexOf($serviceHostPrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $owned) { continue }
    try { Stop-Service -Name $service.Name -Force -ErrorAction SilentlyContinue } catch { }
    & sc.exe delete $service.Name | Out-Null
}

Get-Process -Name FrpDesk -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $path = $_.Path
        if ($path -and $path.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }
    catch { }
}

if (Test-Path -LiteralPath $serviceHostRoot) {
    $remaining = @(Get-CimInstance Win32_Service -Filter "Name LIKE 'FrpDesk-Instance-%'" -ErrorAction SilentlyContinue)
    $remainingPaths = @($remaining | ForEach-Object { [string]$_.PathName })
    foreach ($directory in Get-ChildItem -LiteralPath $serviceHostRoot -Directory -ErrorAction SilentlyContinue) {
        $fullPath = $directory.FullName
        $inUse = $remainingPaths | Where-Object {
            $_.IndexOf($fullPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
        }
        if (-not $inUse) {
            Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not (Get-ChildItem -LiteralPath $serviceHostRoot -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $serviceHostRoot -Force -ErrorAction SilentlyContinue
    }
}
