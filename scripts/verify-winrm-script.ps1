# Проверка генерируемого PowerShell-скрипта WinRM без подключения к удалённому хосту.
# Ловим синтаксические ошибки, следим за параметрами потокового чтения NDJSON
# и проверяем C#-декодер чанков на multibyte-символах, разрезанных на границе.
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

Assert-True ((($script -split '2097152').Count - 1) -eq 2) 'чтение NDJSON ограничено чанком 2 МБ в обоих циклах'
Assert-True (-not $script.Contains('GetDecoder()')) 'декодирование UTF-8 вынесено из PowerShell в C#'
Assert-True (-not $script.Contains('-join $chars')) 'склейка символов через -join вынесена из PowerShell'
Assert-True ((($script -split "'CH'").Count - 1) -eq 2) 'чанки отдаются префиксом CH в обоих циклах'
Assert-True ($script -match 'if \(-not \$got\) \{ Start-Sleep -Milliseconds 25 \}') 'sleep только на пустом чанке'
Assert-True (([regex]::Matches($script, 'Wait-Job \$job')).Count -eq 1) 'ожидание задачи выполняется один раз'
Assert-True (([regex]::Matches($script, 'while \(\$true\)')).Count -eq 2) 'есть основной цикл и дочитывание хвоста'
Assert-True ($script -match 'Remove-PSSession \$s, \$poll') 'закрываются обе сессии'

# Декодер: кириллица разрезана ровно между байтами одного символа; вторая
# строка заканчивается без \n и должна выйти только из Flush().
$decType = $asm.GetType('WinDiskMap.Core.Remote.NdJsonChunkDecoder', $true)
$decoder = [Activator]::CreateInstance($decType)
$feed = $decType.GetMethod('Feed')
$flushMethod = $decType.GetMethod('Flush')
$collected = New-Object System.Text.StringBuilder
$emit = [Action[string]]({ param($s) [void]$collected.AppendLine($s) }.GetNewClosure())
$first  = '{"t":"note","v":"Привет"}'
$second = '{"t":"note","v":"мир"}'
$bytes = [Text.Encoding]::UTF8.GetBytes($first + "`n" + $second)
$split = [Array]::IndexOf($bytes, [byte]0xD0)
Assert-True ($split -gt 0 -and $split -lt $bytes.Length) 'найдена граница внутри многобайтового символа'
$feed.Invoke($decoder, @([Convert]::ToBase64String($bytes, 0, $split + 1), $emit))
$feed.Invoke($decoder, @([Convert]::ToBase64String($bytes, $split + 1, $bytes.Length - $split - 1), $emit))
Assert-True ($collected.ToString().Trim() -eq $first) 'первая строка собрана до вызова Flush'
$flushMethod.Invoke($decoder, @($emit))
$lines = @($collected.ToString().Trim().Split("`n") | ForEach-Object { $_.Trim() })
Assert-True ($lines.Count -eq 2) 'хвост без перевода строки отдан методом Flush'
Assert-True ($lines[1] -eq $second) 'многобайтовые имена восстановлены на границе чанков'

# Писатель агента: до Flush данные остаются в пачке, после — попадают в поток.
$writerType = $asm.GetType('WinDiskMap.Core.Remote.NdJsonWriter', $true)
$stream = [IO.MemoryStream]::new()
$ctor = $writerType.GetConstructor([type[]]@([IO.Stream]))
$writer = $ctor.Invoke([object[]]@($stream))
$writerType.GetMethod('Meta').Invoke($writer, @('walk'))
Assert-True ($stream.Length -eq 0) 'Meta() буферизуется пачкой, а не сбрасывает поток сразу'
$writerType.GetMethod('Flush').Invoke($writer, @())
$written = [Text.Encoding]::UTF8.GetString($stream.ToArray())
Assert-True ($written -eq ('{"t":"meta","mode":"walk"}' + "`n")) 'Flush() выгружает пачку целиком и с переводом строки'

"WINRM SCRIPT OK"
