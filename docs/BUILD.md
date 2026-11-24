# Сборка и упаковка

## Требования

- Windows 10/11/Server.
- .NET SDK 8 (для `dotnet build` и `dotnet publish`).
- PowerShell 7 (`pwsh`) или Windows PowerShell 5.1 — скрипты используют только совместимые команды.

## Сборка

```powershell
.\scripts\build.ps1
```

Скрипт делает `dotnet build src\WinDiskMap\WinDiskMap.csproj -c Release` и проверяет результат:

| Каталог | Файл | Для чего |
|---|---|---|
| `src\WinDiskMap\bin\Release\net8.0\` | `WinDiskMap.exe` | Основная сборка; для запуска нужен .NET 8 Runtime |
| `src\WinDiskMap\bin\Release\net48\` | `WinDiskMap.exe` | Агент WinRM; .NET Framework 4.8 входит в Windows |

Оба exe — одна и та же программа с двумя целевыми платформами. Роль определяется аргументами
(`--agent`, `--remote`, `--agent-local`), а не файлом — см. [CLI.md](CLI.md) и
[ARCHITECTURE.md](ARCHITECTURE.md).

## Portable-архивы

```powershell
.\scripts\package.ps1                        # net48, ~50 КБ
.\scripts\package.ps1 -SelfContained         # плюс net8.0 win-x64, ~30 МБ
.\scripts\package.ps1 -Version 1.1.0         # версия в имени архива и README.txt
```

Результат в `artifacts\`:

| Архив | Содержимое |
|---|---|
| `WinDiskMap-portable-net48-v1.0.0.zip` | `WinDiskMap.exe`, `WinDiskMap.exe.config`, `README.txt`, `LICENSE.txt`, `SHA256SUMS.txt` |
| `WinDiskMap-portable-win-x64-selfcontained-v1.0.0.zip` | Один `WinDiskMap.exe` (single-file publish, без .NET Runtime) и те же текстовые файлы |

Архивы не требуют установки, не пишут в реестр и не включаются в git. `SHA256SUMS.txt`
содержит хэши всех файлов внутри архива — их можно сверить перед запуском в корпоративной
среде.

## Версионирование

Версия задаётся параметром `-Version` скрипта `package.ps1` (по умолчанию `1.0.0`) и пока
не хранится в исходниках как константа. При выпуске релиза обновляйте её одновременно в
скрипте сборки и в примечаниях к релизу.

## Подпись exe

Portable-сборки получаются **неподписанными**: SmartScreen и EDR могут реагировать на новый
хэш. Возможные варианты:

1. **Самоподписанный сертификат** — бесплатно, но не даёт доверия Windows; подходит только
   для тестов и внутреннего распространения с установкой сертификата в хранилище.
2. **[SignPath Foundation](https://signpath.org/)** — бесплатная подпись для открытых проектов
   с публичным репозиторием и OSI-лицензией; ключ хранится в HSM, подпись ставится в CI.
   Требует соответствия условиям проекта (активность, документация, роли код-ревью и одобрения
   релиза), сертификат выпускается на имя фонда.
3. **[Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/overview)** —
   управляемый сервис Microsoft с публичным доверием, но требует платной подписки Azure и
   прохождения проверки личности; бесплатная подписка не поддерживается.
4. **Коммерческий OV/EV-сертификат** — самый прямой путь к «нормальному» доверию и репутации
   SmartScreen, но платный; EV дополнительно ускоряет накопление репутации.

Отдельного файла политики подписи в репозитории пока нет.

## Структура репозитория

```
src/WinDiskMap/      исходники (net8.0 + net48)
  Core/              скан, MFT, обход, индекс, CSV
  Core/Remote/       агент, NDJSON, транспорты, хост удалённого скана
  Tui/               терминальный интерфейс
scripts/             сборка и упаковка
docs/                подробная документация
artifacts/           готовые ZIP (в git не включены)
verification/        результаты проверок
```
