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
    ///   1. New-PSSession host  — сессия WSMan;
    ///   2. Copy-Item -ToSession — копирование того же exe (net48) в temp хоста;
    ///   3. Invoke-Command — запуск exe в режиме --agent, stdout в файл на хосте;
    ///   4. хост читает файл по сессии порциями (UTF-8), пока агент пишет, — живьём;
    ///   5. очистка: удаление exe и файла, закрытие сессии.
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
        /// сессию к удалённому хосту, копирует exe, запускает агента и читает его
        /// stdout-файл построчно, печатая строки с префиксом OUT\t.
        ///
        /// Пути на удалённом хосте формируются из имени файла и $env:TEMP — «%TEMP%»
        /// не раскрывается в параметрах Start-Process/Copy-Item.
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

            string agentArgs = "--agent --path " + QuotePs(remoteTarget) + (noMft ? " --no-mft" : "");

            // Удалённый скрипт: запустить агент, дождаться конца, отдать код.
            string remoteScript =
                "$ErrorActionPreference='Stop'; " +
                "$out = Join-Path $env:TEMP '" + EscapeSingle(remoteOut) + "'; " +
                "if (Test-Path $out) { Remove-Item $out -Force }; " +
                "$exe = Join-Path $env:TEMP '" + EscapeSingle(remoteExeName) + "'; " +
                "$p = Start-Process -FilePath $exe " +
                "-ArgumentList " + QuotePs(agentArgs) + " " +
                "-RedirectStandardOutput $out -NoNewWindow -PassThru; " +
                "$p.WaitForExit(); " +
                "exit $p.ExitCode";

            var sb = new StringBuilder();
            sb.Append("$ErrorActionPreference='Stop';");
            sb.Append("try {");
            if (credSetup.Length > 0) sb.Append(credSetup);

            // 1. Сессия.
            sb.Append("$s = New-PSSession ").Append(sessionParams).Append(";");

            // 2. Копирование агента в temp удалённого хоста (путь вычисляет он сам).
            sb.Append("$localExe = '").Append(EscapeSingle(LocalExePath())).Append("';");
            sb.Append("Copy-Item -Path $localExe -Destination (Join-Path $env:TEMP '")
              .Append(EscapeSingle(remoteExeName))
              .Append("') -ToSession $s -Force;");

            // 3. Запуск агента в фоне на удалённом хосте (ждём код отдельной задачей).
            sb.Append("$job = Invoke-Command -Session $s -ScriptBlock {")
              .Append(remoteScript)
              .Append("} -AsJob;");

            // 4. Читаем stdout-файл построчно по сессии, пока задача работает.
            sb.Append("$outName = '").Append(EscapeSingle(remoteOut)).Append("';");
            sb.Append("$pos = 0;");
            sb.Append("$sb = New-Object System.Text.StringBuilder;");
            sb.Append("while ($true) {");
            sb.Append("  $chunk = Invoke-Command -Session $s -ScriptBlock {");
            sb.Append("      param($n, $p) $f = Join-Path $env:TEMP $n;");
            sb.Append("      if (-not (Test-Path $f)) { return ,@('','',$p) }");
            sb.Append("      $fs = [IO.File]::Open($f,'Open','Read','ReadWrite');");
            sb.Append("      try { if ($fs.Length -le $p) { return ,@('','',$p) }");
            sb.Append("            $fs.Position = $p;");
            sb.Append("            $len = [int]($fs.Length - $p);");
            sb.Append("            $buf = New-Object byte[] $len;");
            sb.Append("            $n2 = $fs.Read($buf,0,$len);");
            sb.Append("            if ($n2 -le 0) { return ,@('','',$p) }");
            sb.Append("            return ,@([Convert]::ToBase64String($buf,0,$n2), ($p+$n2).ToString())");
            sb.Append("      } finally { $fs.Dispose() }");
            sb.Append("  } -ArgumentList $outName, $pos;");
            sb.Append("  if ($null -eq $chunk) { Start-Sleep -Milliseconds 200; if ($job.State -ne 'Running') { break } continue }");
            sb.Append("  $b64 = $chunk[0];");
            sb.Append("  if ($b64 -and $b64.Length -gt 0) {");
            sb.Append("      try {");
            sb.Append("          $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$b64));");
            sb.Append("          $sb.Append($text);");
            sb.Append("          while (($i = $sb.ToString().IndexOf([char]10)) -ge 0) {");
            sb.Append("              $ln = $sb.ToString(0, $i).TrimEnd([char]13);");
            sb.Append("              $sb.Remove(0, $i+1);");
            sb.Append("              if ($ln) { Write-Output ('OUT' + [char]9 + $ln) }");
            sb.Append("          }");
            sb.Append("      } catch {}");
            sb.Append("  }");
            sb.Append("  try { $pos = [int]([string]$chunk[1]) } catch { $pos = $pos }");
            sb.Append("  if ($job.State -ne 'Running') { break }");
            sb.Append("  Start-Sleep -Milliseconds 120;");
            sb.Append("}");

            // 5. Дожидаемся задачи, отдаём остаток буфера и код возврата.
            sb.Append("try { Wait-Job $job | Out-Null } catch {}");
            sb.Append("$rc = 0;");
            sb.Append("try { $rc = (Receive-Job $job -ErrorAction Stop | Select-Object -Last 1) } catch { $rc = 2 };");
            sb.Append("if ($sb.Length -gt 0) { $ln = $sb.ToString().TrimEnd([char]13,[char]10); if ($ln) { Write-Output ('OUT' + [char]9 + $ln) } }");

            // 6. Очистка (не мешает результату, если что-то не удалилось).
            sb.Append("try { Invoke-Command -Session $s -ScriptBlock { param($e,$n) Remove-Item (Join-Path $env:TEMP $e) -Force -ErrorAction SilentlyContinue; Remove-Item (Join-Path $env:TEMP $n) -Force -ErrorAction SilentlyContinue } -ArgumentList '")
              .Append(EscapeSingle(remoteExeName)).Append("','")
              .Append(EscapeSingle(remoteOut)).Append("' | Out-Null } catch {}");
            sb.Append("Remove-PSSession $s -ErrorAction SilentlyContinue;");
            sb.Append("exit $rc;");

            // Единая точка ошибок: любая ошибка превращается в одну строку ERR\t.
            sb.Append("} catch {");
            sb.Append("  $m = $_.Exception.Message;");
            sb.Append("  if ($_.Exception.InnerException) { $m = $m + ' / ' + $_.Exception.InnerException.Message }");
            sb.Append("  Write-Output ('ERR' + [char]9 + $m);");
            sb.Append("  if ($s) { Remove-PSSession $s -ErrorAction SilentlyContinue }");
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
            // Имя файла агента в temp удалённого хоста ($env:TEMP раскрывается на месте).
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

        private static string EscapeBlock(string s)
        {
            return s; // блок передаётся как есть; кавычки внутри уже экранированы
        }
    }
}
