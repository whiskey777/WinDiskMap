# Проверка точности и скорости

Эталон — WizTree 64. Контрольный путь — `C:\Windows` (NTFS, ~197 558 папок,
~226 958 файлов, 56 146 966 797 байт файловых данных).

## Полный автоматический прогон

```powershell
# обычный (не elevated) PowerShell, WizTree должен быть установлен
pwsh -File .\scripts\verify-against-wiztree.ps1 -Target 'C:\Windows'
```

Скрипт:

1. убивает живой процесс WizTree, удаляет старый CSV, делает чистый `/export`;
2. запускает DiskMap `--headless --details` с замером времени;
3. сравнивает CSV **пофайлово**, нормализуя известную особенность WizTree: точки в концах
   имён без расширения (`SOFTWARE.` → `SOFTWARE`); reparse-папки в экспорте WizTree
   отсутствуют — это не считается расхождением;
4. печатает: missing/extra/размеры/итог/папки/время.

Ожидаемый результат (21.10.2026):

```
нет у нас      : 0
лишних у нас   : 0
расхождений р-ра: 0
папки          : wt=197558 dm=197558
файловые байты : 56146966797 | 56146966797 | Δ 0
время          : WizTree 6.640 s | DiskMap 3.570 s | 0.54x
```

## Ручная сверка двух CSV

```powershell
pwsh -File .\scripts\compare-csv.ps1 -WizCsv C:\temp\wt_na.csv -DmCsv C:\temp\dm_agent.csv
```

Скрипт ожидает WizTree CSV (разделитель `,`, первая строка — заголовок, каталоги
заканчиваются на `\`) и DiskMap CSV (`;`, BOM, колонки `Тип;...;РазмерБайт`).
На english-ОС колонки DiskMap другие — используйте русскую локаль (`--lang ru`) для
контрольных прогонов.

## MFT-режим

```powershell
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll C:\Windows --headless --csv mft.csv --log mft.log
```

Требуется администратор. Сверка та же (`compare-csv.ps1`): 0 расхождений по объёму.
В отчёте должно быть «MFT (прямое чтение $MFT)», в логе — статистика парсера. Если MFT
не удался, в примечании будет `[stats: ...]` / `[lookup: ...]` и режим переключится на
обход — это не считается ошибкой движка, но требует разбора.

## Удалённый агент

```powershell
dotnet src\DiskMap\bin\Release\net8.0\DiskMap.dll --agent-local --path C:\Windows --csv agent.csv
pwsh -File .\scripts\compare-csv.ps1 -WizCsv C:\temp\wt_na.csv -DmCsv .\agent.csv
```

Результат 21.10.2026: 197 558 папок / 226 958 файлов / 52.3 ГБ, missing 0, extra 0,
size mismatches 0, TOTAL delta 0 — CSV совпадает с WizTree байт-в-байт.

## Важные оговорки

- В MFT-режиме hardlink учитывается в каждом каталоге-владельце: `C:\Windows` ≈ 72 ГБ
  (WinSxS ↔ System32), а не «уникальные» 34 ГБ. Это уже семантика WizTree.
- WizTree добавляет точку к именам без расширения и не выгружает reparse-папки (CSC, Ngc) —
  нормализация в скриптах обязательна.
- В ранних итерациях сравнения (`cmp4_report.txt`) были «лишние» файлы из-за живо меняющегося
  тома (ETW-логи, SRU, Protectors.json) — финальный замер делается с убитым процессом WizTree
  и полной нормализацией, расхождений нет.
- Большие CSV (73–77 МБ) в git не коммитятся; они остаются в `C:\temp\` на машине разработчика.
