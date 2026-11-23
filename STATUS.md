# STATUS — состояние проекта (handoff)

Обновлено: 2026-11-23. Проект перенесён в отдельную git-папку
`C:\Users\akorn\Documents\DiskMap\` из `C:\Users\akorn\Documents\Default Project\src\DiskMap\`.
Исходники при переносе не изменялись; сборки `bin/obj` в репозиторий не включены.

## Версии и окружение

| Что | Значение |
|---|---|
| Target frameworks | `net8.0` (основной запуск), `net48` (агент WinRM) |
| Сборка | `dotnet build src\DiskMap\DiskMap.csproj -c Release` → 0 warning / 0 error |
| .NET SDK | установленный на машине разработчика (проект собирался в 2026 г.) |
| ОС | Windows 10/11 (MFT + TUI через P/Invoke) |
| Эталон сравнения | WizTree 64 (`C:\Program Files\WizTree\WizTree64.exe`) |
| Контрольный том | `C:\Windows`, NTFS, ~56.1 ГБ файловых байт |

## Что сделано

### Фаза 1 — локальный движок (завершена, расхождений 0)

- `Core/Mft/MftScanner.cs` — прямое чтение `$MFT`, обработка `$ATTRIBUTE_LIST`, FN-дедуп,
  `$DATA` из extension-записей, reparse-флаги, пустая метка тома. Эталон WizTree:
  **0 расхождений по объёму**.
- `Core/Win32Find.cs` + `Core/DirectoryWalk.cs` — обход через `FindFirstFileExW` с
  `FindExInfoBasic`, параллельно, с одним потоком-сборщиком (`IWalkSink`). Числа не
  зависят от потока. Время на `C:\Windows`: **3.57 с** против **6.64 с** у WizTree.
- `Core/ScanIndex.cs`, `Core/ScanRunner.cs`, `Core/TreeBuilder.cs` — единое дерево и счётчики
  для всех режимов.
- `Tui/TuiApp.cs` — живое дерево, 2 локали, SGR-мышь, чистый выход, Ctrl+C.
- `Core/CsvExporter.cs` — UTF-8 BOM, `;`, фильтры глубины/типа, `--details`.

### Фаза 2 — удалённый скан (транспорт и протокол готовы, боевой WinRM не проверен)

- `Core/Remote/IAgentTransport.cs`, `PowerShellTransport.cs` — WinRM: `New-PSSession`,
  `Copy-Item -ToSession` net48-агента в `%TEMP%` хоста, запуск с `--agent`, вывод в файл,
  чтение порциями как UTF-8 (`OUT\t` / `ERR\t`), очистка сессии и файлов.
- `Core/Remote/LocalAgentTransport.cs` — тот же протокол через локальный дочерний процесс
  (`--agent-local`), для отладки без WinRM.
- `Core/Remote/AgentRunner.cs` — роль агента: MFT при админе и локальном NTFS, иначе обход;
  запись поддерева в NDJSON (BFS, родитель раньше ребёнка, id 0 = корень).
- `Core/Remote/NdJson.cs`, `NdJsonWriter.cs` — минимальный JSON без зависимостей, UTF-8 без
  BOM, одна запись на строку, escape для имён файлов (`\uXXXX` для управляющих).
- `Core/Remote/RemoteScanRunner.cs` — хост: парсинг потока в живое дерево через `TreeBuilder`,
  подмена корня `C:\Windows` → `\\host\C$\Windows`, обработка `error`/оборванного потока.
- Проверено локально: `--agent-local` по `C:\Windows` → **197 558 папок / 226 958 файлов /
  52.3 ГБ**, CSV байт-в-байт совпадает с WizTree (missing 0, extra 0, size mismatches 0,
  TOTAL delta 0). Сборка Release — чистая.

## Проверки (21.10.2026, перенесены в verification/)

| Проверка | Результат |
|---|---|
| WizTree vs DiskMap, обход каталогов, `C:\Windows` | 197 558 папок у обеих, TOTAL 56 146 966 797 байт у обеих, delta 0 |
| WizTree vs DiskMap, пофайлово (с нормализацией `.` в именах без расширения) | missing 0, extra 0, расхождений размера 0 |
| WizTree vs `--agent-local`, пофайлово | missing 0, extra 0, расхождений размера 0, TOTAL delta 0 |
| Скорость обхода (не elevated) | DiskMap 3.57 с, WizTree 6.64 с |
| MFT-режим | 0 расхождений по объёму с WizTree на контрольном томе |

Исходные CSV (73–77 МБ каждый) остались в `C:\temp\` и намеренно **не** включены в git:
`wt_na.csv`, `dm_na.csv`, `dm_agent.csv`, `wt_full*.csv`, `dm_full*.csv`.
Скрипты сверки лежат в `scripts/`, краткие отчёты — в `verification/`.

## Открытые концы / что делать дальше

1. **Боевой прогон WinRM до `10.10.1.107`.** На клиенте `TrustedHosts` пуст; на хосте нужно
   `Enable-PSRemoting`, Basic + AllowUnencrypted (или Kerberos), правило firewall 5985.
   После настройки: `--remote 10.10.1.107 --remote-user ... --remote-pass ... --path C:\Windows`
   и сверка CSV с эталоном. Последняя правка — `Encoding.UTF8` + `-InputFormat None
   -OutputFormat Text` и защита парсинга чанка в `PowerShellTransport.cs`; собрана, но на
   реальном хосте **не проверялась**. Ранее текст ошибки отображался кракозябрами —
   проверить кодировку ещё раз.
2. **MFT-ветка агента под админом** на удалённом хосте не тестировалась (локально прав нет).
   Ожидается тот же результат, что у локального MFT-скана — требует подтверждения.
3. **Документация приведена в соответствие с Фазой 2** (этот файл, README, docs/) —
   исходный `README.md` в `Default Project` остался прежним (только Фаза 1 + планы).
   Если проект продолжится здесь, считать канонической эту папку.

## Как продолжить

```powershell
cd C:\Users\akorn\Documents\DiskMap
.\scripts\build.ps1
# отладка протокола без WinRM:
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll --agent-local --path C:\Windows --csv a.csv
# полная сверка с WizTree (нужен установленный WizTree, обычный PowerShell без админа):
pwsh -File .\scripts\verify-against-wiztree.ps1
```
