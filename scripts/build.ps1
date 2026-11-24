# Сборка Release: net8.0 (основной запуск) + net48 (агент WinRM).
# Версия берётся из csproj, если не передана явно: -Version 1.0.0-beta.1
param(
    [string]$Version
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\WinDiskMap\WinDiskMap.csproj'

if (-not (Test-Path $proj)) { throw "Не найден проект: $proj" }

if (-not $Version) {
    $match = [regex]::Match((Get-Content -LiteralPath $proj -Raw), '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw "Не найден <Version> в $proj" }
    $Version = $match.Groups[1].Value
}

Write-Host "==> dotnet build $proj -c Release -p:Version=$Version"
& dotnet build $proj -c Release "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$out8 = Join-Path $root 'src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll'
$out48 = Join-Path $root 'src\WinDiskMap\bin\Release\net48\WinDiskMap.exe'
foreach ($f in @($out8, $out48)) {
    if (Test-Path $f) {
        $item = Get-Item $f
        "OK  $($item.FullName)  $($item.Length) bytes"
    } else {
        throw "Не собран: $f"
    }
}
