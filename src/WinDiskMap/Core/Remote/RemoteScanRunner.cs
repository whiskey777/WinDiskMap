using System;
using System.IO;

namespace WinDiskMap.Core.Remote
{
    /// <summary>
    /// Хост-сторона удалённого скана. Пользователь задаёт путь ЛОКАЛЬНО на удалённом
    /// хосте (--remote host --path C:\Windows); агент на хосте сканирует его как
    /// локальный том (MFT/обход), а здесь строится живое дерево.
    ///
    /// Отображаемый корень переводится в административную шару для узнаваемости:
    /// C:\Windows на host → \\host\C$\Windows. Если путь уже UNC — оставляется как есть.
    /// </summary>
    public static class RemoteScanRunner
    {
        public static void Run(ScanSession session, string host, string user, string password, bool noMft)
        {
            RunCore(session, host, noMft, new PowerShellTransport(host, user, password), true);
        }

        /// <summary>
        /// Работа через локальный дочерний процесса-агент (--agent-local): тот же
        /// протокол и тот же парсер, но без WinRM — для отладки и локальных нужд.
        /// </summary>
        public static void RunLocalAgent(ScanSession session, bool noMft)
        {
            RunCore(session, null, noMft, new LocalAgentTransport(LocalExeForAgent()), false);
        }

        private static void RunCore(ScanSession session, string host, bool noMft,
            IAgentTransport transport, bool remote)
        {
            try
            {
                string target = session.DisplayPath;
                if (string.IsNullOrEmpty(target))
                {
                    session.Error = Loc.T("Не задан путь к каталогу.",
                                          "No directory path given.");
                    return;
                }

                string displayRoot = remote ? ToDisplayRoot(host, target) : target;
                session.Mode = remote
                    ? Loc.T("Удалённый скан (WinRM): ", "Remote scan (WinRM): ") + host
                    : Loc.T("Агент (локальный процесс)", "Agent (local process)");
                session.Progress.Phase = Loc.T("Удалённое сканирование", "Remote scan");

                AppLog.Write("INFO", "agent: host=" + (host ?? "-") + " path=" + target
                                     + " display=" + displayRoot + " noMft=" + noMft);

                int rc = transport.StreamAgent(target, noMft, line =>
                {
                    RemoteScannerLine(session, displayRoot, line);
                });

                if (rc != 0 && session.Error == null)
                    session.Error = Loc.T("Агент завершился с кодом ", "Agent exited with code ") + rc;

                Flush(session, displayRoot);
            }
            catch (Exception ex)
            {
                session.Error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                session.Done = true;
            }
        }

        // --- Парсер потока: тот же TreeBuilder, но строку за строкой (живьём). ---

        [ThreadStatic] private static RemoteStreamState _state;

        private sealed class RemoteStreamState
        {
            public TreeBuilder Tree;
            public bool Began;
            public bool Ended;
            public string Notes;
            public string DisplayRoot;
        }

        private static void RemoteScannerLine(ScanSession session, string displayRoot, string line)
        {
            if (line.Length == 0) return;
            var st = _state;
            if (st == null)
            {
                st = new RemoteStreamState
                {
                    Tree = new TreeBuilder(session, session.Progress),
                    DisplayRoot = displayRoot
                };
                _state = st;
            }

            string t = NdJson.GetString(line, "t");
            if (t == null) return;

            switch (t)
            {
                case "meta":
                    session.Mode = NdJson.GetString(line, "mode") ?? session.Mode;
                    break;

                case "phase":
                    session.Progress.Phase = NdJson.GetString(line, "v") ?? session.Progress.Phase;
                    break;

                case "note":
                    {
                        string v = NdJson.GetString(line, "v");
                        if (!string.IsNullOrEmpty(v))
                            st.Notes = st.Notes != null ? st.Notes + "; " + v : v;
                    }
                    break;

                case "dir":
                    {
                        int id = NdJson.GetInt(line, "id");
                        int parent = NdJson.GetInt(line, "parent", -1);
                        if (parent < 0 && !st.Began)
                        {
                            st.Began = true;
                            st.Tree.Begin(displayRoot);
                            break; // корень агента → отображаемый корень хоста
                        }
                        st.Tree.Dir(id, parent, NdJson.GetString(line, "name"),
                                    NdJson.GetBool(line, "reparse"));
                    }
                    break;

                case "file":
                    st.Tree.File(NdJson.GetInt(line, "id"), NdJson.GetInt(line, "parent", -1),
                                 NdJson.GetString(line, "name"), NdJson.GetLong(line, "size"));
                    break;

                case "end":
                    st.Ended = true;
                    break;

                case "error":
                    throw new IOException(NdJson.GetString(line, "v") ?? "agent error");
            }
        }

        private static void Flush(ScanSession session, string displayRoot)
        {
            var st = _state;
            if (st == null) return;

            if (st.Notes != null)
                session.Progress.Note = Join(session.Progress.Note, st.Notes);

            if (!st.Ended && session.Error == null)
                session.Progress.Note = Join(session.Progress.Note, Loc.T(
                    "Агент завершился без финальной записи (поток оборван).",
                    "Agent ended without a final record (stream broken)."));

            _state = null;
        }

        /// <summary>C:\Windows на host → \\host\C$\Windows; UNC оставляем как есть.</summary>
        private static string ToDisplayRoot(string host, string path)        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return path;

            string full = path;
            try { full = Path.GetFullPath(path); } catch { }

            if (full.Length >= 2 && full[1] == ':')
            {
                char letter = char.ToUpperInvariant(full[0]);
                string rest = full.Substring(2).TrimStart('\\');
                return "\\\\" + host + "\\" + letter + "$" + (rest.Length > 0 ? "\\" + rest : "");
            }
            return "\\\\" + host + "\\" + full.TrimStart('\\');
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "; " + b;
        }

        /// <summary>
        /// Путь к бинарнику, который запускается как агент. Агент — тот же exe;
        /// предпочитаем net48 (self-contained, есть на любой Windows), иначе — сам процесс.
        /// </summary>
        public static string LocalExeForAgent()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;

            // net48-exe, net8-apphost и single-file: агент — сам процесс.
            // Assembly.Location здесь нельзя использовать: в single-file он пустой (IL3000).
            try
            {
                string proc = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(proc)
                    && string.Equals(Path.GetFileName(proc), "WinDiskMap.exe",
                                     StringComparison.OrdinalIgnoreCase)
                    && File.Exists(proc))
                    return proc;
            }
            catch { }

            string candidate = Path.Combine(dir, "WinDiskMap.exe");
            if (File.Exists(candidate)) return candidate;

            string net48 = Path.GetFullPath(Path.Combine(dir, "..", "..", "net48", "WinDiskMap.exe"));
            if (File.Exists(net48)) return net48;

            // Обычный запуск через `dotnet WinDiskMap.dll` — apphost может отсутствовать.
            string dll = Path.Combine(dir, "WinDiskMap.dll");
            return File.Exists(dll) ? dll : candidate;
        }
    }
}
