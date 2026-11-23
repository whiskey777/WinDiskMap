using System;
using System.IO;

namespace WinDiskMap.Core
{
    /// <summary>
    /// Удаление объекта дерева с диска. Реальный RemoveDirectory/DeleteFile, а не только
    /// правка дерева: после успеха узел убирается из модели, размеры предков пересчитываются.
    ///
    /// Reparse-папки (junction/symlink) удаляются как ссылка — без обхода цели, иначе
    /// рекурсия ушла бы за пределы дерева (например, «Application Data»).
    /// Косые пути получают префикс \\?\ — так работают длинные имена и UNC.
    /// </summary>
    public static class DeleteHelper
    {
        public static bool TryDelete(string path, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(path))
            {
                error = Loc.T("Путь пуст", "Empty path");
                return false;
            }

            try
            {
                // Корень тома/шары не удаляем ни при каких обстоятельствах.
                string root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root)
                    && string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'),
                                     StringComparison.OrdinalIgnoreCase))
                {
                    error = Loc.T("Нельзя удалить корень тома", "Cannot delete a volume root");
                    return false;
                }

                string target = DirectoryWalk.LongPath(path.TrimEnd('\\'));
                FileAttributes attrs = File.GetAttributes(target);
                if ((attrs & FileAttributes.Directory) != 0)
                    DeleteDirectory(target, attrs);
                else
                    DeleteFile(target, attrs);

                AppLog.Write("WARN", "deleted: " + path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLog.Write("ERROR", "delete failed: " + path + " — " + ex.Message);
                return false;
            }
        }

        private static void DeleteFile(string path, FileAttributes attrs)
        {
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }

        private static void DeleteDirectory(string path, FileAttributes attrs)
        {
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

            // Junction/symlink: RemoveDirectory удаляет саму ссылку, не разворачивая её.
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path, false);
                return;
            }

            foreach (string child in Directory.GetFileSystemEntries(path))
            {
                FileAttributes childAttrs = File.GetAttributes(child);
                if ((childAttrs & FileAttributes.Directory) != 0)
                    DeleteDirectory(child, childAttrs);
                else
                    DeleteFile(child, childAttrs);
            }
            Directory.Delete(path, false);
        }
    }
}
