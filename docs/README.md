# Документация WinDiskMap

Краткое описание, установка и примеры — в [корневом README](../README.md). Здесь собраны
подробности механики работы и настройки.

## Как это работает

| Документ | О чём |
|---|---|
| [MECHANICS.md](MECHANICS.md) | Конвейер скана: MFT и обход каталогов, индекс, дерево, учёт logical/allocated, hardlink, даты, ошибки |
| [TUI.md](TUI.md) | Отрисовка дерева, колонки, фильтры, сортировка, мышь, очистка экрана |
| [CSV-FORMAT.md](CSV-FORMAT.md) | Schema CSV, все колонки, режимы `--only`, `--depth`, `--details` |
| [CLI.md](CLI.md) | Полный справочник опций, комбинации, коды выхода |

## Удалённый скан

| Документ | О чём |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Роли одного exe, протокол NDJSON, транспорт WinRM |
| [REMOTE-SETUP.md](REMOTE-SETUP.md) | Чеклист настройки WinRM, права, отладка через `--agent-local` |

## Разработка

| Документ | О чём |
|---|---|
| [BUILD.md](BUILD.md) | Сборка net8.0/net48, portable-архивы, версионирование, хэши |
| [CI.md](CI.md) | Quality Gate: CodeQL, Dependency Review, Dependabot, защита ветки |
| [CODE-SIGNING.md](CODE-SIGNING.md) | Политика подписи, конфигурация SignPath, роли и одобрение релиза |
| [BENCHMARK.md](BENCHMARK.md) | Методика проверки точности и воспроизводимые замеры |

Для локальной разработки также есть скрипты в [`scripts/`](../scripts): сборка, упаковка и
сверка репортов.
