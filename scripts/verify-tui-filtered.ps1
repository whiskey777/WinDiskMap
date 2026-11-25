# Проверка строки <Filtered>: она должна показывать размер/даты файлов, скрытых фильтром.
# Тест работает с приватными членами TUI через reflection — без интерактивной консоли.
param(
    [string]$Config = 'Release'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$dll  = Join-Path $root "src\WinDiskMap\bin\$Config\net8.0\WinDiskMap.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "Нет сборки: $dll" }

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    "ok  $message"
}

$asm = [Reflection.Assembly]::LoadFrom($dll)
$flags = [Reflection.BindingFlags]'Static,Instance,Public,NonPublic'

$nodeType    = $asm.GetType('WinDiskMap.Core.Node', $true)
$sessionType = $asm.GetType('WinDiskMap.Core.ScanSession', $true)
$appType     = $asm.GetType('WinDiskMap.Tui.TuiApp', $true)
$filterType  = $asm.GetType('WinDiskMap.Tui.FilterMode', $true)

function New-Node([string]$path) {
    $n = [Activator]::CreateInstance($nodeType, @($null))
    $n.Name = Split-Path -Leaf $path
    $n.FullPath = $path
    $n.Expanded = $true
    return $n
}

function Add-LiveFile($node, [string]$name, [long]$size, [long]$lastWriteUtc) {
    $m = $nodeType.GetMethod('AddLiveFile', $flags)
    [void]$m.Invoke($node, @($name, $size, 0, $lastWriteUtc, 0, [uint32]0))
}

$rootNode = New-Node 'C:\Test'
$child = New-Node 'C:\Test\Sub'
$child.Parent = $rootNode
$rootNode.Dirs.Add($child)

# В корне: 2 скрытых в режиме «только папки», в подпапке — 1.
Add-LiveFile $rootNode 'big.tmp' 1000 200
Add-LiveFile $rootNode 'note.txt' 500 300
Add-LiveFile $child 'core.dmp' 300 400

$session = [Activator]::CreateInstance($sessionType)
$session.Root = $rootNode
$session.Done = $true

$app = [Activator]::CreateInstance($appType, @($session))
$rowsField  = $appType.GetField('_rows', $flags)
$filterField = $appType.GetField('_filter', $flags)
$rebuild = $appType.GetMethod('RebuildRows', $flags)

function Get-Rows($app) {
    return @([System.Collections.IList]$rowsField.GetValue($app))
}

function Field($row, [string]$name) {
    return $row.GetType().GetField($name, $flags).GetValue($row)
}

# Режим «только папки»: все файлы скрыты, но папки видны.
$filterField.SetValue($app, [Enum]::Parse($filterType, 'Folders'))
[void]$rebuild.Invoke($app, $null)
$rows = Get-Rows $app

$hints = @($rows | Where-Object { [bool](Field $_ 'Hint') -and [string](Field $_ 'Name') -match '\(\d+\)' })
Assert-True ($hints.Count -eq 2) 'для двух раскрытых папок добавлены строки <Filtered>'

$rootHint = @($hints | Where-Object { [long](Field $_ 'Size') -eq 1500 })
Assert-True ($rootHint.Count -eq 1) 'в корне показана сумма скрытых файлов (1000 + 500)'
Assert-True ([string](Field $rootHint[0] 'Name') -match '\(2\)$') 'в имени указано число скрытых файлов'
Assert-True ([long](Field $rootHint[0] 'LastWriteUtc') -eq 300) 'в строке самая свежая дата скрытого файла'

$childHint = @($hints | Where-Object { [long](Field $_ 'Size') -eq 300 })
Assert-True ($childHint.Count -eq 1) 'в подпапке показана сумма скрытых файлов'
Assert-True ([long](Field $childHint[0] 'LastWriteUtc') -eq 400) 'дата из подпапки не «залипает» на родителе'

# Режим «только tmp»: скрыты обычный файл и дамп, виден .tmp.
$filterField.SetValue($app, [Enum]::Parse($filterType, 'Temp'))
[void]$rebuild.Invoke($app, $null)
$rows = Get-Rows $app
$hints = @($rows | Where-Object { [bool](Field $_ 'Hint') -and [string](Field $_ 'Name') -match '\(\d+\)' })
$rootHint = @($hints | Where-Object { [long](Field $_ 'Size') -eq 500 })
Assert-True ($rootHint.Count -eq 1) 'в режиме tmp показан размер скрытого note.txt'
Assert-True ([string](Field $rootHint[0] 'Name') -match '\(1\)$') 'скрыт ровно один файл'
Assert-True (@($rows | Where-Object { [string](Field $_ 'Name') -eq 'big.tmp' }).Count -eq 1) 'big.tmp остаётся видимым'

"FILTERED OK"
