using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinDiskMap.Core
{
    public enum OnlyMode
    {
        /// <summary>Строки-агрегаты папок (по умолчанию).</summary>
        Dirs,
        /// <summary>Строки всех файлов.</summary>
        Files,
        /// <summary>Строки только tmp/temp-файлов.</summary>
        Tmp,
        /// <summary>Строки только дампов.</summary>
        Dumps
    }

    public sealed class CsvOptions
    {
        public string OutputPath;
        /// <summary>В режиме Dirs: добавить к агрегатам папок строки каждого файла.</summary>
        public bool Details;
        /// <summary>Максимальная глубина строк относительно корня экспорта (0 — только корень).</summary>
        public int MaxDepth = int.MaxValue;
        public OnlyMode Only = OnlyMode.Dirs;
    }

    /// <summary>
    /// Экспорт в едином «combined» формате: поля WizTree в их родном порядке первыми,
    /// затем наши поля (иерархия, категория, human-readable размер, доля).
    /// UTF-8 с BOM, разделитель «,» (стандартный CSV; Import-Csv в PowerShell работает
    /// без параметров). Заголовки и значения ВСЕГДА английские, независимо от --lang:
    /// формат файла не должен зависеть от локали ОС, иначе ломается разбор PowerShell-объектов.
    /// Первые семь колонок совпадают с WizTree по имени и смыслу:
    ///   File name,Size,On disk,Modified,Attributes,Files,Folders
    /// Четыре колонки тома (DRIVECAPACITY …, в строке корня у WizTree) вынесены в конец
    /// и заполняются только в строке корня — так позиции наших колонок не «прыгают».
    /// Строки-баннера перед заголовком нет (в отличие от экспорта WizTree).
    /// Потоковая запись: миллионы строк не держатся в памяти.
    /// </summary>
    public static class CsvExporter
    {
        private const string Header =
            "File name,Size,On disk,Modified,Attributes,Files,Folders," +
            "Depth,Parent,Name,Category,SizeHuman,Percent," +
            "DRIVECAPACITY,FREESPACE,USEDSPACE,RESERVEDSPACE";

        private struct VolumeInfo
        {
            public bool Ok;
            public long Capacity;
            public long Free;
            public long Used;
            public long Reserved;
        }

        public static long Export(Node root, CsvOptions options, Action<long> progress = null)
        {
            long total = root.Size;
            long rows = 0;

            // Счётчики поддерева и параметры тома считаем один раз до записи.
            FillSubtreeCounts(root);
            VolumeInfo vol = ReadVolume(root.FullPath);

            using (var writer = new StreamWriter(options.OutputPath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(Header);

                var stack = new Stack<Node>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    Node dir = stack.Pop();

                    if (dir.Depth <= options.MaxDepth && options.Only == OnlyMode.Dirs)
                    {
                        WriteDirRow(writer, dir, total, vol);
                        rows++;
                    }

                    // Файловая строка живёт на уровне dir.Depth + 1 — глубина считается по ней самой.
                    if (dir.Depth + 1 <= options.MaxDepth)
                    {
                        if (options.Only == OnlyMode.Dirs)
                        {
                            if (options.Details)
                                rows += WriteFileRows(writer, dir, OnlyMode.Files, total);
                        }
                        else
                        {
                            rows += WriteFileRows(writer, dir, options.Only, total);
                        }
                    }

                    for (int i = dir.Dirs.Count - 1; i >= 0; i--)
                        stack.Push(dir.Dirs[i]);

                    if (progress != null && (rows & 0x3FFF) == 0) progress(rows);
                }
            }

            return rows;
        }

        /// <summary>Экспорт агрегатов всего дерева (используется клавишей «e» в TUI).</summary>
        public static long ExportVisible(Node root, string path)
        {
            var options = new CsvOptions { OutputPath = path, Only = OnlyMode.Dirs, Details = false };
            return Export(root, options);
        }

        /// <summary>Рекурсивные счётчики файлов/папок для колонок WizTree «Файлы»/«Папки».
        /// Reparse-папки не учитываются — так же, как в счётчиках сканера и у WizTree.</summary>
        private static void FillSubtreeCounts(Node node)
        {
            long files = node.FileCount;
            long dirs = node.IsReparse ? 0 : 1;
            for (int i = 0; i < node.Dirs.Count; i++)
            {
                FillSubtreeCounts(node.Dirs[i]);
                files += node.Dirs[i].SubtreeFiles;
                dirs += node.Dirs[i].SubtreeDirs;
            }
            node.SubtreeFiles = files;
            node.SubtreeDirs = dirs;
        }

        private static VolumeInfo ReadVolume(string fullPath)
        {
            var result = new VolumeInfo();
            try
            {
                string root = Path.GetPathRoot(fullPath ?? "");
                if (string.IsNullOrEmpty(root) || root.Length < 3 || root[1] != ':') return result;
                var drive = new DriveInfo(root.Substring(0, 2));
                result.Capacity = drive.TotalSize;
                result.Free = drive.TotalFreeSpace;
                result.Used = result.Capacity - result.Free;
                result.Reserved = drive.TotalFreeSpace - drive.AvailableFreeSpace;
                result.Ok = true;
            }
            catch
            {
                // UNC-путь или диск без статистики — колонки остаются пустыми.
            }
            return result;
        }

        private static long WriteFileRows(StreamWriter writer, Node dir, OnlyMode mode, long total)
        {
            if (dir.FileCount == 0) return 0;

            long rows = 0;
            var files = dir.GetFileRows();
            for (int i = 0; i < files.Count; i++)
            {
                FileRow f = files[i];
                if (mode == OnlyMode.Tmp && f.Category != Category.Temp) continue;
                if (mode == OnlyMode.Dumps && f.Category != Category.Dump) continue;

                // — поля WizTree —
                WriteQuoted(writer, f.FullPath, true);
                writer.Write(',');
                writer.Write(f.Size);
                writer.Write(',');
                writer.Write(f.Allocated > 0 ? f.Allocated : f.Size);
                writer.Write(',');
                WriteField(writer, Format.CsvTime(f.LastWriteUtc));
                writer.Write(',');
                writer.Write(f.Attributes);
                writer.Write(',');
                writer.Write('0'); // Файлы
                writer.Write(',');
                writer.Write('0'); // Папки

                // — наши поля —
                writer.Write(',');
                writer.Write(dir.Depth + 1);
                writer.Write(',');
                WriteQuoted(writer, dir.FullPath, true);
                writer.Write(',');
                WriteQuoted(writer, f.Name, true);
                writer.Write(',');
                writer.Write(CategoryName(f.Category));
                writer.Write(',');
                writer.Write(Format.Bytes(f.Size));
                writer.Write(',');
                writer.Write(Format.Percent(f.Size, total));

                WriteVolume(writer, default(VolumeInfo));
                writer.WriteLine();
                rows++;
            }
            return rows;
        }

        private static void WriteDirRow(StreamWriter writer, Node dir, long total, VolumeInfo vol)
        {
            // — поля WizTree —
            WriteQuoted(writer, WithTrailingSlash(dir.FullPath), true);
            writer.Write(',');
            writer.Write(dir.Size);
            writer.Write(',');
            writer.Write(dir.Allocated);
            writer.Write(',');
            WriteField(writer, Format.CsvTime(dir.LastWriteUtc));
            writer.Write(',');
            // WizTree не показывает бит FILE_ATTRIBUTE_DIRECTORY в колонке «Атрибуты».
            writer.Write(dir.Attributes & ~0x10u);
            writer.Write(',');
            writer.Write(dir.SubtreeFiles);
            writer.Write(',');
            writer.Write(dir.SubtreeDirs);

            // — наши поля —
            writer.Write(',');
            writer.Write(dir.Depth);
            writer.Write(',');
            WriteQuoted(writer, dir.Parent != null ? dir.Parent.FullPath : "", true);
            writer.Write(',');
            WriteQuoted(writer, dir.Name, true);
            writer.Write(',');
            writer.Write(CategoryName(Category.Folder));
            writer.Write(',');
            writer.Write(Format.Bytes(dir.Size));
            writer.Write(',');
            writer.Write(Format.Percent(dir.Size, total));

            WriteVolume(writer, dir.Parent == null ? vol : default(VolumeInfo));
            writer.WriteLine();
        }

        private static void WriteVolume(StreamWriter writer, VolumeInfo vol)
        {
            if (!vol.Ok)
            {
                writer.Write(",,,,");
                return;
            }
            writer.Write(',');
            writer.Write(vol.Capacity);
            writer.Write(',');
            writer.Write(vol.Free);
            writer.Write(',');
            writer.Write(vol.Used);
            writer.Write(',');
            writer.Write(vol.Reserved);
        }

        private static string WithTrailingSlash(string path)
        {
            if (string.IsNullOrEmpty(path)) return "\\";
            return path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\";
        }

        private static string CategoryName(Category c)
        {
            switch (c)
            {
                case Category.Temp: return "Temp";
                case Category.Dump: return "Dump";
                case Category.Folder: return "Folder";
                default: return "File";
            }
        }

        /// <summary>Простое поле: пустое/с разделителем/с кавычкой — в кавычки.</summary>
        private static void WriteField(StreamWriter writer, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0)
            {
                writer.Write('"');
                writer.Write(value.Replace("\"", "\"\""));
                writer.Write('"');
            }
            else
            {
                writer.Write(value);
            }
        }

        /// <summary>Пути в «Имя файла» у WizTree всегда в кавычках — делаем так же.</summary>
        private static void WriteQuoted(StreamWriter writer, string value, bool always)
        {
            if (value == null) value = "";
            if (always || value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0)
            {
                writer.Write('"');
                writer.Write(value.Replace("\"", "\"\""));
                writer.Write('"');
            }
            else
            {
                writer.Write(value);
            }
        }
    }
}
