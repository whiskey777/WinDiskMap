# Пофайловая сверка CSV WizTree и DiskMap с нормализацией известных особенностей WizTree:
#   - точки в концах имён без расширения (SOFTWARE. -> SOFTWARE);
#   - reparse-папки не выгружаются в экспорт WizTree (это не расхождение).
# DiskMap CSV должен быть с русскими заголовками (--lang ru).
param(
    [Parameter(Mandatory=$true)][string]$WizCsv,
    [Parameter(Mandatory=$true)][string]$DmCsv,
    [int]$Top = 15
)
$ErrorActionPreference = 'Stop'

$wtLines = [System.IO.File]::ReadAllLines($WizCsv, [System.Text.Encoding]::UTF8)
if ($wtLines.Count -lt 2) { throw "Пустой CSV WizTree: $WizCsv" }
$wtRows = ($wtLines[1..($wtLines.Count-1)]) -join "`n" | ConvertFrom-Csv -Delimiter ','
$dmRows = [System.IO.File]::ReadAllText($DmCsv, [System.Text.Encoding]::UTF8) | ConvertFrom-Csv -Delimiter ';'

$dmFile = @{}; $dmDir = @{}
foreach ($r in $dmRows) {
    $p = if ([string]::IsNullOrEmpty($r.Родитель)) { $r.Имя } else { "$($r.Родитель)\$($r.Имя)" }
    $p = $p.TrimEnd('\')
    if ($r.Тип -eq 'Файл') { $dmFile[$p] = [int64]$r.РазмерБайт } else { $dmDir[$p] = [int64]$r.РазмерБайт }
}

$wtFile = @{}; $wtDir = @{}; $dotFixed = 0
foreach ($r in $wtRows) {
    $raw = $r.'Имя файла'
    $p = $raw.TrimEnd('\')
    $sz = [int64]$r.'Размер'
    if ($raw.EndsWith('\')) { $wtDir[$p] = $sz; continue }
    $key = $p
    if (-not $dmFile.ContainsKey($key) -and $p.EndsWith('.')) {
        $t = $p.TrimEnd('.')
        if ($dmFile.ContainsKey($t)) { $key = $t; $dotFixed++ }
    }
    $wtFile[$key] = $sz
}

$missing = @($wtFile.Keys | Where-Object { -not $dmFile.ContainsKey($_) })
$missingSum = ($missing | ForEach-Object { $wtFile[$_] } | Measure-Object -Sum).Sum
$extra = @($dmFile.Keys | Where-Object { -not $wtFile.ContainsKey($_) })
$extraSum = ($extra | ForEach-Object { $dmFile[$_] } | Measure-Object -Sum).Sum

$diffs = @(foreach ($k in $wtFile.Keys) {
    if ($dmFile.ContainsKey($k) -and $dmFile[$k] -ne $wtFile[$k]) {
        [pscustomobject]@{ Path=$k; Delta=($dmFile[$k] - $wtFile[$k]) }
    }
})

"dot-normalized paths: $dotFixed"
"missing: $($missing.Count) bytes=$missingSum"
$missing | Sort-Object { $wtFile[$_] } -Descending | Select-Object -First $Top | ForEach-Object { "  $($wtFile[$_].ToString('N0'))  $_" }
""
"extra: $($extra.Count) bytes=$extraSum"
$extra | Sort-Object { $dmFile[$_] } -Descending | Select-Object -First $Top | ForEach-Object { "  $($dmFile[$_].ToString('N0'))  $_" }
""
"size mismatches: $($diffs.Count) sum=$((($diffs | Measure-Object Delta -Sum).Sum))"
$diffs | Sort-Object { [math]::Abs($_.Delta) } -Descending | Select-Object -First $Top | ForEach-Object { "  $($_.Delta.ToString('N0'))  $($_.Path)" }
""
$wtSum = ($wtFile.Values | Measure-Object -Sum).Sum
$dmSum = ($dmFile.Values | Measure-Object -Sum).Sum
"TOTAL wt=$wtSum dm=$dmSum delta=$($dmSum - $wtSum)"
"dirs: wt=$($wtDir.Count) dm=$($dmDir.Count)"
"files: wt=$($wtFile.Count) dm=$($dmFile.Count)"

if ($missing.Count -or $extra.Count -or $diffs.Count -or ($dmSum -ne $wtSum)) { exit 1 }
exit 0
