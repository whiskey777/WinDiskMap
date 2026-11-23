using System;
using System.IO;

namespace WinDiskMap.Core
{
    public enum Category
    {
        Folder,
        File,
        Temp,
        Dump
    }

    /// <summary>
    /// Классификация объектов для цветового выделения в TUI и фильтрации в CSV.
    /// Temp: расширения .tmp/.temp/.partial и всё, что лежит в Temp/Temporary Files.
    /// Dump: расширения .dmp/.hdmp/.mdmp/.kdmp и всё в CrashDumps/WER.
    /// </summary>
    public static class CategoryClassifier
    {
        private static readonly string[] TempExtensions =
        {
            ".tmp", ".temp", ".partial", ".~", ".cache.tmp"
        };

        private static readonly string[] DumpExtensions =
        {
            ".dmp", ".hdmp", ".mdmp", ".kdmp", ".dmd"
        };

        private static readonly string[] TempPathMarkers =
        {
            "\\Temp\\", "\\Temporary Files\\", "\\tmp\\", "\\$temp$\\"
        };

        private static readonly string[] DumpPathMarkers =
        {
            "\\CrashDumps\\", "\\WER\\", "\\Minidump\\", "\\Minidumps\\", "\\Memory Dumps\\"
        };

        public static Category ClassifyFile(string name, string fullPath)
        {
            string ext = Path.GetExtension(name);
            if (ext != null && ext.Length > 0)
            {
                string e = ext.ToLowerInvariant();
                for (int i = 0; i < DumpExtensions.Length; i++)
                    if (e == DumpExtensions[i]) return Category.Dump;
                for (int i = 0; i < TempExtensions.Length; i++)
                    if (e == TempExtensions[i]) return Category.Temp;
            }

            if (fullPath != null)
            {
                string upper = fullPath.ToUpperInvariant();
                for (int i = 0; i < DumpPathMarkers.Length; i++)
                    if (upper.IndexOf(DumpPathMarkers[i], StringComparison.Ordinal) >= 0) return Category.Dump;
                for (int i = 0; i < TempPathMarkers.Length; i++)
                    if (upper.IndexOf(TempPathMarkers[i], StringComparison.Ordinal) >= 0) return Category.Temp;
            }

            return Category.File;
        }
    }
}
