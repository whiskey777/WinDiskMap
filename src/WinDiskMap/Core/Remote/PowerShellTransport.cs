using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace WinDiskMap.Core.Remote
{
    /// <summary>
    /// Транспорт удалённого скана через PowerShell Remoting (WinRM/WSMan).
    ///
    /// Схема (без установки чего-либо на удалённый хост):
    ///   1. Две New-PSSession на host: одна под агента, вторая под чтение файла
    ///      (одновременно одна сессия не может выполнять два Invoke-Command —
    ///      вторая задача получает "session availability is Busy");
    ///   2. Copy-Item -ToSession — копирование того же exe (net48) в temp хоста;
    ///   3. Invoke-Command -AsJob — запуск exe в режиме --agent, stdout в файл;
    ///   4. через вторую сессию файл читается порциями (UTF-8), пока агент пишет;
    ///   5. очистка: удаление exe и файла, закрытие обеих сессий.
    ///
    /// Почему через файл, а не через stdout сессии: Invoke-Command возвращает
    /// десериализованные строки, которые PowerShell перекодирует по кодовой странице
    /// консоли — Unicode-имена файлов пострадали бы. Файл + чтение байтами в UTF-8
    /// сохраняют имена точно.
    ///
    /// Локально запускается pwsh/powershell.exe как дочерний процесс; требуется
    /// доступный WinRM и права на удалённом хосте.
    /// </summary>
    internal sealed class PowerShellTransport : IAgentTransport
    {
        private readonly string _host;
        private readonly string _credentialUser;
        private readonly string _credentialPassword;

        public PowerShellTransport(string host, string user, string password)
        {
            _host = host;
            _credentialUser = user;
            _credentialPassword = password;
        }

        public int StreamAgent(string remoteTarget, bool noMft, Action<string> onLine)
        {
            return StreamAgent(DefaultRemoteExePath(), remoteTarget, noMft, onLine);
        }

        /// <summary>
        /// Запускает агента на удалённом хосте и вызывает onLine для каждой строки
        /// NDJSON по мере поступления. Возвращает код, видимый с хоста.
        /// </summary>
        public int StreamAgent(string remoteExeName, string remoteTarget, bool noMft,
            Action<string> onLine)
        {
            string remoteOut = "WinDiskMap-agent-out-" + Guid.NewGuid().ToString("N") + ".ndjson";
            string ps = BuildScript(remoteExeName, remoteTarget, noMft, remoteOut);
            AppLog.Write("INFO", "remote ps-script:\n" + ps);

            // Скрипт — во временный .ps1 и запускаем через -File: длинная команда с
            // кавычками и юникодом не переживёт командную строку, а файл переживёт.
            string scriptPath = Path.Combine(Path.GetTempPath(),
                "WinDiskMap-ps-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(scriptPath, ps, new UTF8Encoding(true));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ShellExe(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -InputFormat None -OutputFormat Text -File "
                                + "\"" + scriptPath + "\""
                };

                using (var proc = new Process { StartInfo = psi })
                {
                    var stderr = new StringBuilder();
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                    proc.Start();
                    proc.BeginErrorReadLine();

                    // Строки агента приходят с префиксом OUT\t; остальное — служебное.
                    string line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        if (line.StartsWith("OUT\t", StringComparison.Ordinal))
                        {
                            onLine(line.Substring(4));
                        }
                        else if (line.StartsWith("ERR\t", StringComparison.Ordinal))
                        {
                            throw new IOException(Loc.T("Ошибка на удалённом хосте: ",
                                                        "Error on remote host: ")
                                                  + line.Substring(4));
                        }
                    }
                    proc.WaitForExit();

                    if (proc.ExitCode != 0)
                    {
                        string detail = stderr.Length > 0 ? stderr.ToString().Trim() : "";
                        throw new IOException(Loc.T("WinRM-сессия завершилась с кодом ",
                                                    "WinRM session exited with code ")
                                              + proc.ExitCode
                                              + (detail.Length > 0 ? ": " + detail : ""));
                    }
                    return proc.ExitCode;
                }
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }

        /// <summary>
        /// PowerShell-скрипт, целиком выполняющийся на локальной машине: он создаёт
        /// две сессии к удалённому хосту, копирует exe, запускает агента и читает его
        /// stdout-файл построчно через вторую сессию, печатая строки с префиксом OUT\t.
        ///
        /// TEMP удалённого хоста запрашивается у него же и передаётся явными
        /// путями во все удалённые блоки: локальный $env:TEMP может отличаться
        /// (другой профиль / короткое имя), а «%TEMP%» не раскрывается в
        /// параметрах Start-Process/Copy-Item.
        /// Все ошибки конвертируются в одну строку ERR\t&lt;текст&gt;: хост покажет её
        /// пользователю вместо простыни PowerShell.
        /// </summary>
        private string BuildScript(string remoteExeName, string remoteTarget, bool noMft, string remoteOut)
        {
            string credSetup = "";
            string sessionParams = "-ComputerName '" + Escape(_host) + "'";
            if (!string.IsNullOrEmpty(_credentialUser))
            {
                credSetup = "$sec = ConvertTo-SecureString " + QuotePs(_credentialPassword)
                          + " -AsPlainText -Force; "
                          + "$cred = New-Object System.Management.Automation.PSCredential("
                          + QuotePs(_credentialUser) + ", $sec); ";
                sessionParams += " -Credential $cred";
            }

            // CommandLine для CreateProcess: кавычки только двойные, одиночные
            // не являются разделителями и попали бы в путь как символы.
            string agentArgs = "--agent --path " + QuoteWinArg(remoteTarget)
                               + (noMft ? " --no-mft" : "");

            // Удалённый скрипт: запустить агента, дождаться конца и записать код
            // в отдельный файл. `exit` внутри -AsJob не возвращает код надёжно,
            // поэтому код читается через вторую сессию. stderr агента тоже
            // сохраняется: без него ошибка видна только как "exit code 2".
            string remoteScript =
                "param($exe, $out, $err, $rcfile, $agentArgs) " +
                "$ErrorActionPreference='Stop'; " +
                "if (Test-Path $out) { Remove-Item $out -Force }; " +
                "if (Test-Path $err) { Remove-Item $err -Force }; " +
                "if (Test-Path $rcfile) { Remove-Item $rcfile -Force }; " +
                "$p = Start-Process -FilePath $exe " +
                "-ArgumentList $agentArgs " +
                // -NoNewWindow в неинтерактивной сессии WinRM даёт
                // "Access is denied": не к какому консольному окну присоединиться.
                // При RedirectStandardOutput PowerShell не использует ShellExecute.
                "-RedirectStandardOutput $out -RedirectStandardError $err " +
                "-PassThru; " +
                "$p.WaitForExit(); " +
                "[IO.File]::WriteAllText($rcfile, [string]$p.ExitCode)";

            var sb = new StringBuilder();
            sb.Append("$ErrorActionPreference='Stop';");
            sb.Append("try {");
            if (credSetup.Length > 0) sb.Append(credSetup);

            // 1. Две сессии: $s занята фоновой задачей агента, $poll читает её
            //    stdout-файл. Повторный Invoke-Command в занятую сессию даёт
            //    "session availability is Busy".
            sb.Append("$s = New-PSSession ").Append(sessionParams).Append(";");
            sb.Append("$poll = New-PSSession ").Append(sessionParams).Append(";");

            // 2. TEMP удалённого хоста узнаём у него же. `$env:TEMP` в Copy-Item
            //    раскрылся бы локально и ушёл на хост как чужой путь.
            sb.Append("$remoteTemp = Invoke-Command -Session $s -ScriptBlock { $env:TEMP };");
            sb.Append("if (-not $remoteTemp) { throw 'Remote TEMP is empty' };");
            sb.Append("Invoke-Command -Session $s -ScriptBlock { param($d) if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null } } -ArgumentList $remoteTemp;");
            sb.Append("$remoteExe = Join-Path $remoteTemp '").Append(EscapeSingle(remoteExeName)).Append("';");
            sb.Append("$remoteOut = Join-Path $remoteTemp '").Append(EscapeSingle(remoteOut)).Append("';");
            sb.Append("$remoteErr = Join-Path $remoteTemp '").Append(EscapeSingle(remoteOut + ".err")).Append("';");
            sb.Append("$remoteRc = Join-Path $remoteTemp '").Append(EscapeSingle(remoteOut + ".rc")).Append("';");

            // 3. Копирование агента и запуск в фоне на удалённом хосте.
            sb.Append("$localExe = '").Append(EscapeSingle(LocalExePath())).Append("';");
            sb.Append("Copy-Item -Path $localExe -Destination $remoteExe -ToSession $s -Force;");
            sb.Append("$job = Invoke-Command -Session $s -ScriptBlock {")
              .Append(remoteScript)
              .Append("} -ArgumentList $remoteExe, $remoteOut, $remoteErr, $remoteRc, ")
              .Append(QuotePs(agentArgs)).Append(" -AsJob;");

            // 4. Читаем stdout-файл порциями через вторую сессию. Порция ограничена
            //    256 КБ: иначе один Invoke-Command может вернуть десятки мегабайт
            //    и застрять на base64/десериализации, а TUI получит данные рывками.
            //    Декодер с состоянием сохраняет multibyte-имена, разорванные
            //    на границе порций; строки извлекаются без ToString() всего буфера
            //    на каждую строку (было O(n^2) на большой пачке NDJSON).
            sb.Append("$pos = 0;");
            sb.Append("$pending = '';");
            sb.Append("$decoder = [Text.Encoding]::UTF8.GetDecoder();");
            sb.Append("while ($true) {");
            sb.Append("  $chunk = Invoke-Command -Session $poll -ScriptBlock {");
            sb.Append("      param($f, $p)");
            sb.Append("      if (-not (Test-Path $f)) { return ,@('','',$p) }");
            sb.Append("      $fs = [IO.File]::Open($f,'Open','Read','ReadWrite');");
            sb.Append("      try { if ($fs.Length -le $p) { return ,@('','',$p) }");
            sb.Append("            $fs.Position = $p;");
            sb.Append("            $len = [int][Math]::Min(($fs.Length - $p), 262144);");
            sb.Append("            $buf = New-Object byte[] $len;");
            sb.Append("            $n2 = $fs.Read($buf,0,$len);");
            sb.Append("            if ($n2 -le 0) { return ,@('','',$p) }");
            sb.Append("            return ,@([Convert]::ToBase64String($buf,0,$n2), ($p+$n2).ToString())");
            sb.Append("      } finally { $fs.Dispose() }");
            sb.Append("  } -ArgumentList $remoteOut, $pos;");
            sb.Append("  if ($null -eq $chunk) { Start-Sleep -Milliseconds 100; if ($job.State -ne 'Running') { break } continue }");
            sb.Append("  $b64 = $chunk[0];");
            sb.Append("  if ($b64 -and $b64.Length -gt 0) {");
            sb.Append("      try {");
            sb.Append("          $bytes = [Convert]::FromBase64String([string]$b64);");
            sb.Append("          $count = $decoder.GetCharCount($bytes, 0, $bytes.Length, $false);");
            sb.Append("          if ($count -gt 0) {");
            sb.Append("            $chars = New-Object char[] $count;");
            sb.Append("            $got = $decoder.GetChars($bytes, 0, $bytes.Length, $chars, 0, $false);");
            sb.Append("            if ($got -gt 0) { $pending += (-join $chars[0..($got-1)]) }");
            sb.Append("          }");
            sb.Append("          while (($i = $pending.IndexOf([char]10)) -ge 0) {");
            sb.Append("            $ln = $pending.Substring(0, $i).TrimEnd([char]13);");
            sb.Append("            $pending = $pending.Substring($i + 1);");
            sb.Append("            if ($ln) { Write-Output ('OUT' + [char]9 + $ln) }");
            sb.Append("          }");
            sb.Append("      } catch {}");
            sb.Append("  }");
            sb.Append("  try { $pos = [int]([string]$chunk[1]) } catch { $pos = $pos }");
            sb.Append("  if ($job.State -ne 'Running') { break }");
            sb.Append("  Start-Sleep -Milliseconds 25;");
            sb.Append("}");

            // 5. Дожидаемся задачи и вычитываем хвост: агент мог дописать данные
            //    между последним опросом и выходом. Потеря последней строки
            //    означала бы неполное дерево без всякой ошибки.
            sb.Append("try { Wait-Job $job | Out-Null } catch {}");
            sb.Append("while ($true) {");
            sb.Append("  $chunk = Invoke-Command -Session $poll -ScriptBlock {");
            sb.Append("      param($f, $p)");
            sb.Append("      if (-not (Test-Path $f)) { return ,@('','',$p) }");
            sb.Append("      $fs = [IO.File]::Open($f,'Open','Read','ReadWrite');");
            sb.Append("      try { if ($fs.Length -le $p) { return ,@('','',$p) }");
            sb.Append("            $fs.Position = $p;");
            sb.Append("            $len = [int][Math]::Min(($fs.Length - $p), 262144);");
            sb.Append("            $buf = New-Object byte[] $len;");
            sb.Append("            $n2 = $fs.Read($buf,0,$len);");
            sb.Append("            if ($n2 -le 0) { return ,@('','',$p) }");
            sb.Append("            return ,@([Convert]::ToBase64String($buf,0,$n2), ($p+$n2).ToString())");
            sb.Append("      } finally { $fs.Dispose() }");
            sb.Append("  } -ArgumentList $remoteOut, $pos;");
            sb.Append("  if ($null -eq $chunk) { break }");
            sb.Append("  $b64 = $chunk[0];");
            sb.Append("  if (-not $b64 -or $b64.Length -eq 0) { break }");
            sb.Append("  try {");
            sb.Append("    $bytes = [Convert]::FromBase64String([string]$b64);");
            sb.Append("    $count = $decoder.GetCharCount($bytes, 0, $bytes.Length, $false);");
            sb.Append("    if ($count -gt 0) {");
            sb.Append("      $chars = New-Object char[] $count;");
            sb.Append("      $got = $decoder.GetChars($bytes, 0, $bytes.Length, $chars, 0, $false);");
            sb.Append("      if ($got -gt 0) { $pending += (-join $chars[0..($got-1)]) }");
            sb.Append("    }");
            sb.Append("    while (($i = $pending.IndexOf([char]10)) -ge 0) {");
            sb.Append("      $ln = $pending.Substring(0, $i).TrimEnd([char]13);");
            sb.Append("      $pending = $pending.Substring($i + 1);");
            sb.Append("      if ($ln) { Write-Output ('OUT' + [char]9 + $ln) }");
            sb.Append("    }");
            sb.Append("    try { $pos = [int]([string]$chunk[1]) } catch {}");
            sb.Append("  } catch { break }");
            sb.Append("}");
            sb.Append("if ($pending.Length -gt 0) { $ln = $pending.TrimEnd([char]13,[char]10); if ($ln) { Write-Output ('OUT' + [char]9 + $ln) } }");
            sb.Append("$rcText = '';");
            sb.Append("try { $rcText = [string](Invoke-Command -Session $poll -ScriptBlock { param($f) if (Test-Path $f) { [IO.File]::ReadAllText($f) } else { '' } } -ArgumentList $remoteRc) } catch {}");
            sb.Append("$rc = 0;");
            sb.Append("try { if ($rcText) { $rc = [int]$rcText } } catch { $rc = 2 };");

            // Причину failures отдаём одной строкой ERR: иначе пользователь
            // видит только «exit code 2» без текста ошибки агента.
            sb.Append("$agentErr = '';");
            sb.Append("try { $agentErr = [string](Invoke-Command -Session $poll -ScriptBlock { param($f) if (Test-Path $f) { [IO.File]::ReadAllText($f) } else { '' } } -ArgumentList $remoteErr) } catch {}");
            sb.Append("$jobErr = '';");
            sb.Append("try { $jobErr = [string](($job.ChildJobs | ForEach-Object { $_.JobStateInfo.Reason } | Where-Object { $_ } | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.FullyQualifiedErrorId + ': ' + $_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage } else { $_.ToString() } }) -join '; ') } catch {}");
            sb.Append("if ($job.State -ne 'Completed' -or $rc -ne 0) {");
            sb.Append("  $parts = @();");
            sb.Append("  if ($jobErr) { $parts += ('job: ' + $jobErr.Trim()) };");
            sb.Append("  if ($agentErr) { $parts += ('agent stderr: ' + $agentErr.Trim()) };");
            sb.Append("  if ($parts.Count -eq 0) { $parts += ('job state=' + $job.State + '; agent exit=' + $rc) };");
            sb.Append("  Write-Output ('ERR' + [char]9 + ($parts -join ' | '));");
            sb.Append("}");

            // 6. Очистка (не мешает результату, если что-то не удалилось).
            //    Удалённому блоку передаём уже полные пути — не его $env:TEMP.
            sb.Append("try { Invoke-Command -Session $poll -ScriptBlock { param($e,$n,$r,$c) Remove-Item $e -Force -ErrorAction SilentlyContinue; Remove-Item $n -Force -ErrorAction SilentlyContinue; Remove-Item $r -Force -ErrorAction SilentlyContinue; Remove-Item $c -Force -ErrorAction SilentlyContinue } -ArgumentList $remoteExe, $remoteOut, $remoteErr, $remoteRc | Out-Null } catch {}");
            sb.Append("Remove-PSSession $s, $poll -ErrorAction SilentlyContinue;");
            sb.Append("exit $rc;");

            // Единая точка ошибок: любая ошибка превращается в одну строку ERR\t.
            sb.Append("} catch {");
            sb.Append("  $m = $_.Exception.Message;");
            sb.Append("  if ($_.Exception.InnerException) { $m = $m + ' / ' + $_.Exception.InnerException.Message }");
            sb.Append("  Write-Output ('ERR' + [char]9 + $m);");
            sb.Append("  if ($s) { Remove-PSSession $s -ErrorAction SilentlyContinue }");
            sb.Append("  if ($poll) { Remove-PSSession $poll -ErrorAction SilentlyContinue }");
            sb.Append("  exit 2;");
            sb.Append("}");

            return sb.ToString();
        }

        /// <summary>Путь к тому же бинарнику для копирования на удалённый хост (net48 exe).</summary>
        private static string LocalExePath()
        {
            string cwd = Directory.GetCurrentDirectory();
            string p1 = Path.GetFullPath(Path.Combine(cwd, "src", "WinDiskMap", "bin", "Release", "net48", "WinDiskMap.exe"));
            if (File.Exists(p1)) return p1;
            string p2 = Path.GetFullPath(Path.Combine(cwd, "..", "src", "WinDiskMap", "bin", "Release", "net48", "WinDiskMap.exe"));
            if (File.Exists(p2)) return p2;
            // fallback
            return RemoteScanRunner.LocalExeForAgent();
        }

        public static string DefaultRemoteExePath()
        {
            // Имя файла агента; полный путь строится из TEMP удалённого хоста.
            return "WinDiskMap-agent.exe";
        }

        /// <summary>Оболочка PowerShell: встроенный powershell.exe (есть на любой Windows).</summary>
        private static string ShellExe()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string bundled = Path.Combine(win, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(bundled)) return bundled;
            return "powershell.exe";
        }

        private static string Escape(string s) { return s == null ? "" : s.Replace("'", "''"); }
        private static string EscapeSingle(string s) { return s == null ? "" : s.Replace("'", "''"); }

        private static string QuotePs(string s)
        {
            return "'" + EscapeSingle(s) + "'";
        }

        /// <summary>Квотирование аргумента для CreateProcess (только двойные кавычки).</summary>
        private static string QuoteWinArg(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOf(' ') < 0 && s.IndexOf('\t') < 0 && s.IndexOf('"') < 0) return s;
            var q = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    q.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                if (slashes > 0) { q.Append('\\', slashes); slashes = 0; }
                q.Append(c);
            }
            if (slashes > 0) q.Append('\\', slashes * 2);
            return q.Append('"').ToString();
        }

        private static string EscapeBlock(string s)
        {
            return s; // блок передаётся как есть; кавычки внутри уже экранированы
        }
    }
}
