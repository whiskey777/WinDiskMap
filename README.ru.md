# WinDiskMap

[English](README.md) | Русский

[![ci](https://github.com/whiskey777/WinDiskMap/actions/workflows/ci.yml/badge.svg)](https://github.com/whiskey777/WinDiskMap/actions/workflows/ci.yml)
[![codeql](https://github.com/whiskey777/WinDiskMap/actions/workflows/codeql.yml/badge.svg)](https://github.com/whiskey777/WinDiskMap/actions/workflows/codeql.yml)

[![Download](https://img.shields.io/github/v/release/whiskey777/WinDiskMap?label=Download&style=for-the-badge&logo=github)](https://github.com/whiskey777/WinDiskMap/releases/latest)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%2FServer-0078D6?logo=windows&logoColor=white)

Консольный анализатор занятого места на диске для Windows. Отображает дерево каталогов в терминале, подсчитывает размеры файлов и папок, экспортирует результаты в CSV и поддерживает удалённое сканирование по WinRM.

## Описание

WinDiskMap отображает структуру каталогов и размеры объектов в терминале. Дерево формируется в ходе сканирования и обновляется по мере поступления данных. Привилегированный режим читает `$MFT` напрямую, портативный режим обходит каталоги через Win32. Результаты можно экспортировать в CSV или получить с удалённого хоста с помощью того же алгоритма подсчёта.

## Возможности

- **TUI с обновлением в процессе сканирования** — дерево формируется по мере обхода; в заголовке отображаются текущая фаза и счётчики; выход по Ctrl+C и управление мышью SGR (колесо, щелчок).
- **Два режима сбора данных** — прямое чтение `$MFT` (требует прав администратора и локального NTFS) и обход каталогов (без повышенных прав, UNC, не-NTFS). Оба режима обеспечивают одинаковые результаты.
- **Удалённое сканирование по WinRM** — на удалённый хост передаётся только `WinDiskMap.exe` сборки net48; данные передаются потоком NDJSON; дерево и CSV формируются локально.
- **CSV-экспорт** — UTF-8 с BOM, разделитель `,`, потоковая запись, фильтры и ограничение глубины.
- **Headless-режим и журналирование** — `--headless` для скриптов, `--log <файл>` для диагностики.
- **Два языка интерфейса** — ru/en, язык определяется по локали операционной системы.

## Содержание

- [Описание](#описание)
- [Возможности](#возможности)
- [Установка](#установка)
- [Использование](#использование)
- [Клавиши TUI](#клавиши-tui)
- [CSV](#csv)
- [Документация](#документация)
- [Ограничения](#ограничения)
- [Лицензия](#лицензия)

## Установка

Поддерживается только Windows. Готовые архивы публикуются в [последнем релизе](https://github.com/whiskey777/WinDiskMap/releases/latest); сборка из исходников выполняется так:

```powershell
git clone https://github.com/whiskey777/WinDiskMap.git
cd WinDiskMap
.\scripts\build.ps1
.\scripts\package.ps1 -SelfContained
```

В `artifacts\` появятся два архива:

| Архив | Состав | Рекомендуется |
|---|---|---|
| `WinDiskMap-portable-net48-v1.0.0.zip` | `WinDiskMap.exe` (~50 КБ), использует .NET Framework 4.8, встроенный в Windows | Для Windows 10/11/Server. Этот же файл WinRM-транспорт передаёт на удалённый хост как агента. |
| `WinDiskMap-portable-win-x64-selfcontained-v1.0.0.zip` | один `WinDiskMap.exe` (~30 МБ) со встроенным .NET 8 | Если .NET 8 не установлен и не требуется зависимость от системного .NET Framework. |

Архивы функционально эквивалентны; разница заключается только в среде выполнения. Они распаковываются в любую папку, не требуют установки и не изменяют реестр; в состав входят `WinDiskMap.exe`, `README.txt`, `LICENSE.txt` и `SHA256SUMS.txt`.

Для работы из исходников после `build.ps1` используйте
`src\WinDiskMap\bin\Release\net48\WinDiskMap.exe` (агент WinRM; .NET Framework 4.8 уже в ОС) или
`src\WinDiskMap\bin\Release\net8.0\WinDiskMap.exe` (требуется .NET 8 Runtime).
Подробнее — [сборка и упаковка](docs/BUILD.md).

## Использование

```
WinDiskMap.exe <путь> [опции]
WinDiskMap.exe --remote <хост> --path <путь на хосте> [опции]
```

| Опция | Назначение |
|---|---|
| `--csv <файл>` | экспорт в CSV (вместо TUI) |
| `--headless` | без TUI: прогресс и итоговый отчёт в stdout |
| `--log <файл>` | журнал с метками времени: фазы, счётчики, ошибки |
| `--details` | с `--only dirs`: добавить в CSV строку на каждый файл |
| `--depth <N>` | ограничить глубину строк CSV (`0` — только корень) |
| `--only <режим>` | `dirs` (по умолчанию), `files`, `tmp`, `dumps` |
| `--no-mft` | отключить прямое чтение `$MFT` |
| `--lang <ru\|en>` | язык интерфейса (по умолчанию — язык ОС) |
| `--remote <хост>` | сканирование каталога на удалённом хосте через WinRM |
| `--remote-user <user>` / `--remote-pass <pass>` | явные учётные данные WinRM |
| `--path <путь>` | путь сканирования (обязателен с `--remote`/`--agent`) |
| `--agent` | служебный режим: вывод NDJSON в stdout (используется хостом) |
| `--agent-local` | отладка протокола локальным процессом-агентом |
| `--debug-mft <маска>` | вывод в `--log` атрибутов MFT-записей с указанными именами (`;` между масками) |
| `--help` | справка |

Примеры:

```powershell
WinDiskMap.exe C:\Users
WinDiskMap.exe C:\ --csv big.csv --depth 2
WinDiskMap.exe C:\ --headless --csv dumps.csv --only dumps --log scan.log
WinDiskMap.exe \\server\share --headless
WinDiskMap.exe --remote hostB --path C:\Windows --csv remote.csv
WinDiskMap.exe --agent-local --path C:\Windows --csv agent.csv
```

Коды выхода: `0` — успешное завершение, `1` — ошибка в аргументах, `2` — ошибка сканирования, `3` — недоступен терминал для TUI. Полное описание — [CLI](docs/CLI.md).

## Клавиши TUI

| Клавиша | Действие |
|---|---|
| ↑ ↓, PgUp/PgDn, Home/End | навигация |
| ← / → | свернуть / развернуть папку (или перейти к родителю/первому потомку) |
| Enter | папка — развернуть/свернуть; файл — открыть ассоциацией Windows |
| `f` | цикл фильтров: папки → папки и файлы → tmp → дампы |
| `s` | цикл сортировок: размер ↓ → размер ↑ → имя ↑ → имя ↓ |
| `e` | экспорт дерева в CSV |
| Del | удалить файл/папку после подтверждения `y/N` (корень не удаляется) |
| колесо / левый клик | прокрутка / перемещение курсора и раскрытие папки |
| `q`, Esc | выход |

## CSV

Один формат для всех режимов: UTF-8 с BOM, разделитель `,`, без строки-баннера. Заголовки
и значения (`Folder`, `File`, `Temp`, `Dump`) всегда английские, независимо от `--lang`.

```csv
File name,Size,On disk,Modified,Attributes,Files,Folders,Depth,Parent,Name,Category,SizeHuman,Percent,DRIVECAPACITY,FREESPACE,USEDSPACE,RESERVEDSPACE
```

`--only dirs` (по умолчанию) записывает агрегаты папок, `--details` добавляет файлы, `--depth`
ограничивает глубину. Описание полей и правила подсчёта приведены в [формате CSV](docs/CSV-FORMAT.md).

## Документация

- [Механика работы](docs/MECHANICS.md) — конвейер сканирования, MFT и обход, учёт размеров, даты.
- [Интерфейс TUI](docs/TUI.md) — отрисовка, колонки, фильтры, мышь, очистка экрана.
- [Формат CSV](docs/CSV-FORMAT.md) — все колонки, режимы экспорта, примеры разбора.
- [CLI](docs/CLI.md) — все опции, комбинации, коды выхода.
- [Архитектура удалённого скана](docs/ARCHITECTURE.md) — роли процессов, NDJSON, WinRM.
- [Настройка WinRM](docs/REMOTE-SETUP.md) — чеклист для клиента и хоста.
- [Проверка точности](docs/BENCHMARK.md) — методика сравнения.
- [Сборка и упаковка](docs/BUILD.md) — скрипты, артефакты, версии, подпись.
- [Непрерывные проверки](docs/CI.md) — CodeQL, Dependabot, Dependency Review, защита ветки.
- [История изменений](CHANGELOG.md).
- [Указатель документации](docs/README.md).

## Ограничения

- Только Windows (P/Invoke `kernel32`/`user32`, прямое чтение `$MFT`).
- MFT-режим требует прав администратора и локального NTFS; для UNC всегда используется обход каталогов.
- WinRM требует настройки хоста; поток NDJSON оптимизирован (чанки 2 МБ, декодирование в C#), удалённый MFT-режим проверен на Windows Server 2016.
- Подпись архивов выполняется при настроенном SignPath. Текущие архивы неподписаны; в корпоративной среде возможны предупреждения SmartScreen/EDR (см. [сборку](docs/BUILD.md)).

## Лицензия

MIT — см. [LICENSE](LICENSE).
