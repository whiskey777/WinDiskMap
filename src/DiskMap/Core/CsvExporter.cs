using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DiskMap.Core
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
    /// Экспорт в CSV: UTF-8 с BOM, разделитель «;» (корректно открывается в Excel
    /// с русской локалью), потоковая запись — миллионы строк не держим в памяти.
    /// </summary>
    public static class CsvExporter
    {
        public static long Export(Node root, CsvOptions options, Action<long> progress = null)
        {
            long total = root.Size;
            long rows = 0;

            using (var writer = new StreamWriter(options.OutputPath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(Loc.T(
                    "Тип;Глубина;Родитель;Имя;РазмерБайт;Размер;Категория;ДоляПроцент",
                    "Type;Depth;Parent;Name;SizeBytes;Size;Category;Percent"));

                var stack = new Stack<Node>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    Node dir = stack.Pop();

                    if (dir.Depth <= options.MaxDepth && options.Only == OnlyMode.Dirs)
                    {
                        WriteDirRow(writer, dir, total);
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

                writer.Write(Loc.T("Файл;", "File;"));
                writer.Write(dir.Depth + 1);
                writer.Write(';');
                WriteQuoted(writer, dir.FullPath);
                writer.Write(';');
                WriteQuoted(writer, f.Name);
                writer.Write(';');
                writer.Write(f.Size);
                writer.Write(';');
                writer.Write(Format.Bytes(f.Size));
                writer.Write(';');
                writer.Write(CategoryName(f.Category));
                writer.Write(';');
                writer.Write(Format.Percent(f.Size, total));
                writer.WriteLine();
                rows++;
            }
            return rows;
        }

        private static void WriteDirRow(StreamWriter writer, Node dir, long total)
        {
            writer.Write(Loc.T("Папка;", "Folder;"));
            writer.Write(dir.Depth);
            writer.Write(';');
            WriteQuoted(writer, dir.Parent != null ? dir.Parent.FullPath : "");
            writer.Write(';');
            WriteQuoted(writer, dir.Name);
            writer.Write(';');
            writer.Write(dir.Size);
            writer.Write(';');
            writer.Write(Format.Bytes(dir.Size));
            writer.Write(';');
            writer.Write(CategoryName(Category.Folder));
            writer.Write(';');
            writer.Write(Format.Percent(dir.Size, total));
            writer.WriteLine();
        }

        private static string CategoryName(Category c)
        {
            switch (c)
            {
                case Category.Temp: return "Temp";
                case Category.Dump: return "Dump";
                case Category.Folder: return Loc.T("Папка", "Folder");
                default: return Loc.T("Файл", "File");
            }
        }

        private static void WriteQuoted(StreamWriter writer, string value)
        {
            if (value == null) value = "";
            if (value.IndexOf(';') >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0)
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
