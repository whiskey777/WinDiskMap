# Проверка переключателя тем TUI: раскладка клавиши T, порядок тем и сохранение выбора.
# WINDISKMAP_THEME_FILE направляет настройки во временную папку, чтобы не трогать
# реальный %APPDATA%\WinDiskMap\theme.txt.
param(
    [string]$Config = 'Release'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$dll  = Join-Path $root "src\WinDiskMap\bin\$Config\net8.0\WinDiskMap.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "Нет сборки: $dll" }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('wdm-theme-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$savedThemeFile = $env:WINDISKMAP_THEME_FILE
$savedNoTrueColor = $env:WINDISKMAP_NO_TRUECOLOR
$savedWtSession = $env:WT_SESSION
$savedTermProgram = $env:TERM_PROGRAM
$savedColorTerm = $env:COLORTERM
$env:WINDISKMAP_THEME_FILE = Join-Path $sandbox 'theme.txt'

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    "ok  $message"
}

function New-Key([char]$char, [ConsoleKey]$key, [bool]$shift = $false) {
    [ConsoleKeyInfo]::new($char, $key, $shift, $false, $false)
}

try {
    $asm   = [Reflection.Assembly]::LoadFrom($dll)
    $theme = $asm.GetType('WinDiskMap.Tui.Theme', $true)
    $flags = [Reflection.BindingFlags]'Static,Instance,Public,NonPublic'

    $isThemeKey = $theme.GetMethod('IsThemeKey', $flags)
    Assert-True $isThemeKey.Invoke($null, @(New-Key 't' ([ConsoleKey]::T))) 'латинская T'
    Assert-True $isThemeKey.Invoke($null, @(New-Key ([char]0x0435) ([ConsoleKey]::E))) 'кириллическая е (физическая T в ru)'
    Assert-True $isThemeKey.Invoke($null, @(New-Key ([char]0x0415) ([ConsoleKey]::E) $true)) 'кириллическая Е с Shift'
    Assert-True (-not $isThemeKey.Invoke($null, @(New-Key 'e' ([ConsoleKey]::E)))) 'латинская e — экспорт, не тема'
    Assert-True (-not $isThemeKey.Invoke($null, @(New-Key 's' ([ConsoleKey]::S)))) 's — сортировка'

    $all = $theme.GetField('All', $flags).GetValue($null)
    $ids = @($all | ForEach-Object { $_.Id })
    Assert-True (($ids -join ',') -eq 'modern-dark,nc-far,classic') 'порядок тем: Modern Dark → FAR → классика'

    $esc = [string][char]27
    $hard = $theme.GetField('HardReset', $flags).GetValue($null)
    Assert-True ($hard -eq ($esc + '[0m')) 'жёсткий сброс при выходе'

    $tui = $asm.GetType('WinDiskMap.Tui.TuiApp', $true)
    $label = [string]$tui.GetField('VersionLabel', $flags).GetValue($null)
    $info = [string]($asm.GetCustomAttributesData() |
        Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute' } |
        Select-Object -First 1).ConstructorArguments[0].Value
    $info = ($info -split '\+')[0]
    Assert-True ($label -eq $info) "версия в шапке TUI: $label"
    Assert-True ($label -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') 'версия сборки — корректный SemVer'

    # Палитра не должна зависеть от схемы терминала: никаких индексированных
    # 256-цветов и базовых 16 кодов, только явный RGB.
    $seqFields = @('Reset','Dim','Header','Folder','File','Temp','Dump','Warn','SortActive','Key')
    $bad = @()
    foreach ($p in $all) {
        foreach ($n in $seqFields) {
            $v = [string]$p.GetType().GetField($n, $flags).GetValue($p)
            if ($v -match '38;5;|48;5;|\[(9[0-7]|3[0-7]|4[0-7])m') { $bad += "$($p.Id).$n" }
        }
        $reset = [string]$p.GetType().GetField('Reset', $flags).GetValue($p)
        Assert-True ($reset -match '48;2;') "$($p.Id): фон задан в RGB"
    }
    Assert-True ($bad.Count -eq 0) "RGB у всех тем ($($bad -join ','))"

    # Старый conhost (Windows Server 2016 / build 14393) не понимает 38;2/48;2:
    # там используется запасной набор из базовых 16 цветов. Папки не должны
    # становиться «синими», а подсказки — сплошь жёлтыми.
    $legacy = $theme.GetField('Legacy', $flags).GetValue($null)
    $legacyIds = @($legacy | ForEach-Object { $_.Id }) -join ','
    Assert-True ($legacyIds -eq 'modern-dark,nc-far,classic') 'резервные палитры в том же порядке'
    $badLegacy = @()
    foreach ($p in $legacy) {
        foreach ($n in $seqFields) {
            $v = [string]$p.GetType().GetField($n, $flags).GetValue($p)
            if ($v -match '38;2;|48;2;|38;5;|48;5;') { $badLegacy += "$($p.Id).$n" }
        }
    }
    Assert-True ($badLegacy.Count -eq 0) "резерв без RGB/256 ($($badLegacy -join ','))"
    $legacyFolder = [string]$legacy[0].GetType().GetField('Folder', $flags).GetValue($legacy[0])
    Assert-True ($legacyFolder -notmatch '(^|;)(34|94)(;|m)') 'Modern Dark в старом conhost: папки не синие'
    $legacyDim = [string]$legacy[0].GetType().GetField('Dim', $flags).GetValue($legacy[0])
    $legacyKey = [string]$legacy[0].GetType().GetField('Key', $flags).GetValue($legacy[0])
    Assert-True ($legacyKey -ne $legacyDim) 'резерв: клавиши отличаются от описаний'

    # Клавиши в подсказках должны быть визуально отделены от описаний.
    $loc = $asm.GetType('WinDiskMap.Core.Loc', $true)
    $hints = $loc.GetProperty('KeyHints', $flags).GetValue($null)
    $keys = @($hints | ForEach-Object { $_.Key }) -join ','
    Assert-True ($keys -eq '↑↓,t,←,→,Enter,f,s,e,Del,q') 'подсказки: все клавиши на месте'
    $keySeq = [string]$all[0].GetType().GetField('Key', $flags).GetValue($all[0])
    $dimSeq = [string]$all[0].GetType().GetField('Dim', $flags).GetValue($all[0])
    Assert-True ($keySeq -ne $dimSeq -and $keySeq -match '38;2;') 'клавиши подсказок выделены отдельным RGB-цветом'

    $init = $theme.GetMethod('Init', $flags)
    $cycle = $theme.GetMethod('Cycle', $flags)
    $index = $theme.GetProperty('Index', $flags)
    $current = $theme.GetProperty('Current', $flags)

    $init.Invoke($null, $null)
    Assert-True ($index.GetValue($null) -eq 0) 'тема по умолчанию — Modern Dark'

    [void]$cycle.Invoke($null, $null)
    Assert-True ($current.GetValue($null).Id -eq 'nc-far') 'цикл: следующая тема — Нортон/FAR'
    $settings = $env:WINDISKMAP_THEME_FILE
    Assert-True ((Get-Content -LiteralPath $settings -Raw).Trim() -eq 'nc-far') 'выбор записан в theme.txt'

    $init.Invoke($null, $null)
    Assert-True ($index.GetValue($null) -eq 1) 'выбор восстановлен при следующем запуске'

    Set-Content -LiteralPath $settings -Value 'no-such-theme' -NoNewline
    $init.Invoke($null, $null)
    Assert-True ($index.GetValue($null) -eq 0) 'неизвестное значение — тема по умолчанию'

    [void]$cycle.Invoke($null, $null)
    Set-Content -LiteralPath $settings -Value 'vscode-dark' -NoNewline
    $init.Invoke($null, $null)
    Assert-True ($current.GetValue($null).Id -eq 'modern-dark') 'старый id vscode-dark совместим'

    $trueColor = $theme.GetProperty('TrueColor', $flags)
    $env:WINDISKMAP_NO_TRUECOLOR = '1'
    $init.Invoke($null, $null)
    Assert-True (-not [bool]$trueColor.GetValue($null)) 'старый conhost: 16-цветный резерв включён'
    Assert-True ($current.GetValue($null).Folder -eq $legacyFolder) 'старый conhost: белые папки вместо синих'

    $env:WINDISKMAP_NO_TRUECOLOR = '0'
    $env:WT_SESSION = 'verify-theme'
    $init.Invoke($null, $null)
    Assert-True ([bool]$trueColor.GetValue($null)) 'Windows Terminal: сохранён RGB'
    Assert-True ($current.GetValue($null).Folder -match '38;2;') 'Windows Terminal: RGB-папки'

    "THEME OK"
}
finally {
    $env:WINDISKMAP_THEME_FILE = $savedThemeFile
    $env:WINDISKMAP_NO_TRUECOLOR = $savedNoTrueColor
    $env:WT_SESSION = $savedWtSession
    $env:TERM_PROGRAM = $savedTermProgram
    $env:COLORTERM = $savedColorTerm
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
