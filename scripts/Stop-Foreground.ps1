param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot
)

$ErrorActionPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'

function Get-TargetProcessIds {
    $ids = @()
    foreach ($name in @('FrpDesk', 'frpc')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                $path = $process.Path
            }
            catch {
                continue
            }
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            if (-not ([IO.Path]::GetFullPath($path)).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $ids += $process.Id
        }
    }
    return $ids
}

$ids = @(Get-TargetProcessIds)
if ($ids.Count -gt 0) {
    Stop-Process -Id $ids -Force -ErrorAction SilentlyContinue
}

$deadline = (Get-Date).AddSeconds(15)
while ($true) {
    $remaining = @(Get-TargetProcessIds)
    if ($remaining.Count -eq 0) { break }
    if ((Get-Date) -ge $deadline) {
        [Console]::Error.WriteLine("Processes under $root are still running.")
        exit 1
    }
    Start-Sleep -Milliseconds 250
}
exit 0
