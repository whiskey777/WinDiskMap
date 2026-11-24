# Пофайловая сверка внешнего экспорта и WinDiskMap (combined-формат).
#   - оба файла — стандартный CSV с разделителем ',';
#   - колонки читаются ПОЗИЦИОННО (0=имя, 1=размер, 2=на диске), поэтому язык
#     заголовков не важен: у внешнего экспорта они могут быть локализованы,
#     у WinDiskMap всегда EN;
#   - точки в концах имён без расширения (SOFTWARE. -> SOFTWARE) нормализуются;
#   - reparse-папки могут отсутствовать во внешнем экспорте (это не расхождение).
param(
    [Parameter(Mandatory=$true)][string]$ExternalCsv,
    [Parameter(Mandatory=$true)][string]$DmCsv,
    [int]$Top = 15
)
$ErrorActionPreference = 'Stop'

function Get-Col($row, [int]$idx) {
    # У PSMemberInfoIntegratingCollection нет осмысленного .Count/.Item(int),
    # поэтому берём имена отдельным массивом и обращаемся к значению по имени.
    $names = @($row.PSObject.Properties.Name)
    if ($names.Count -le $idx) { throw "Слишком мало колонок в строке CSV: $($row | Out-String)" }
    return $row.($names[$idx])
}

# У внешнего экспорта перед заголовком может быть строка-баннер, у WinDiskMap её нет.
$wtLines = [System.IO.File]::ReadAllLines($ExternalCsv, [System.Text.Encoding]::UTF8)
if ($wtLines.Count -lt 2) { throw "Пустой CSV эталона: $ExternalCsv" }
$wtRows = ($wtLines[1..($wtLines.Count-1)]) -join "`n" | ConvertFrom-Csv -Delimiter ','
$dmRows = [System.IO.File]::ReadAllText($DmCsv, [System.Text.Encoding]::UTF8) | ConvertFrom-Csv -Delimiter ','

$dmFile = @{}; $dmDir = @{}; $dmAlloc = @{}
foreach ($r in $dmRows) {
    $raw = [string](Get-Col $r 0)
    $p = $raw.TrimEnd('\')
    $sz = [int64](Get-Col $r 1)
    if ($raw.EndsWith('\')) { $dmDir[$p] = $sz; continue }
    $dmFile[$p] = $sz
    $a = [string](Get-Col $r 2)
    if ($a) { $dmAlloc[$p] = [int64]$a }
}

$wtFile = @{}; $wtDir = @{}; $wtAlloc = @{}; $dotFixed = 0
foreach ($r in $wtRows) {
    $raw = [string](Get-Col $r 0)
    $p = $raw.TrimEnd('\')
    $sz = [int64](Get-Col $r 1)
    if ($raw.EndsWith('\')) { $wtDir[$p] = $sz; continue }
    $key = $p
    if (-not $dmFile.ContainsKey($key) -and $p.EndsWith('.')) {
        $t = $p.TrimEnd('.')
        if ($dmFile.ContainsKey($t)) { $key = $t; $dotFixed++ }
    }
    $wtFile[$key] = $sz
    $a = [string](Get-Col $r 2)
    if ($a) { $wtAlloc[$key] = [int64]$a }
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

$allocDiffs = @(foreach ($k in $wtFile.Keys) {
    if ($dmAlloc.ContainsKey($k) -and $wtAlloc.ContainsKey($k) -and $dmAlloc[$k] -ne $wtAlloc[$k]) {
        [pscustomobject]@{ Path=$k; Delta=($dmAlloc[$k] - $wtAlloc[$k]) }
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
"on-disk mismatches: $($allocDiffs.Count) sum=$((($allocDiffs | Measure-Object Delta -Sum).Sum))"
$allocDiffs | Sort-Object { [math]::Abs($_.Delta) } -Descending | Select-Object -First $Top | ForEach-Object { "  $($_.Delta.ToString('N0'))  $($_.Path)" }
""
$wtSum = ($wtFile.Values | Measure-Object -Sum).Sum
$dmSum = ($dmFile.Values | Measure-Object -Sum).Sum
"TOTAL wt=$wtSum dm=$dmSum delta=$($dmSum - $wtSum)"
"dirs: wt=$($wtDir.Count) dm=$($dmDir.Count)"
"files: wt=$($wtFile.Count) dm=$($dmFile.Count)"

if ($missing.Count -or $extra.Count -or $diffs.Count -or ($dmSum -ne $wtSum)) { exit 1 }
exit 0
