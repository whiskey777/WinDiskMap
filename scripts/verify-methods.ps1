# Сверка двух экспортов WinDiskMap: MFT (админ) и обычный обход каталогов.
# Разделитель ','; иногда Difference только в FREESPACE/USEDSPACE — сканы в разное время.
param(
    [Parameter(Mandatory=$true)][string]$MftCsv,
    [Parameter(Mandatory=$true)][string]$WalkCsv,
    [int]$Top = 8
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Read-Rows([string]$path) {
    $lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
    @(($lines -join "`n") | ConvertFrom-Csv -Delimiter ',')
}
function Col($row, [int]$idx) {
    $n = @($row.PSObject.Properties.Name)
    if ($n.Count -le $idx) { return $null }
    return $row.($n[$idx])
}

$names = @('File name','Size','On disk','Modified','Attributes','Files','Folders',
           'Depth','Parent','Name','Category','SizeHuman','Percent',
           'DRIVECAPACITY','FREESPACE','USEDSPACE','RESERVEDSPACE')

$a = Read-Rows $MftCsv
$b = Read-Rows $WalkCsv
"rows: MFT=$($a.Count) walk=$($b.Count)"

$ma = @{}; $dupA = 0
foreach ($r in $a) { $k=[string](Col $r 0); if($ma.ContainsKey($k)){$dupA++}else{$ma[$k]=$r} }
$mb = @{}; $dupB = 0
foreach ($r in $b) { $k=[string](Col $r 0); if($mb.ContainsKey($k)){$dupB++}else{$mb[$k]=$r} }
"duplicates: MFT=$dupA walk=$dupB"
$onlyA = @($ma.Keys | Where-Object { -not $mb.ContainsKey($_) })
$onlyB = @($mb.Keys | Where-Object { -not $ma.ContainsKey($_) })
"paths: only-MFT=$($onlyA.Count) only-walk=$($onlyB.Count)"
$onlyA | Select-Object -First $Top | ForEach-Object { "  ONLY MFT $_" }
$onlyB | Select-Object -First $Top | ForEach-Object { "  ONLY WALK $_" }

$bad = $false
foreach ($i in 0..12) {
    $n = 0; $ex = @()
    foreach ($k in $ma.Keys) {
        if (-not $mb.ContainsKey($k)) { continue }
        $x = [string](Col $ma[$k] $i); $y = [string](Col $mb[$k] $i)
        if ($x -cne $y) {
            $n++
            if ($ex.Count -lt $Top) { $ex += "  MFT=$x WALK=$y  $k" }
        }
    }
    "$($names[$i]): $n mismatches"
    $ex | ForEach-Object { $_ }
    if ($n -gt 0) { $bad = $true }
}

$rootA = $ma['C:\AI\']
if ($rootA -and $mb.ContainsKey('C:\AI\')) {
    $rootB = $mb['C:\AI\']
    ""
    "ROOT volume columns (may differ — scans at different times):"
    foreach ($i in 13..16) {
        "  $($names[$i]): MFT=$([string](Col $rootA $i)) WALK=$([string](Col $rootB $i))"
    }
}

""
"RESULT: $(if($bad -or $onlyA.Count -or $onlyB.Count){'MISMATCH'}else{'OK'})"
if ($bad -or $onlyA.Count -or $onlyB.Count) { exit 1 }
