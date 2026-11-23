using System;
using System.IO;
using System.Security.Principal;
using System.Threading;

namespace DiskMap.Core
{
    /// <summary>Прогресс сканирования, читаемый из UI-потока.</summary>
    public sealed class ScanProgress
    {
        private string _phase = "";
        private string _note;
        private long _files;
        private long _dirs;
        private long _bytes;

        public string Phase
        {
            get { return Volatile.Read(ref _phase); }
            set
            {
                string v = value ?? "";
                Volatile.Write(ref _phase, v);
                AppLog.Write("PHASE", v);
            }
        }

        public string Note
        {
            get { return Volatile.Read(ref _note); }
            set
            {
                Volatile.Write(ref _note, value);
                if (!string.IsNullOrEmpty(value)) AppLog.Write("NOTE", value);
            }
        }

        public long Files { get { return Interlocked.Read(ref _files); } }
        public long Dirs { get { return Interlocked.Read(ref _dirs); } }
        public long Bytes { get { return Interlocked.Read(ref _bytes); } }

        public void AddFile(long size) { Interlocked.Increment(ref _files); Interlocked.Add(ref _bytes, size); }
        public void AddDir() { Interlocked.Increment(ref _dirs); }
    }

    /// <summary>
    /// Запуск сканирования в фоновом потоке: MFT-режим (дерево готово в конце, скан быстрый)
    /// или живой обход каталогов (корень появляется сразу, TUI пополняется по мере скана).
    /// Все исключения и причины фолбэков сохраняются в session (Error / Progress.Note).
    /// </summary>
    public static class ScanRunner
    {
        public static void Run(ScanSession session, bool forceManaged)
        {
            try
            {
                string targetPath = Path.GetFullPath(session.DisplayPath).TrimEnd('\\');
                session.DisplayPath = targetPath;

                if (!Directory.Exists(targetPath))
                {
                    session.Error = Loc.T("Каталог не найден: ", "Directory not found: ") + targetPath;
                    return;
                }

                string volumeRoot = Path.GetPathRoot(targetPath);
                bool admin = IsAdministrator();
                bool networkPath = IsNetworkPath(targetPath);
                bool localNtfs = !networkPath
                                 && volumeRoot != null
                                 && volumeRoot.Length >= 1
                                 && IsNtfs(volumeRoot);
                bool mftPossible = !forceManaged && localNtfs;

                // Сеть и локальный диск — разные истории: у шары нет $MFT, поэтому
                // MFT недоступен, и сообщения не должны валить это на «нет админа».
                if (networkPath)
                {
                    if (!CanReadDirectory(targetPath))
                    {
                        session.Error = Loc.T(
                            "Нет доступа к сетевому пути: ", "Cannot access network path: ")
                            + targetPath + " — "
                            + Loc.T("проверьте, что шара доступна и есть права (при необходимости выполните net use).",
                                    "check that the share is reachable and you have rights (use net use if needed).");
                        return;
                    }
                    session.Progress.Note = Loc.T(
                        "Сетевой путь (UNC) — MFT недоступен, обычный обход каталогов",
                        "Network path (UNC) — no MFT, using directory walk");
                }
                else if (mftPossible && admin)
                {
                    try
                    {
                        ScanIndex idx = Mft.MftScanner.Scan(volumeRoot, session.Progress);
                        idx.BuildChildMap();
                        int target = idx.FindDirectory(targetPath);
                        if (target < 0)
                            throw new DirectoryNotFoundException(
                                Loc.T("Каталог не найден на томе после разбора MFT: ",
                                      "Directory not found on volume after MFT parse: ") + targetPath
                                + " " + idx.DescribeLookupFailure(targetPath));

                        Node node = idx.BuildTree(target, targetPath);
                        session.Mode = Loc.T("MFT (прямое чтение $MFT)", "MFT (direct $MFT read)");
                        lock (session.Sync) session.Root = node;
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Диагностика обязана дойти до пользователя: тип + текст исключения.
                        string note = Loc.T("MFT-режим недоступен", "MFT mode unavailable")
                                      + " (" + ex.GetType().Name + "): " + ex.Message;
                        session.Progress.Note = session.Progress.Note != null
                            ? session.Progress.Note + "; " + note
                            : note;
                    }
                }
                else if (mftPossible && !admin)
                {
                    session.Progress.Note = Loc.T(
                        "Нет прав администратора — обычный обход каталогов",
                        "No administrator rights — using directory walk");
                }

                session.Mode = Loc.T("Обход каталогов", "Directory walk")
                               + (networkPath
                                  ? Loc.T(" (сетевой путь)", " (network path)")
                                  : admin ? "" : Loc.T(" (без админа)", " (without admin)"));
                DirectoryWalk.BuildLive(session);
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

        public static bool IsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// UNC-путь (\\server\share\...). К локальным путям с мапленными дисками не
        /// относится намеренно: у мапленного диска есть буква, MFT по нему тоже не
        /// прочитать, но это отдельный случай — сетевым считаем только явный UNC.
        /// </summary>
        public static bool IsNetworkPath(string path)
        {
            return path != null && path.StartsWith("\\\\", StringComparison.Ordinal);
        }

        /// <summary>
        /// Пробная попытка прочитать корень: отличает «шара недоступна / нет прав»
        /// (внятная ошибка) от «пустой каталог» (нормальный результат). Проверка
        /// дешёвая — один FindFirstFile по маске «\*».
        /// </summary>
        public static bool CanReadDirectory(string path)
        {
            try
            {
                Win32Entry[] entries;
                long links;
                return Win32Find.TryReadDirectory(path, out entries, out links);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNtfs(string root)
        {
            try
            {
                var drive = new DriveInfo(root.Substring(0, 1));
                return string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
