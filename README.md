# WinDiskMap

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
- **CSV**: UTF-8 с BOM, разделитель `,` (стандартный CSV, `Import-Csv` без параметров),
  заголовки и значения всегда английские, потоковая запись, `--depth`,
  `--only dirs|files|tmp|dumps`, `--details`.
- **Вывод**: TUI (по умолчанию), `--headless` (прогресс и отчёт в stdout),
  `--log <файл>` (журнал с таймстемпами). Комбинируются.

## Сборка

```powershell
.\scripts\build.ps1
# или:
dotnet build src\WinDiskMap\WinDiskMap.csproj -c Release
```

| Файл | Назначение |
|---|---|
| `src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll` | основной запуск: `dotnet WinDiskMap.dll ...` |
| `src\WinDiskMap\bin\Release\net48\WinDiskMap.exe` | .NET Framework 4.8; его копирует WinRM-агент |

Только Windows (P/Invoke `kernel32`/`user32`, MFT).

### Portable-пакеты

```powershell
.\scripts\package.ps1                  # artifacts\WinDiskMap-portable-net48-v1.0.0.zip (~43 КБ)
.\scripts\package.ps1 -SelfContained   # + автономный win-x64 ZIP (~30 МБ)
```

Архивы распаковываются в любую папку, ничего не устанавливают и не пишут в реестр;
внутри `README.txt`, `LICENSE.txt`, `SHA256SUMS.txt`. Артефакты в git не включены.

## Использование

```
WinDiskMap <путь> [опции]

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
dotnet src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll C:\Users
dotnet src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll C:\ --csv big.csv --depth 2
dotnet src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll C:\ --headless --csv d.csv --only dumps --log m.log
dotnet src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll \\server\share --headless
dotnet src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll --remote hostB --path C:\Windows --csv remote.csv
dotnet src\WinDiskMap\bin\Release\net8.0\WinDiskMap.dll --agent-local --path C:\Windows --csv a.csv
```

Коды выхода: `0` — ок, `1` — ошибка аргументов, `2` — ошибка сканирования, `3` — нет терминала.

### Клавиши TUI

| Клавиша | Действие |
|---|---|
| ↑ ↓, PgUp/PgDn, Home/End | навигация |
| ← | свернуть папку / перейти к родителю |
| → | развернуть папку / спуститься к первому ребёнку |
| Enter | папка — переключить свёрнутость; файл — открыть через ассоциацию Windows (ShellExecute) |
| f | фильтр: папки → папки+файлы → tmp → дампы |
| s | сортировка: размер ↓ → размер ↑ → имя ↑ → имя ↓ (активная колонка подсвечена в шапке, стрелка показывает направление) |
| e | экспорт дерева в CSV |
| Del | удалить файл/папку с диска после подтверждения `y/N` (корень скана не удаляется) |
| колесо мыши / левый клик | прокрутка / курсор + раскрыть папку |
| q, Esc | выход |

Окно перерисовывается автоматически при изменении размера консоли. Диалог удаления —
центрированный блок с красной рамкой; безопасный ответ `N` выбран по умолчанию. Две статусные
строки (счётчик/фильтр/сортировка и подсказка клавиш) привязаны к нижнему краю окна и не
перемещаются при раскрытии деревьев или смене фильтра.

В дереве есть шапка колонок `Путь / Размер / % / Создан / Посл. запись`. Даты берутся из
`$STANDARD_INFORMATION` (фолбэк — `$FILE_NAME`) в MFT-режиме и из `WIN32_FIND_DATA` при обходе
каталогов; показываются в местном времени. Между колонками — по два пробела, перед группой дат
отступ шире (колонки разной ширины не слипаются, но экран не загрязнён лишними линиями).
На узком терминале сначала скрываются даты, затем процент — размер остаётся всегда.
Заглушка показывает три состояния: `...` во время скана, `<Пусто>` / `<Empty>` для реально
пустой папки и `<Отфильтровано>` / `<Filtered>` когда объекты есть, но фильтр их скрывает.

## CSV

Единый формат «WizTree +»: первые семь колонок совпадают с экспортом WizTree по имени
и смыслу, затем идут наши поля, а в строке корня — параметры тома:

```
File name,Size,On disk,Modified,Attributes,Files,Folders,
Depth,Parent,Name,Category,SizeHuman,Percent,
DRIVECAPACITY,FREESPACE,USEDSPACE,RESERVEDSPACE
```

- `Size` — логический размер (как у WizTree), `On disk` — реально занятые кластеры
  (в MFT-режиме из `$DATA`, при обходе каталогов из `FILE_STANDARD_INFO.AllocationSize`).
  Даты — локальное время в формате `yyyy.MM.dd HH:mm:ss`, как в экспорте WizTree.
- Разделитель `,`, UTF-8 с BOM. `Import-Csv` работает без параметров:
  `Import-Csv report.csv | Where-Object Size -gt 1GB`.
- Заголовки и значения (`Folder`/`File`/`Temp`/`Dump`) **всегда английские** и не зависят
  от `--lang` или локали ОС. Строки-баннера перед заголовком нет (в отличие от WizTree).
- `--only dirs` (по умолчанию) — агрегаты папок; с `--details` — плюс каждый файл.
- `--only files|tmp|dumps` — только файлные строки.
- `--depth N` — строки глубже N не пишутся (файл в папке уровня N — строка глубины N+1).

Для пофайловой сверки с WizTree: `scripts\verify-against-wiztree.ps1` и
`scripts\compare-csv.ps1 -WizCsv <wt.csv> -DmCsv <dm.csv>`.

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
src/WinDiskMap/     исходники (net8.0 + net48)
  Core/          скан, MFT, обход, CSV, дерево
  Core/Remote/   агент, NDJSON, транспорты, хост удалённого скана
  Tui/           терминальный интерфейс
scripts/         сборка и упаковка portable-архивов
```

Лицензия: MIT — см. [LICENSE](LICENSE).
