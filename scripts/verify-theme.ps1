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
    $flags = [Reflection.BindingFlags]'Static,Public,NonPublic'

    $isThemeKey = $theme.GetMethod('IsThemeKey', $flags)
    Assert-True $isThemeKey.Invoke($null, @(New-Key 't' ([ConsoleKey]::T))) 'латинская T'
    Assert-True $isThemeKey.Invoke($null, @(New-Key ([char]0x0435) ([ConsoleKey]::E))) 'кириллическая е (физическая T в ru)'
    Assert-True $isThemeKey.Invoke($null, @(New-Key ([char]0x0415) ([ConsoleKey]::E) $true)) 'кириллическая Е с Shift'
    Assert-True (-not $isThemeKey.Invoke($null, @(New-Key 'e' ([ConsoleKey]::E)))) 'латинская e — экспорт, не тема'
    Assert-True (-not $isThemeKey.Invoke($null, @(New-Key 's' ([ConsoleKey]::S)))) 's — сортировка'

    $all = $theme.GetField('All', $flags).GetValue($null)
    $ids = @($all | ForEach-Object { $_.Id })
    Assert-True (($ids -join ',') -eq 'vscode-dark,nc-far,classic') 'порядок тем: VS Code → FAR → классика'

    $init = $theme.GetMethod('Init', $flags)
    $cycle = $theme.GetMethod('Cycle', $flags)
    $index = $theme.GetProperty('Index', $flags)
    $current = $theme.GetProperty('Current', $flags)

    $init.Invoke($null, $null)
    Assert-True ($index.GetValue($null) -eq 0) 'тема по умолчанию — VS Code Dark'

    [void]$cycle.Invoke($null, $null)
    Assert-True ($current.GetValue($null).Id -eq 'nc-far') 'цикл: следующая тема — Нортон/FAR'
    $settings = $env:WINDISKMAP_THEME_FILE
    Assert-True ((Get-Content -LiteralPath $settings -Raw).Trim() -eq 'nc-far') 'выбор записан в theme.txt'

    $init.Invoke($null, $null)
    Assert-True ($index.GetValue($null) -eq 1) 'выбор восстановлен при следующем запуске'

    Set-Content -LiteralPath $settings -Value 'no-such-theme' -NoNewline
    $init.Invoke($null, $null)
    Assert-True ($index.GetValue($null) -eq 0) 'неизвестное значение — тема по умолчанию'

    "THEME OK"
}
finally {
    $env:WINDISKMAP_THEME_FILE = $savedThemeFile
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
