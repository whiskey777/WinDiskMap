# DiskMap

Console disk space analyzer for Windows — a WizTree/TreeSize alternative without GUI.

Консольный анализатор занятого места на диске: живое цветное TUI-дерево, прямое чтение
`$MFT`, CSV-экспорт и удалённый скан по WinRM. Приоритет — точность счёта, совместимая
с WizTree.

## Возможности

- **TUI открывается сразу**, дерево пополняется по мере обхода. Фаза и счётчики в шапке,
  без путей текущих каталогов. После выхода экран очищается, Ctrl+C восстанавливает
  цвета, курсор и режим мыши.
- **MFT-режим** — прямое чтение `$MFT` (как у WizTree), требует администратора и локального
  NTFS. При ошибке причина пишется в журнал, скан продолжается обходом каталогов.
- **Обход каталогов** — портативный фолбэк: без админа, UNC-пути, не-NTFS. Чтение напрямую
  через `FindFirstFileExW` (`FindExInfoBasic`, без DOS 8.3); обход параллелен, но дерево
  мутирует один поток-сборщик — цифры не зависят от числа потоков.
- **Удалённый скан по WinRM** — лёгкий `net48`-агент копируется на хост через
  `Copy-Item -ToSession`, данные идут обратно потоком NDJSON, дерево и CSV строятся
  локально в реальном времени. `--agent-local` — отладка того же протокола без WinRM.
- **Сетевые пути (UNC)** — сканируются обходом (у шары нет `$MFT`); недоступный корень
  даёт внятную ошибку.
- **Цвета**: папки — голубой, файлы — белый, tmp/temp — жёлтый, дампы — пурпурный.
- **Мышь (SGR)**: колесо — прокрутка, левый клик — курсор + раскрыть/свернуть папку.
- **Два языка**: ru при русском языке интерфейса ОС, иначе en; переопределение `--lang ru|en`.
- **CSV**: UTF-8 с BOM (Excel), разделитель `;`, потоковая запись, `--depth`,
  `--only dirs|files|tmp|dumps`, `--details`.
- **Вывод**: TUI (по умолчанию), `--headless` (прогресс и отчёт в stdout),
  `--log <файл>` (журнал с таймстемпами). Комбинируются.

## Сборка

```powershell
.\scripts\build.ps1
# или:
dotnet build src\DiskMap\DiskMap.csproj -c Release
```

| Файл | Назначение |
|---|---|
| `src\DiskMap\bin\Release\net8.0\DiskMap.dll` | основной запуск: `dotnet DiskMap.dll ...` |
| `src\DiskMap\bin\Release\net48\DiskMap.exe` | .NET Framework 4.8; его копирует WinRM-агент |

Только Windows (P/Invoke `kernel32`/`user32`, MFT).

### Portable-пакеты

```powershell
.\scripts\package.ps1                  # artifacts\DiskMap-portable-net48-v1.0.0.zip (~43 КБ)
.\scripts\package.ps1 -SelfContained   # + автономный win-x64 ZIP (~30 МБ)
```

Архивы распаковываются в любую папку, ничего не устанавливают и не пишут в реестр;
внутри `README.txt`, `LICENSE.txt`, `SHA256SUMS.txt`. Артефакты в git не включены.

## Использование

```
DiskMap <путь> [опции]

  --csv <файл>       экспорт в CSV (без TUI)
  --headless         без TUI: построчный прогресс и итоговый отчёт в stdout
  --log <файл>       журнал с таймстемпами (фазы, счётчики, ошибки)
  --details          в режиме --only dirs добавить в CSV строки каждого файла
  --depth <N>        ограничить глубину строк CSV (0 — только корень)
  --only <режим>     dirs (по умолчанию) | files | tmp | dumps
  --no-mft           отключить MFT-режим
  --debug-mft <маск> дамп атрибутов записей MFT с такими именами в --log
  --lang <ru|en>     язык интерфейса (по умолчанию — язык ОС)
  --remote <хост>    сканировать каталог на удалённом хосте через WinRM
  --remote-user <пользователь>   учётные данные WinRM
  --remote-pass <пароль>         пароль WinRM (с --remote-user)
  --agent            служебный режим агента (NDJSON в stdout)
  --agent-local      отладка: скан через локальный процесс-агент (без WinRM)
  --help             справка
```

Примеры:

```powershell
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll C:\Users
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll C:\ --csv big.csv --depth 2
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll C:\ --headless --csv d.csv --only dumps --log m.log
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll \\server\share --headless
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll --remote hostB --path C:\Windows --csv remote.csv
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll --agent-local --path C:\Windows --csv a.csv
```

Коды выхода: `0` — ок, `1` — ошибка аргументов, `2` — ошибка сканирования, `3` — нет терминала.

### Клавиши TUI

| Клавиша | Действие |
|---|---|
| ↑ ↓, PgUp/PgDn, Home/End | навигация |
| ← | свернуть папку / перейти к родителю |
| → | развернуть папку / спуститься к первому ребёнку |
| Enter | переключить свёрнутость |
| f | фильтр: папки → папки+файлы → tmp → дампы |
| s | сортировка: по размеру / по имени |
| e | экспорт дерева в CSV |
| колесо мыши / левый клик | прокрутка / курсор + раскрыть папку |
| q, Esc | выход |

## CSV

Колонки: `Тип;Глубина;Родитель;Имя;РазмерБайт;Размер;Категория;ДоляПроцент`
(на en-ОС — английские заголовки и значения `Folder`/`File`).

- `--only dirs` (по умолчанию) — агрегаты папок; с `--details` — плюс каждый файл.
- `--only files|tmp|dumps` — только файлные строки.
- `--depth N` — строки глубже N не пишутся (файл в папке уровня N — строка глубины N+1).

## Семантика счёта (совместимость с WizTree)

- Логический размер файла — основной.
- Hardlink учитывается в каждом каталоге-владельце (отдельная запись `$FILE_NAME`);
  DOS-алиас 8.3 отбрасывается при наличии длинного имени.
- `$DATA` из extension-записи `$ATTRIBUTE_LIST` побеждает FN-фолбэк; FN-ссылки расширения
  считаются дополнительными hardlink-путями, дедуп по (родитель, имя).
- Junction/symlink-папки видны в дереве с размером 0, но не входят в счётчик папок.
- `$MFT` входит в размер корня тома — как у WizTree.

Для сравнения с WizTree учтите, что его полный `/export` добавляет точку к именам без
расширения (`SOFTWARE.`) и не выгружает reparse-папки.

## Удалённый скан: мини-чеклист WinRM

На клиенте (если не домен с Kerberos), от имени администратора:

```powershell
Set-Item WSMan:\localhost\Client\TrustedHosts -Value 'hostB' -Force
```

На хосте: `Enable-PSRemoting -Force`, при Basic — `Set-Item WSMan:\localhost\Service\Auth\Basic $true`,
`Set-Item WSMan:\localhost\Service\AllowUnencrypted $true`, `Restart-Service WinRM`, порт 5985.

`--path` задаётся локально для удалённого хоста (`C:\Windows`); в дереве он отображается
как `\\hostB\C$\Windows`. Все данные передаются потоком NDJSON (UTF-8 без BOM, по одной
записи на строку: `meta`, `phase`, `note`, `dir`, `file`, `end`, `error`).

## Ограничения

- Только Windows.
- WinRM требует настройки хоста и прав; MFT-режим требует администратора.
- Portable EXE не подписан — в корпоративной среде возможны предупреждения
  SmartScreen/EDR. Для пилота используйте `--no-mft` и локальный запуск без WinRM,
  согласуйте хэш из `SHA256SUMS.txt` со службой безопасности.

## Структура

```
src/DiskMap/     исходники (net8.0 + net48)
  Core/          скан, MFT, обход, CSV, дерево
  Core/Remote/   агент, NDJSON, транспорты, хост удалённого скана
  Tui/           терминальный интерфейс
scripts/         сборка и упаковка portable-архивов
```

Лицензия: MIT — см. [LICENSE](LICENSE).
