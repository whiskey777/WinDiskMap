# Сверка первых 7 колонок внешнего экспорта с WinDiskMap.
# У внешнего экспорта перед заголовком может быть строка-баннер и локализованные
# заголовки — читаем позиционно. У WinDiskMap заголовок всегда английский, баннера нет.
param(
    [Parameter(Mandatory=$true)][string]$ExternalCsv,
    [Parameter(Mandatory=$true)][string]$DmCsv,
    [int]$Top = 15,
    [switch]$IgnoreModified
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Read-Rows([string]$path, [int]$skip) {
    $lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
    if ($lines.Count -le $skip) { throw "Пустой CSV: $path" }
    return @((($lines[$skip..($lines.Count-1)]) -join "`n") | ConvertFrom-Csv -Delimiter ',')
}
function Col($row, [int]$idx) {
    $names = @($row.PSObject.Properties.Name)
    if ($names.Count -le $idx) { throw "Слишком мало колонок: $($row | Out-String)" }
    return $row.($names[$idx])
}

$wt = Read-Rows $ExternalCsv 1
$dm = Read-Rows $DmCsv 0
"rows: wt=$($wt.Count) dm=$($dm.Count)"

$dmMap = @{}
foreach ($r in $dm) { $dmMap[[string](Col $r 0)] = $r }
$wtMap = @{}
foreach ($r in $wt) { $wtMap[[string](Col $r 0)] = $r }

$missing = @($wtMap.Keys | Where-Object { -not $dmMap.ContainsKey($_) })
$extra = @($dmMap.Keys | Where-Object { -not $wtMap.ContainsKey($_) })
"path sets: missing=$($missing.Count) extra=$($extra.Count)"
$missing | Select-Object -First $Top | ForEach-Object { "  MISSING $_" }
$extra   | Select-Object -First $Top | ForEach-Object { "  EXTRA   $_" }

$names = @{ 1='Size'; 2='On disk'; 3='Modified'; 4='Attributes'; 5='Files'; 6='Folders' }
$stats = @{}
$examples = @{}
foreach ($i in 1..6) { $stats[$i] = 0; $examples[$i] = @() }

foreach ($k in $wtMap.Keys) {
    if (-not $dmMap.ContainsKey($k)) { continue }
    $w = $wtMap[$k]; $d = $dmMap[$k]
    foreach ($i in 1..6) {
        if ($i -eq 3 -and $IgnoreModified) { continue }
        $a = [string](Col $w $i)
        $b = [string](Col $d $i)
        if ($a -ne $b) {
            $stats[$i]++
            if ($examples[$i].Count -lt $Top) {
                $examples[$i] += [pscustomobject]@{ Path=$k; W=$a; D=$b }
            }
        }
    }
}

$bad = $false
foreach ($i in 1..6) {
    if ($i -eq 3 -and $IgnoreModified) { continue }
    ""
    "$($names[$i]): $($stats[$i]) mismatches"
    $examples[$i] | ForEach-Object { "  W=$($_.W) D=$($_.D)  $($_.Path)" }
    if ($stats[$i] -gt 0) { $bad = $true }
}

if ($missing.Count -or $extra.Count) { $bad = $true }
""
"RESULT: $(if($bad){'MISMATCH'}else{'OK'})"
if ($bad) { exit 1 }
exit 0
