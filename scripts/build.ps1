# Сборка Release: net8.0 (основной запуск) + net48 (агент WinRM).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\DiskMap\DiskMap.csproj'

if (-not (Test-Path $proj)) { throw "Не найден проект: $proj" }

Write-Host "==> dotnet build $proj -c Release"
& dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$out8 = Join-Path $root 'src\DiskMap\bin\Release\net8.0\DiskMap.dll'
$out48 = Join-Path $root 'src\DiskMap\bin\Release\net48\DiskMap.exe'
foreach ($f in @($out8, $out48)) {
    if (Test-Path $f) {
        $item = Get-Item $f
        "OK  $($item.FullName)  $($item.Length) bytes"
    } else {
        throw "Не собран: $f"
    }
}
