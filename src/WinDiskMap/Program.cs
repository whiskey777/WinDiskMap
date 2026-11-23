using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using WinDiskMap.Core;
using WinDiskMap.Core.Mft;
using WinDiskMap.Core.Remote;
using WinDiskMap.Tui;

namespace WinDiskMap
{
    internal static class Program
    {
        private static readonly string UsageRu =
@"WinDiskMap — анализ занятого места на диске (аналог WizTree/TreeSize в консоли)

Использование:
  WinDiskMap <путь> [опции]

Опции:
  --csv <файл>       экспорт в CSV (без TUI)
  --headless         без TUI: построчный прогресс и итоговый отчёт в stdout
  --log <файл>       журнал с таймстемпами (фазы, счётчики, ошибки) для диагностики
  --details          в режиме --only dirs добавить в CSV строки каждого файла
  --depth <N>        ограничить глубину строк CSV (0 — только корень)
  --only <режим>     dirs (по умолчанию) | files | tmp | dumps
  --no-mft           отключить MFT-режим (обычный обход каталогов)
  --debug-mft <маск> дамп атрибутов записей MFT с такими именами в --log (через ';')
  --lang <ru|en>     язык интерфейса (по умолчанию — язык ОС)
  --remote <хост>    сканировать каталог на удалённом хосте через WinRM
  --remote-user <пользователь>   учётные данные WinRM (если нужны явные)
  --remote-pass <пароль>         пароль WinRM (с --remote-user)
  --agent            служебный режим агента (NDJSON в stdout); запускается хостом
  --agent-local      отладка: скан через локальный процесс-агент (без WinRM)
  --help             эта справка

Примеры:
  WinDiskMap C:\Users                     TUI по каталогу (дерево растёт по мере скана)
  WinDiskMap C:\ --csv big.csv --depth 2  CSV: агрегаты папок первых двух уровней
  WinDiskMap C:\ --headless --log m.log   скан без TUI + журнал для диагностики
  WinDiskMap C:\ --csv d.csv --only dumps CSV: только дампы
  WinDiskMap \\server\share --headless    сетевая шара: обход каталогов (MFT недоступен)
  WinDiskMap --remote hostB --path C:\ --csv h.csv   удалённый скан C:\ на hostB (WinRM)

TUI (открывается сразу, дерево пополняется по мере сканирования):
  ↑↓ навигация   ← свернуть/к родителю   → развернуть   Enter открыть файл/переключить папку
  f фильтр (папки / папки+файлы / tmp / дампы)   s сортировка
  e экспорт дерева в CSV   Del удалить (с подтверждением)   колесо мыши — прокрутка   q выход
  окно перерисовывается автоматически при изменении размера консоли

MFT-режим (прямое чтение $MFT, скорость WizTree) требует запуска от администратора
и работает только с локального тома NTFS. Без админа, для сетевых (UNC) путей и
не-NTFS работает обычный обход каталогов — медленнее, но результат тот же.
Для сетевой шары нужны доступные учётные данные (при необходимости — net use).

Коды выхода: 0 — ок, 1 — ошибка аргументов, 2 — ошибка сканирования, 3 — нет терминала.";

        private static readonly string UsageEn =
@"WinDiskMap — console disk space analyzer (WizTree/TreeSize-like, no GUI)

Usage:
  WinDiskMap <path> [options]

Options:
  --csv <file>       export to CSV (no TUI)
  --headless         no TUI: line-based progress and final report to stdout
  --log <file>       timestamped log (phases, counters, errors) for troubleshooting
  --details          with --only dirs, add a row per file to the CSV
  --depth <N>        limit CSV row depth (0 — root only)
  --only <mode>      dirs (default) | files | tmp | dumps
  --no-mft           disable MFT mode (use directory walk)
  --debug-mft <mask> dump MFT attrs of records with such names to --log (';' separated)
  --lang <ru|en>     interface language (default — OS language)
  --remote <host>    scan a directory on a remote host via WinRM
  --remote-user <user>           explicit WinRM credentials (if needed)
  --remote-pass <password>       WinRM password (with --remote-user)
  --agent            agent service mode (NDJSON to stdout); started by the host
  --agent-local      debug: scan via a local agent process (no WinRM)
  --help             this help

Examples:
  WinDiskMap C:\Users                     TUI for a directory (tree fills during scan)
  WinDiskMap C:\ --csv big.csv --depth 2  CSV: folder aggregates, first two levels
  WinDiskMap C:\ --headless --log m.log   scan without TUI + troubleshooting log
  WinDiskMap C:\ --csv d.csv --only dumps CSV: dumps only
  WinDiskMap \\server\share --headless    network share: directory walk (no MFT)
  WinDiskMap --remote hostB --path C:\ --csv h.csv   remote scan of C:\ on hostB (WinRM)

TUI (opens immediately, the tree fills in as the scan progresses):
  ↑↓ navigate   ← collapse/parent   → expand   Enter open file/toggle folder
  f filter (folders / folders+files / tmp / dumps)   s sort
  e export tree to CSV   Del delete (with confirmation)   mouse wheel — scroll   q quit
  the window redraws automatically when the console is resized

MFT mode (direct $MFT read, WizTree-like speed) requires administrator rights and
works only on a local NTFS volume. Without admin, for network (UNC) paths and
non-NTFS a plain directory walk is used — slower, same results.
Network shares need usable credentials (use net use if necessary).

Exit codes: 0 — ok, 1 — bad arguments, 2 — scan error, 3 — no terminal.";

        private static int Main(string[] args)
        {
            // Язык определяем до первого вывода: русский только при русском языке ОС.
            Loc.Init(FindLang(args));

            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }

            Console.CancelKeyPress += OnCancelKeyPress;

            string path = null;
            string csvPath = null;
            string lang = null;
            string logPath = null;
            string remoteHost = null;
            string remoteUser = null;
            string remotePass = null;
            bool agent = false;
            bool agentLocal = false;
            bool details = false;
            bool noMft = false;
            bool headless = false;
            int maxDepth = int.MaxValue;
            OnlyMode only = OnlyMode.Dirs;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        Console.WriteLine(Loc.Ru ? UsageRu : UsageEn);
                        return 0;

                    case "--csv":
                        if (++i >= args.Length) return Fail(Loc.T("--csv требует путь к файлу", "--csv requires a file path"));
                        csvPath = args[i];
                        break;

                    case "--headless":
                        headless = true;
                        break;

                    case "--log":
                        if (++i >= args.Length) return Fail(Loc.T("--log требует путь к файлу", "--log requires a file path"));
                        logPath = args[i];
                        break;

                    case "--details":
                        details = true;
                        break;

                    case "--no-mft":
                        noMft = true;
                        break;

                    case "--agent":
                        agent = true;
                        break;

                    case "--agent-local":
                        agentLocal = true;
                        break;

                    case "--path":
                        if (++i >= args.Length)
                            return Fail(Loc.T("--path требует путь", "--path requires a path"));
                        if (path != null)
                            return Fail(Loc.T("Путь указан дважды", "Path specified twice"));
                        path = args[i];
                        break;

                    case "--remote":
                        if (++i >= args.Length)
                            return Fail(Loc.T("--remote требует имя хоста", "--remote requires a host name"));
                        remoteHost = args[i];
                        break;

                    case "--remote-user":
                        if (++i >= args.Length)
                            return Fail(Loc.T("--remote-user требует имя пользователя",
                                              "--remote-user requires a user name"));
                        remoteUser = args[i];
                        break;

                    case "--remote-pass":
                        if (++i >= args.Length)
                            return Fail(Loc.T("--remote-pass требует пароль", "--remote-pass requires a password"));
                        remotePass = args[i];
                        break;

                    case "--debug-mft":
                        if (++i >= args.Length)
                            return Fail(Loc.T("--debug-mft требует подстроку имени", "--debug-mft requires a name substring"));
                        MftScanner.DebugMask = args[i];
                        break;

                    case "--lang":
                        if (++i >= args.Length) return Fail(Loc.T("--lang требует ru|en", "--lang requires ru|en"));
                        lang = args[i].ToLowerInvariant();
                        if (lang != "ru" && lang != "en")
                            return Fail(Loc.T("Неизвестный язык: ", "Unknown language: ") + args[i]);
                        break;

                    case "--depth":
                        if (++i >= args.Length || !int.TryParse(args[i], out maxDepth) || maxDepth < 0)
                            return Fail(Loc.T("--depth требует неотрицательное число",
                                              "--depth requires a non-negative number"));
                        break;

                    case "--only":
                        if (++i >= args.Length)
                            return Fail(Loc.T("--only требует режим: dirs|files|tmp|dumps",
                                              "--only requires a mode: dirs|files|tmp|dumps"));
                        switch (args[i].ToLowerInvariant())
                        {
                            case "dirs": only = OnlyMode.Dirs; break;
                            case "files": only = OnlyMode.Files; break;
                            case "tmp": only = OnlyMode.Tmp; break;
                            case "dumps": only = OnlyMode.Dumps; break;
                            default:
                                return Fail(Loc.T("Неизвестный режим --only: ", "Unknown --only mode: ") + args[i]);
                        }
                        break;

                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal) || a.StartsWith("-", StringComparison.Ordinal))
                            return Fail(Loc.T("Неизвестная опция: ", "Unknown option: ") + a);
                        if (path != null)
                            return Fail(Loc.T("Указано несколько путей: ", "Multiple paths specified: ")
                                        + path + " " + Loc.T("и", "and") + " " + a);
                        path = a;
                        break;
                }
            }

            if (lang != null) Loc.Init(lang);

            // Роль «агент» (запускается на удалённом хосте): только NDJSON в stdout,
            // без TUI/CSV/логов — вывод не должен быть замусорен.
            if (agent)
            {
                if (path == null)
                {
                    Console.Error.WriteLine("agent: --path is required");
                    return 1;
                }
                return AgentRunner.Run(path, noMft);
            }

            if (path == null)
            {
                Console.WriteLine(Loc.Ru ? UsageRu : UsageEn);
                return 1;
            }

            if (logPath != null)
            {
                AppLog.Init(logPath);
                AppLog.Write("INFO", "WinDiskMap; args=" + string.Join(" ", args)
                                     + "; lang=" + (Loc.Ru ? "ru" : "en")
                                     + "; cwd=" + Directory.GetCurrentDirectory());
                AppLog.Write("INFO", "admin=" + ScanRunner.IsAdministrator()
                                     + "; headless=" + headless + "; noMft=" + noMft
                                     + "; remote=" + (remoteHost ?? "-"));
            }

            try
            {
                int code = Run(path, csvPath, details, noMft, maxDepth, only, headless,
                               remoteHost, remoteUser, remotePass, agentLocal);
                AppLog.Write("INFO", "exit=" + code);
                return code;
            }
            catch (Exception ex)
            {
                AppLog.Write("ERROR", ex.ToString());
                Console.Error.WriteLine(Loc.T("Ошибка: ", "Error: ") + ex.Message);
                return 2;
            }
            finally
            {
                AppLog.Close();
            }
        }

        private static string FindLang(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i].ToLowerInvariant() == "--lang") return args[i + 1].ToLowerInvariant();
            return null;
        }

        private static int Run(string path, string csvPath, bool details, bool noMft,
            int maxDepth, OnlyMode only, bool headless,
            string remoteHost, string remoteUser, string remotePass, bool agentLocal)
        {
            if (!headless) Vt.Enable();

            var session = new ScanSession { DisplayPath = path };
            Thread scanThread;
            if (remoteHost != null)
            {
                scanThread = new Thread(() => RemoteScanRunner.Run(
                    session, remoteHost, remoteUser, remotePass, noMft))
                {
                    IsBackground = true,
                    Name = "WinDiskMap-remote"
                };
            }
            else if (agentLocal)
            {
                scanThread = new Thread(() => RemoteScanRunner.RunLocalAgent(session, noMft))
                {
                    IsBackground = true,
                    Name = "WinDiskMap-agent-local"
                };
            }
            else
            {
                scanThread = new Thread(() => ScanRunner.Run(session, noMft))
                {
                    IsBackground = true,
                    Name = "WinDiskMap-scan"
                };
            }
            scanThread.Start();
            StartLogger(session);

            if (csvPath != null || headless)
                return RunReport(session, csvPath, details, maxDepth, only, headless);

            return RunTui(session);
        }

        /// <summary>Фоновый журнал: периодические счётчики + итог скана (когда включён --log).</summary>
        private static void StartLogger(ScanSession session)
        {
            if (!AppLog.Enabled) return;
            var th = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                long lastSec = -999;
                while (!session.Done)
                {
                    long sec = (long)sw.Elapsed.TotalSeconds;
                    if (sec - lastSec >= 10)
                    {
                        ScanProgress p = session.Progress;
                        AppLog.Write("INFO", "progress: files=" + p.Files
                                             + " dirs=" + p.Dirs + " bytes=" + p.Bytes);
                        lastSec = sec;
                    }
                    Thread.Sleep(250);
                }
                ScanProgress done = session.Progress;
                AppLog.Write("INFO", "scan done: files=" + done.Files
                                     + " dirs=" + done.Dirs + " bytes=" + done.Bytes
                                     + " mode=" + session.Mode);
                if (session.Error != null) AppLog.Write("ERROR", session.Error);
            })
            {
                IsBackground = true,
                Name = "WinDiskMap-logger"
            };
            th.Start();
        }

        private static int RunTui(ScanSession session)
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(Loc.T(
                    "Для TUI нужен реальный терминал. Используйте --csv/--headless для вывода в файл.",
                    "TUI requires a real terminal. Use --csv/--headless to export to a file."));
                return 3;
            }

            new TuiApp(session).Run();

            // Чистый выход: стираем экран TUI, никаких остатков футера/статусов.
            Console.Write("\x1b[2J\x1b[H");

            if (session.Error != null)
            {
                Console.Error.WriteLine(Loc.T("Ошибка сканирования: ", "Scan error: ") + session.Error);
                return 2;
            }
            if (!string.IsNullOrEmpty(session.Progress.Note))
            {
                Console.Error.WriteLine(Loc.T("Примечание: ", "Note: ") + session.Progress.Note);
            }
            return 0;
        }

        /// <summary>
        /// Скан + отчёт (+ CSV, если задан). headless — построчный прогресс вместо VT-экрана,
        /// работает при перенаправленном выводе (логи, CI).
        /// </summary>
        private static int RunReport(ScanSession session, string csvPath, bool details,
            int maxDepth, OnlyMode only, bool headless)
        {
            if (headless)
            {
                var sw = Stopwatch.StartNew();
                string lastPhase = "";
                long lastPrint = -999;
                while (!session.Done)
                {
                    ScanProgress p = session.Progress;
                    long sec = (long)sw.Elapsed.TotalSeconds;
                    if (p.Phase != lastPhase || sec - lastPrint >= 5)
                    {
                        Console.WriteLine("[{0}] {1} │ {2}: {3} │ {4}: {5} │ {6}: {7}",
                            sw.Elapsed.ToString(@"hh\:mm\:ss"),
                            p.Phase,
                            Loc.T("Файлов", "Files"), Format.Number(p.Files),
                            Loc.T("Папок", "Folders"), Format.Number(p.Dirs),
                            Loc.T("Объём", "Size"), Format.Bytes(p.Bytes));
                        lastPhase = p.Phase;
                        lastPrint = sec;
                    }
                    Thread.Sleep(400);
                }
            }
            else
            {
                Vt.Enable();
                int tick = 0;
                while (!session.Done)
                {
                    TuiApp.DrawProgress(session.Progress, tick++);
                    Thread.Sleep(120);
                }
                Console.Write("\x1b[2J\x1b[H");
            }

            if (session.Error != null)
            {
                Console.Error.WriteLine(Loc.T("Ошибка сканирования: ", "Scan error: ") + session.Error);
                return 2;
            }
            if (!string.IsNullOrEmpty(session.Progress.Note))
            {
                Console.Error.WriteLine(Loc.T("Примечание: ", "Note: ") + session.Progress.Note);
            }

            Node root = session.Root;
            PrintReport(session, root);

            if (csvPath != null)
            {
                var options = new CsvOptions
                {
                    OutputPath = csvPath,
                    Details = details,
                    MaxDepth = maxDepth,
                    Only = only
                };
                Console.WriteLine(Loc.T("Экспорт CSV: ", "CSV export: ") + csvPath
                                  + " (only=" + only + ", depth="
                                  + (maxDepth == int.MaxValue ? "∞" : maxDepth.ToString()) + ")");
                long rows = CsvExporter.Export(root, options);
                AppLog.Write("INFO", "csv: rows=" + rows + " path=" + csvPath);
                Console.WriteLine(Loc.T("Готово. Строк записано: ", "Done. Rows written: ") + rows.ToString("N0"));
            }
            return 0;
        }

        private static void PrintReport(ScanSession session, Node root)
        {
            long files = 0, dirs = 0, reparse = 0;
            CountTree(root, ref files, ref dirs, ref reparse);

            Console.WriteLine(Loc.T("Режим сканирования: ", "Scan mode: ") + session.Mode);
            Console.WriteLine(Loc.T("Корень:             ", "Root:             ") + root.FullPath);
            Console.WriteLine(Loc.T("Папок:              ", "Folders:          ") + Format.Number(dirs));
            Console.WriteLine(Loc.T("Файлов:             ", "Files:            ") + Format.Number(files));
            Console.WriteLine(Loc.T("Объём:              ", "Size:             ") + Format.Bytes(root.Size));
            if (reparse > 0)
                Console.WriteLine(Loc.T("в т.ч. junction/symlink (вне счёта папок): ",
                                        "including junction/symlink (excluded from folders): ")
                                  + Format.Number(reparse));
            Console.WriteLine();
            AppLog.Write("INFO", "report: mode=" + session.Mode + " root=" + root.FullPath
                                 + " dirs=" + dirs + " files=" + files + " bytes=" + root.Size
                                 + " reparseInTree=" + reparse);
        }

        private static void CountTree(Node node, ref long files, ref long dirs, ref long reparse)
        {
            // junction/symlink-папки вне счёта папок (как у WizTree), но учитываем
            // их число отдельно — диагностика совпадения счётчиков с WizTree.
            if (node.IsReparse) reparse++;
            else dirs++;
            files += node.FileCount;
            for (int i = 0; i < node.Dirs.Count; i++)
                CountTree(node.Dirs[i], ref files, ref dirs, ref reparse);
        }

        /// <summary>
        /// Ctrl+C: отменяем штатное «рубление» процесса, восстанавливаем цвета/курсор/мышь
        /// и выходим аккуратно — иначе консоль остаётся с серым шрифтом и спрятанным курсором.
        /// </summary>
        private static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            AppLog.Write("WARN", "Ctrl+C — аварийное завершение");
            try { Console.Write("\x1b[?1006l\x1b[?1000l\x1b[?25h\x1b[0m"); }
            catch { }
            try { Console.ResetColor(); }
            catch { }
            try { Console.WriteLine(); }
            catch { }
            Environment.Exit(130);
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(Loc.T("Ошибка: ", "Error: ") + message);
            Console.Error.WriteLine(Loc.T("Запустите с --help для справки.", "Run with --help for usage."));
            return 1;
        }
    }
}
