# Результаты верификации

Контрольный прогон от **21.10.2026**. Эталон: WizTree 64, путь `C:\Windows`, NTFS.

## Сводка

| Проверка | Результат |
|---|---|
| Обход каталогов vs WizTree, пофайлово | missing 0, extra 0, size mismatches 0, TOTAL delta 0 |
| Папки | wt=197558, dm=197558 |
| Суммарные файловые байты | 56 146 966 797 у обеих |
| Время обхода (без админа) | DiskMap 3.57 с, WizTree 6.64 с |
| MFT-режим vs WizTree | 0 расхождений по объёму |
| `--agent-local` vs WizTree, пофайлово | missing 0, extra 0, size mismatches 0, TOTAL delta 0 |
| Сборка Release (net8.0 + net48) | 0 warning / 0 error |

Сырые отчёты: [2026-10-21-clean-run.txt](2026-10-21-clean-run.txt) — чистый прогон без админа.
Скрипты, которыми получены результаты: [scripts/verify-against-wiztree.ps1](../scripts/verify-against-wiztree.ps1)
и [scripts/compare-csv.ps1](../scripts/compare-csv.ps1).

## Где большие CSV

Не коммитятся в git (73–77 МБ каждый). На машине разработчика лежат в `C:\temp\`:

```
wt_na.csv       эталон WizTree, обход, чистый прогон
dm_na.csv       DiskMap, обход, тот же прогон
dm_agent.csv    DiskMap --agent-local
wt_full*.csv    ранние MFT-сравнения
dm_full*.csv    ранние MFT-прогоны
```

Промежуточные отчёты ранних итераций (`C:\temp\cmp*_report.txt`) показывали расхождения,
обусловленные живым томом и нормализацией имён; финальный результат — в таблице выше.

## Воспроизведение

```powershell
cd C:\Users\akorn\Documents\DiskMap
.\scripts\build.ps1
pwsh -File .\scripts\verify-against-wiztree.ps1 -Target 'C:\Windows'
```
