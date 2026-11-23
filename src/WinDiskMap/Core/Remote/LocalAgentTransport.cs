using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinDiskMap.Core.Remote
{
    /// <summary>
    /// Транспорт «агент как локальный дочерний процесс». Не требует WinRM: запускает
    /// тот же exe с --agent и читает его stdout. Нужен для отладки сквозного пути
    /// (NDJSON → дерево → CSV) и как самостоятельный режим: сканировать другой каталог
    /// ЛОКАЛЬНОГО хоста, но через точно тот же протокол, что и удалённый скан.
    /// </summary>
    internal sealed class LocalAgentTransport : IAgentTransport
    {
        private readonly string _exePath;

        public LocalAgentTransport(string exePath)
        {
            _exePath = exePath;
        }

        public int StreamAgent(string target, bool noMft, Action<string> onLine)
        {
            string args = "--agent --path \"" + target + "\"" + (noMft ? " --no-mft" : "");

            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            using (var proc = new Process { StartInfo = psi })
            {
                var stderr = new StringBuilder();
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.Start();
                proc.BeginErrorReadLine();

                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length > 0) onLine(line);
                }
                proc.WaitForExit();

                if (proc.ExitCode != 0 && stderr.Length > 0)
                    throw new IOException(stderr.ToString().Trim());

                return proc.ExitCode;
            }
        }
    }
}
