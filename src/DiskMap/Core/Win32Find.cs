using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskMap.Core
{
    /// <summary>Одна запись каталога из WIN32_FIND_DATA (имя, атрибуты, размер).</summary>
    internal struct Win32Entry
    {
        public string Name;
        public bool IsDir;
        public bool IsReparse;
        public long Size;
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

                    list.Add(new Win32Entry
                    {
                        Name = name,
                        IsDir = isDir,
                        IsReparse = reparse,
                        // Размер файла — из записи каталога (для папок всегда 0).
                        Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow
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

        private static readonly Win32Entry[] Empty = new Win32Entry[0];
    }
}
