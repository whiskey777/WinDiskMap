# WinDiskMap

[![ci](https://github.com/whiskey777/WinDiskMap/actions/workflows/ci.yml/badge.svg)](https://github.com/whiskey777/WinDiskMap/actions/workflows/ci.yml)
[![codeql](https://github.com/whiskey777/WinDiskMap/actions/workflows/codeql.yml/badge.svg)](https://github.com/whiskey777/WinDiskMap/actions/workflows/codeql.yml)

Консольный анализатор занятого места на диске для Windows: живое TUI-дерево, точный учёт
размера, CSV-экспорт и удалённый скан по WinRM.

## Описание

WinDiskMap показывает, что занимает место на диске, прямо в терминале. Дерево появляется
сразу и пополняется по мере обхода; привилегированный режим читает `$MFT` напрямую,
портативный — обходит каталоги через Win32. Результат можно выгрузить в CSV или получить
с удалённого хоста — с тем же движком подсчёта.

## Возможности

- **TUI без ожидания** — дерево растёт на глазах, фаза и счётчики в шапке; корректный выход
  по Ctrl+C и мышь SGR (колесо, клик).
- **Два режима сбора** — прямое чтение `$MFT` (администратор, локальный NTFS) и фолбэк
  обходом каталогов (без прав, UNC, не-NTFS). Оба дают одинаковые цифры.
- **Удалённый скан по WinRM** — на хост копируется только `WinDiskMap.exe` сборки net48, данные
  идут потоком NDJSON, дерево и CSV строятся локально.
- **CSV** — UTF-8 с BOM, разделитель `,`, потоковая запись, фильтры и ограничение глубины.
- **Headless и журнал** — `--headless` для скриптов, `--log <файл>` для диагностики.
- **Два языка интерфейса** — ru/en, автоопределение по локали ОС.

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

Только Windows. Готовый portable-архив собирается из исходников:

```powershell
git clone https://github.com/whiskey777/WinDiskMap.git
cd WinDiskMap
.\scripts\build.ps1
.\scripts\package.ps1 -SelfContained
```

В `artifacts\` появятся два архива:

| Архив | Что внутри | Когда выбирать |
|---|---|---|
| `WinDiskMap-portable-net48-v1.0.0.zip` | `WinDiskMap.exe` (~50 КБ), использует .NET Framework 4.8, встроенный в Windows | Обычный выбор на Windows 10/11/Server. Именно этот EXE WinRM-транспорт копирует на удалённый хост как агента. |
| `WinDiskMap-portable-win-x64-selfcontained-v1.0.0.zip` | один `WinDiskMap.exe` (~30 МБ) со встроенным .NET 8 | Нужен, если на машине нет .NET 8 и не хочется зависеть от системного Framework. |

Оба архива функционально одинаковы — разница только в runtime. Они распаковываются в любую
папку, ничего не устанавливают и не пишут в реестр; внутри — `WinDiskMap.exe`, `README.txt`,
`LICENSE.txt` и `SHA256SUMS.txt`.

Для работы из исходников после `build.ps1` используйте
`src\WinDiskMap\bin\Release\net48\WinDiskMap.exe` (агент WinRM; .NET Framework 4.8 уже в ОС) или
`src\WinDiskMap\bin\Release\net8.0\WinDiskMap.exe` (нужен .NET 8 Runtime).
Подробности — [сборка и упаковка](docs/BUILD.md).

## Использование

```
WinDiskMap.exe <путь> [опции]
WinDiskMap.exe --remote <хост> --path <путь на хосте> [опции]
```

| Опция | Назначение |
|---|---|
| `--csv <файл>` | экспорт в CSV (вместо TUI) |
| `--headless` | без TUI: прогресс и итоговый отчёт в stdout |
| `--log <файл>` | журнал с таймстемпами: фазы, счётчики, ошибки |
| `--details` | с `--only dirs`: добавить в CSV строку на каждый файл |
| `--depth <N>` | ограничить глубину строк CSV (`0` — только корень) |
| `--only <режим>` | `dirs` (по умолчанию), `files`, `tmp`, `dumps` |
| `--no-mft` | отключить прямое чтение `$MFT` |
| `--lang <ru\|en>` | язык интерфейса (по умолчанию — язык ОС) |
| `--remote <хост>` | скан каталога на удалённом хосте через WinRM |
| `--remote-user <user>` / `--remote-pass <pass>` | явные учётные данные WinRM |
| `--path <путь>` | путь скана (обязателен с `--remote`/`--agent`) |
| `--agent` | служебный режим: NDJSON в stdout (запускается хостом) |
| `--agent-local` | отладка протокола локальным процессом-агентом |
| `--debug-mft <маска>` | дамп атрибутов MFT-записей с такими именами в `--log` (`;` между масками) |
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

Коды выхода: `0` — успех, `1` — ошибка аргументов, `2` — ошибка сканирования, `3` — нет
терминала для TUI. Полный справочник — [CLI](docs/CLI.md).

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
| колесо / левый клик | прокрутка / курсор и раскрытие папки |
| `q`, Esc | выход |

## CSV

Один формат для всех режимов: UTF-8 с BOM, разделитель `,`, без строки-баннера. Заголовки
и значения (`Folder`, `File`, `Temp`, `Dump`) всегда английские, независимо от `--lang`.

```csv
File name,Size,On disk,Modified,Attributes,Files,Folders,Depth,Parent,Name,Category,SizeHuman,Percent,DRIVECAPACITY,FREESPACE,USEDSPACE,RESERVEDSPACE
```

`--only dirs` (по умолчанию) пишет агрегаты папок, `--details` добавляет файлы, `--depth`
ограничивает глубину. Разбор полей и правила подсчёта — [формат CSV](docs/CSV-FORMAT.md).

## Документация

- [Механика работы](docs/MECHANICS.md) — конвейер скана, MFT и обход, учёт размеров, даты.
- [Интерфейс TUI](docs/TUI.md) — отрисовка, колонки, фильтры, мышь, очистка экрана.
- [Формат CSV](docs/CSV-FORMAT.md) — все колонки, режимы экспорта, примеры разбора.
- [CLI](docs/CLI.md) — все опции, комбинации, коды выхода.
- [Архитектура удалённого скана](docs/ARCHITECTURE.md) — роли процесса, NDJSON, WinRM.
- [Настройка WinRM](docs/REMOTE-SETUP.md) — чеклист для клиента и хоста.
- [Проверка точности](docs/BENCHMARK.md) — как проводится сравнение.
- [Сборка и упаковка](docs/BUILD.md) — скрипты, артефакты, версии, подпись.
- [Непрерывные проверки](docs/CI.md) — CodeQL, Dependabot, Dependency Review, защита ветки.
- [История изменений](CHANGELOG.md).
- [Указатель документации](docs/README.md).

## Ограничения

- Только Windows (P/Invoke `kernel32`/`user32`, прямое чтение `$MFT`).
- MFT-режим требует прав администратора и локального NTFS; для UNC всегда используется обход.
- WinRM требует настройки хоста; поток NDJSON оптимизирован (чанки 2 МБ, декодирование в C#),
  удалённый MFT-режим проверен на Windows Server 2016.
- Подпись архивов включается при настроенном SignPath; текущие Release-архивы неподписаны —
  в корпоративной среде возможны предупреждения SmartScreen/EDR
  (см. [сборку](docs/BUILD.md)).

## Лицензия

MIT — см. [LICENSE](LICENSE).
