# Собирает portable ZIP-пакеты в artifacts/ (без установки, просто распаковать и запустить).
#   .\scripts\package.ps1                         — net48 (маленький, для Win10/11/Server)
#   .\scripts\package.ps1 -SelfContained          — плюс net8.0 win-x64 single-file (~30 МБ, без .NET Runtime)
# Версия по умолчанию берётся из <Version> в csproj — один источник для exe и архивов.
param(
    [string]$Version,
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $PSScriptRoot
$proj      = Join-Path $root 'src\WinDiskMap\WinDiskMap.csproj'
$release48 = Join-Path $root 'src\WinDiskMap\bin\Release\net48'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

if (-not $Version) {
    $match = [regex]::Match((Get-Content -LiteralPath $proj -Raw), '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw "Не найден <Version> в $proj" }
    $Version = $match.Groups[1].Value
}
"VERSION $Version"

function Write-Utf8Bom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

function New-Readme([string]$path, [string]$exeName, [string]$variant) {
    $text = @"
WinDiskMap $Version — portable ($variant)
=====================================

Запуск: распаковать в любую папку и запустить $exeName.
Ничего устанавливать не нужно; запись в реестр не используется.

  $exeName --help
  $exeName C:\Users --headless
  $exeName C:\Windows --csv result.csv --no-mft
  $exeName --agent-local --path C:\Windows

Требования: Windows 10/11/Server, консоль с поддержкой ANSI/VT (Windows Terminal
или modern conhost). MFT-режим требует администратора и локального NTFS;
без админа работает обход каталогов (--no-mft).

ВНИМАНИЕ ДЛЯ КОРПОРАТИВНОЙ СРЕДЫ:
- Если WinDiskMap.exe подписан (сборка из Releases), проверьте вкладку
  «Цифровые подписи» в свойствах файла. Для неподписанных сборок SmartScreen/EDR
  могут выдать предупреждение — согласуйте хэш из SHA256SUMS.txt со службой безопасности.
- Прямое чтение `$MFT` и WinRM-агент (-ExecutionPolicy Bypass) могут появиться
  в телеметрии EDR как подозрительные. Для минимального шума используйте --no-mft
  и локальный запуск без WinRM.
- Подробности: README.md и docs/ в репозитории проекта
  https://github.com/whiskey777/WinDiskMap

Лицензия: MIT (файл LICENSE.txt).
"@
    Write-Utf8Bom $path $text
}

function Complete-Package([string]$stage, [string]$zip) {
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt') -Force
    $sums = Get-ChildItem $stage -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
        $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$h  $($_.Name)"
    }
    Write-Utf8Bom (Join-Path $stage 'SHA256SUMS.txt') (($sums -join "`r`n") + "`r`n")
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    "OK  $zip  $((Get-Item $zip).Length) bytes"
}

# --- 1) net48: один exe, работает на Windows 10/11/Server без установки .NET ---
& (Join-Path $PSScriptRoot 'build.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$stage48 = Join-Path $artifacts 'portable-net48'
if (Test-Path $stage48) { Remove-Item $stage48 -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage48 | Out-Null
Copy-Item (Join-Path $release48 'WinDiskMap.exe') $stage48 -Force
Copy-Item (Join-Path $release48 'WinDiskMap.exe.config') $stage48 -Force
New-Readme (Join-Path $stage48 'README.txt') 'WinDiskMap.exe' 'net48 (.NET Framework 4.8 встроен в Windows)'
Complete-Package $stage48 (Join-Path $artifacts "WinDiskMap-portable-net48-v$Version.zip")

# --- 2) net8 single-file: полностью автономный exe, без .NET Runtime на хосте ---
if ($SelfContained) {
    $stageSc = Join-Path $artifacts 'portable-win-x64-selfcontained'
    if (Test-Path $stageSc) { Remove-Item $stageSc -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageSc | Out-Null

    & dotnet publish $proj -c Release -f net8.0 -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        "-p:Version=$Version" -o $stageSc
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    New-Readme (Join-Path $stageSc 'README.txt') 'WinDiskMap.exe' 'net8.0 win-x64 self-contained (без .NET Runtime)'
    Complete-Package $stageSc (Join-Path $artifacts "WinDiskMap-portable-win-x64-selfcontained-v$Version.zip")
}
