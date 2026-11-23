# Контрольный замер "без админа" с полной сверкой данных.
#   1) WizTree — чистый скан (живой процесс убит, старый CSV удалён);
#   2) DiskMap — обход каталогов;
#   3) пофайловая сверка двух CSV (с нормализацией WizTree-точки у имён без расширения).
# Запускать в ОБЫЧНОМ (не elevated) PowerShell: pwsh -File .\scripts\verify-against-wiztree.ps1
param(
    [string]$Target = 'C:\Windows',
    [string]$WizTree = 'C:\Program Files\WizTree\WizTree64.exe',
    [string]$WorkDir = $env:TEMP
)
$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
$dm = Join-Path $root 'src\DiskMap\bin\Release\net8.0\DiskMap.dll'
if (-not (Test-Path $dm)) { throw "Сначала соберите проект: .\scripts\build.ps1 ($dm)" }
if (-not (Test-Path $WizTree)) { throw "Не найден WizTree: $WizTree" }

$wtCsv = Join-Path $WorkDir 'diskmap-verify-wt.csv'
$dmCsv = Join-Path $WorkDir 'diskmap-verify-dm.csv'
$log   = Join-Path $WorkDir 'diskmap-verify.log'

function Invoke-WizTreeFresh([string]$outCsv) {
    Get-Process WizTree64 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
    if (Test-Path $outCsv) { Remove-Item $outCsv -Force }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $WizTree $Target /export=$outCsv | Out-Null

    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-Path $outCsv) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50 }
    while ((Test-Path $outCsv) -and (Get-Item $outCsv).Length -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    Get-Process WizTree64 -ErrorAction SilentlyContinue | Stop-Process -Force
    $sw.Stop()
    return $sw.Elapsed.TotalSeconds
}

function Measure-DiskMap([string]$outCsv) {
    if (Test-Path $outCsv) { Remove-Item $outCsv -Force }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet $dm $Target --headless --csv $outCsv --log $log --details --no-mft | Out-Null
    $sw.Stop()
    return $sw.Elapsed.TotalSeconds
}

"=== 1/3  WizTree (no admin, fresh scan) ==="
$wtTime = Invoke-WizTreeFresh $wtCsv
"WizTree : $($wtTime.ToString('0.000')) s"

"=== 2/3  DiskMap (directory walk) ==="
$dmTime = Measure-DiskMap $dmCsv
"DiskMap : $($dmTime.ToString('0.000')) s"

"=== 3/3  сверка CSV ==="
& (Join-Path $PSScriptRoot 'compare-csv.ps1') -WizCsv $wtCsv -DmCsv $dmCsv
$cmpRc = $LASTEXITCODE

""
"--- ИТОГ ---"
"время: WizTree $($wtTime.ToString('0.000')) s | DiskMap $($dmTime.ToString('0.000')) s | DiskMap/WizTree = $([math]::Round($dmTime/[math]::Max($wtTime,0.001),2))x"
"журнал: $log"
exit $cmpRc
