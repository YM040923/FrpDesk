param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,
    [string]$ManifestPath
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $PackageRoot).Path
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $root "release-manifest.json"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "找不到发布清单：$ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$rootPrefix = $root.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
$verified = 0
foreach ($entry in @($manifest.files)) {
    $relativePath = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath)) {
        throw "发布清单包含无效路径：$relativePath"
    }

    $fullPath = [IO.Path]::GetFullPath((Join-Path $root $relativePath.Replace('/', '\')))
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布清单路径越界：$relativePath"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "发布包文件缺失：$relativePath"
    }

    $actual = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) {
        throw "发布包校验失败：$relativePath"
    }
    $verified++
}

Write-Output "Verified $verified files against $ManifestPath"
