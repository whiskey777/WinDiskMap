using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinDiskMap.Core
{
    /// <summary>Одна запись каталога из WIN32_FIND_DATA (имя, атрибуты, размер).</summary>
    internal struct Win32Entry
    {
        public string Name;
        public bool IsDir;
        public bool IsReparse;
        public long Size;
        /// <summary>«На диске»: реально занятые байты (FILE_STANDARD_INFO.AllocationSize).</summary>
        public long Allocated;
        /// <summary>FILE_ATTRIBUTE_* битмаска из WIN32_FIND_DATA.</summary>
        public uint Attributes;

        /// <summary>FILETIME (UTC, 100 нс с 1601). 0 — неизвестно.</summary>
        public long CreatedUtc;
        public long LastWriteUtc;
    }

    /// <summary>
    /// Обход каталогов напрямую через FindFirstFileEx/FindNextFile: одна запись
    /// каталога отдаёт имя, атрибуты и размер сразу, поэтому на объект приходится
    /// один сисколл вместо трёх (EnumerateFileSystemEntries + GetAttributes + FileInfo).
    /// FindExInfoBasic не запрашивает DOS 8.3-имена — быстрее и не путает их с
    /// настоящими (WizTree тоже работает через FindFirstFile).
    /// </summary>
    internal static class Win32Find
    {
        private const uint FindExInfoBasic = 1;
        private const uint FindExSearchNameMatch = 0;
        private const uint FindFirstExLargeFetch = 0x00000002;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorAccessDenied = 5;
        private const int ErrorNoMoreFiles = 18;
        private const int InvalidHandleValue = -1;
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeReparsePoint = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string lpFileName, uint fInfoLevelId,
            out WIN32_FIND_DATA lpFindFileData, uint fSearchOp, IntPtr lpSearchFilter, uint dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_STANDARD_INFO
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint NumberOfLinks;
            public byte DeletePending;
            public byte Directory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(IntPtr hFile, int fileInformationClass,
            out FILE_STANDARD_INFO lpFileInformation, uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        /// <summary>
        /// Выделенный размер файла («На диске»): AllocationSize из FILE_STANDARD_INFO —
        /// это сумма кластеров, то есть то же, что WizTree берёт из data runs в MFT.
        /// GetCompressedFileSizeW не подходит: на NTFS он возвращает логический размер.
        /// Если файл нельзя открыть (блокировка/доступ), пробуем его и затем логический размер.
        /// </summary>
        private static long GetAllocated(string fullPath, long logicalSize)
        {
            const uint FileReadAttributes = 0x80;
            const uint ShareAll = 1 | 2 | 4;
            const uint OpenExisting = 3;
            const int FileStandardInfo = 1;

            IntPtr h = CreateFileW(fullPath, FileReadAttributes, ShareAll, IntPtr.Zero,
                                   OpenExisting, 0, IntPtr.Zero);
            if (h != new IntPtr(-1))
            {
                try
                {
                    FILE_STANDARD_INFO info;
                    if (GetFileInformationByHandleEx(h, FileStandardInfo, out info,
                                                     (uint)Marshal.SizeOf(typeof(FILE_STANDARD_INFO))))
                        return info.AllocationSize;
                }
                finally { CloseHandle(h); }
            }

            uint high;
            uint low = GetCompressedFileSizeW(fullPath, out high);
            if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) return logicalSize;
            long value = ((long)high << 32) | low;
            return value < 0 ? logicalSize : value;
        }

        /// <summary>
        /// Читает каталог целиком. Возвращает false, если каталог недоступен или исчез
        /// (эквивалент прежних catch-блоков: такой каталог остаётся пустым узлом).
        /// «.» и «..» отбрасываются, reparse-признак отдаётся для папок и файлов.
        /// </summary>
        public static bool TryReadDirectory(string path, out Win32Entry[] entries, out long links)
        {
            entries = null;
            links = 0;

            string mask = path.EndsWith("\\", StringComparison.Ordinal) ? path + "*" : path + "\\*";
            WIN32_FIND_DATA data;
            IntPtr handle = FindFirstFileExW(mask, FindExInfoBasic, out data,
                FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);
            if (handle == new IntPtr(InvalidHandleValue))
            {
                int err = Marshal.GetLastWin32Error();
                // Пустой каталог даёт ERROR_FILE_NOT_FOUND («нет подходящих файлов») —
                // это валидный результат, а не ошибка доступа.
                if (err == ErrorNoMoreFiles || err == ErrorFileNotFound || err == ErrorPathNotFound)
                {
                    entries = Empty;
                    return true;
                }
                if (err == ErrorAccessDenied) return false;
                return false;
            }

            var list = new System.Collections.Generic.List<Win32Entry>(64);
            long skipped = 0;
            try
            {
                do
                {
                    string name = data.cFileName;
                    if (name == "." || name == "..") continue;

                    bool isDir = (data.dwFileAttributes & FileAttributeDirectory) != 0;
                    bool reparse = (data.dwFileAttributes & FileAttributeReparsePoint) != 0;
                    if (isDir && reparse)
                    {
                        skipped++; // junction/symlink-каталоги не обходим (иначе петли)
                        continue;
                    }

                    long logical = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                    list.Add(new Win32Entry
                    {
                        Name = name,
                        IsDir = isDir,
                        IsReparse = reparse,
                        // Размер файла — из записи каталога (для папок всегда 0).
                        Size = logical,
                        // Один дополнительный сисколл на файл: «На диске» для CSV в формате WizTree.
                        Allocated = isDir ? 0 : GetAllocated(path + "\\" + name, logical),
                        Attributes = data.dwFileAttributes,
                        // WIN32_FIND_DATA уже содержит обе даты — дополнительных сисколлов нет.
                        CreatedUtc = ((long)data.ftCreationTimeHigh << 32) | data.ftCreationTimeLow,
                        LastWriteUtc = ((long)data.ftLastWriteTimeHigh << 32) | data.ftLastWriteTimeLow
                    });
                }
                while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }

            links = skipped;
            entries = list.Count == 0 ? Empty : list.ToArray();
            return true;
        }

        /// <summary>
        /// Даты и атрибуты самого каталога (без wildcard FindFirstFile возвращает its own entry).
        /// Используется для корня скана: остальные записи приходят из TryReadDirectory.
        /// </summary>
        public static bool TryGetPathEntry(string path, out Win32Entry entry)
        {
            entry = default(Win32Entry);
            WIN32_FIND_DATA data;
            IntPtr handle = FindFirstFileExW(path, FindExInfoBasic, out data,
                FindExSearchNameMatch, IntPtr.Zero, 0);
            if (handle == new IntPtr(InvalidHandleValue)) return false;
            try
            {
                entry = new Win32Entry
                {
                    Name = data.cFileName,
                    IsDir = (data.dwFileAttributes & FileAttributeDirectory) != 0,
                    IsReparse = (data.dwFileAttributes & FileAttributeReparsePoint) != 0,
                    Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                    Allocated = 0,
                    Attributes = data.dwFileAttributes,
                    CreatedUtc = ((long)data.ftCreationTimeHigh << 32) | data.ftCreationTimeLow,
                    LastWriteUtc = ((long)data.ftLastWriteTimeHigh << 32) | data.ftLastWriteTimeLow
                };
                return true;
            }
            finally
            {
                FindClose(handle);
            }
        }

        private static readonly Win32Entry[] Empty = new Win32Entry[0];
    }
}
