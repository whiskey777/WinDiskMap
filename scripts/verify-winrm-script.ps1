# Проверка генерируемого PowerShell-скрипта WinRM без подключения к удалённому хосту.
# Ловим синтаксические ошибки и следим за параметрами потокового чтения NDJSON.
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
$type = $asm.GetType('WinDiskMap.Core.Remote.PowerShellTransport', $true)
$transport = [Activator]::CreateInstance($type, @('host', '', ''))
$method = $type.GetMethod('BuildScript', [Reflection.BindingFlags]'Instance,NonPublic,Public')
$script = [string]$method.Invoke($transport, @('agent.exe', 'C:\Test', $false, 'out.ndjson'))

$tokens = $null
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors)
Assert-True ($errors.Count -eq 0) "синтаксис PowerShell ($($errors.Count) ошибок)"

Assert-True ($script -match '262144') 'чтение NDJSON ограничено чанком 256 КБ'
Assert-True ($script -match 'GetDecoder\(\)') 'используется stateful UTF-8 декодер'
Assert-True (([regex]::Matches($script, 'Wait-Job \$job')).Count -eq 1) 'ожидание задачи выполняется один раз'
Assert-True (([regex]::Matches($script, 'while \(\$true\)')).Count -eq 2) 'есть основной цикл и дочитывание хвоста'
Assert-True ($script -match 'Remove-PSSession \$s, \$poll') 'закрываются обе сессии'

"WINRM SCRIPT OK"
