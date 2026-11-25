# Настройка удалённого скана (WinRM)

Ниже — чеклист. Команды выполняются **от имени администратора** там, где это указано.
Транспорт использует обычный PowerShell Remoting (порт **5985** для HTTP), без установки
ПО на удалённом хосте: туда копируется только `net48\WinDiskMap.exe`.

## 1. Клиент (где запускаете WinDiskMap)

```powershell
# посмотреть текущие доверенные хосты
Get-Item WSMan:\localhost\Client\TrustedHosts

# добавить целевой хост (от имени администратора)
Set-Item WSMan:\localhost\Client\TrustedHosts -Value '10.10.1.107' -Force
# чтобы добавить, не затирая предыдущие:
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "$((Get-Item WSMan:\localhost\Client\TrustedHosts).Value),10.10.1.107" -Force
```

Требуется, если используете Basic/не-доменную аутентификацию. Для домена с Kerberos
TrustedHosts обычно не нужен.

Проверка связи:

```powershell
Test-WSMan 10.10.1.107
Test-NetConnection 10.10.1.107 -Port 5985
Enter-PSSession -ComputerName 10.10.1.107 -Credential (Get-Credential)
```

## 2. Удалённый хост

От имени администратора на хосте:

```powershell
Enable-PSRemoting -Force
Set-Service WinRM -StartupType Automatic
Start-Service WinRM

# для Basic + незашифрованного HTTP (только в доверенной сети!):
Set-Item WSMan:\localhost\Service\Auth\Basic -Value $true
Set-Item WSMan:\localhost\Service\AllowUnencrypted -Value $true
Restart-Service WinRM
```

Правило брандмауэра создаётся `Enable-PSRemoting`; при необходимости:

```powershell
Enable-NetFirewallRule -Name "WINRM-HTTP-In-TCP"
```

Если хост в домене, предпочтительнее Kerberos (Basic/AllowUnencrypted включать не надо),
но тогда на клиенте должно быть доверие домена и корректный SPN.

## 3. Права

- Для чтения `$MFT` агент должен запускаться под администратором хоста — MFT-ветка
  включается сама при `IsAdministrator()` + локальном NTFS.
- Без админа агент работает обходом каталогов и даёт те же цифры, просто медленнее.
- Для пути `C:\Windows` достаточно прав на чтение; в `%TEMP%` агента должны
  быть права на запись/удаление своего exe и выходного файла.

## 4. Запуск

```powershell
$exe = 'C:\Users\akorn\Documents\WinDiskMap\src\WinDiskMap\bin\Release\net8.0\WinDiskMap.exe'

# с неявными учётными данными текущего пользователя:
& $exe --remote 10.10.1.107 --path C:\Windows --headless --log remote.log

# с явными:
& $exe --remote 10.10.1.107 --remote-user 'user' --remote-pass 'pass' `
  --path C:\Windows --csv remote.csv
```

`--path` задаётся **локально для удалённого хоста** (`C:\Windows`, а не `\\host\C$\Windows`).
В дереве он покажется как `\\host\C$\Windows` — это только отображение.

## 5. Отладка без WinRM

Тот же протокол и тот же парсер, но агент — локальный дочерний процесс:

```powershell
& $exe --agent-local --path C:\Windows --csv agent.csv --headless --log agent.log
# сразу роли агента (NDJSON в stdout):
.\src\WinDiskMap\bin\Release\net48\WinDiskMap.exe --agent --path C:\Windows | Select-Object -First 5
```

Сравнение `agent.csv` с эталонным экспортом: [scripts/compare-reports.ps1](../scripts/compare-reports.ps1),
результаты предыдущих прогонов: [verification/](../verification/).

## 6. Статус проверки и ограничения

WinRM-режим проверен 25.11.2026 на удалённом хосте Windows Server 2016: агент запускается,
NDJSON доходит до хоста, скан `C:\` завершается в MFT- и fallback-режимах, дерево и CSV
строятся. Скорость невысокая — это текущее ограничение транспорта, а не признак ошибки.
Конкретный хост из старой заметки (`10.10.1.107`) мог остаться ненастроенным: проверьте
`TrustedHosts` и шаги раздела 2.

Если скан не стартует, снимите лог с `--log remote.log` (там виден сгенерированный
PowerShell-скрипт) и проверьте `[Console]::OutputEncoding` на клиенте. Для контрольного
прогона после настройки хоста используйте сравнение CSV пофайлово:
[scripts/compare-reports.ps1](../scripts/compare-reports.ps1).
