using System;
using System.IO;
using System.Security;

namespace WinDiskMap.Tui
{
    /// <summary>
    /// Набор ANSI-атрибутов одного варианта оформления. Все значения — готовые
    /// escape-последовательности: рендер просто подставляет их в StringBuilder.
    /// </summary>
    internal sealed class Palette
    {
        public readonly string Id;
        public readonly string NameRu;
        public readonly string NameEn;
        public readonly string Reset;
        public readonly string Dim;
        public readonly string Header;
        public readonly string Folder;
        public readonly string File;
        public readonly string Temp;
        public readonly string Dump;
        public readonly string Warn;
        public readonly string SortActive;

        public Palette(string id, string nameRu, string nameEn,
                       string reset, string dim, string header,
                       string folder, string file, string temp, string dump,
                       string warn, string sortActive)
        {
            Id = id;
            NameRu = nameRu;
            NameEn = nameEn;
            Reset = reset;
            Dim = dim;
            Header = header;
            Folder = folder;
            File = file;
            Temp = temp;
            Dump = dump;
            Warn = warn;
            SortActive = sortActive;
        }

        public string Name(bool ru)
        {
            return ru ? NameRu : NameEn;
        }
    }

    /// <summary>
    /// Темы TUI и сохранение выбора между запусками. Порядок в цикле:
    /// VS Code Dark (по умолчанию) → Norton/FAR → классическая тема beta.1.
    /// </summary>
    internal static class Theme
    {
        public static readonly Palette[] All =
        {
            // Приближение к VS Code Dark Modern: приглушённый серый вместо
            // комментариев, мягкие акценты, синяя подсветка выделения.
            new Palette(
                id: "vscode-dark",
                nameRu: "VS Code Dark",
                nameEn: "VS Code Dark",
                reset: "\x1b[0m",
                dim: "\x1b[38;5;245m",
                header: "\x1b[48;5;236;38;5;251m",
                folder: "\x1b[38;5;80m",
                file: "\x1b[38;5;251m",
                temp: "\x1b[38;5;185m",
                dump: "\x1b[38;5;176m",
                warn: "\x1b[1;38;5;203m",
                sortActive: "\x1b[48;5;24;38;5;15m"),

            // Классика Norton Commander / Far Manager: синие панели, жёлтые
            // каталоги, белая шапка, контрастный красный диалог удаления.
            new Palette(
                id: "nc-far",
                nameRu: "Нортон / FAR",
                nameEn: "Norton / FAR",
                reset: "\x1b[0m",
                dim: "\x1b[37m",
                header: "\x1b[44;97m",
                folder: "\x1b[1;93m",
                file: "\x1b[37m",
                temp: "\x1b[96m",
                dump: "\x1b[95m",
                warn: "\x1b[1;97;41m",
                sortActive: "\x1b[44;1;93m"),

            // Прежняя палитра beta.1 — запасной вариант.
            new Palette(
                id: "classic",
                nameRu: "Классика WinDiskMap",
                nameEn: "WinDiskMap classic",
                reset: "\x1b[0m",
                dim: "\x1b[90m",
                header: "\x1b[7m",
                folder: "\x1b[1;36m",
                file: "\x1b[37m",
                temp: "\x1b[33m",
                dump: "\x1b[35m",
                warn: "\x1b[1;91m",
                sortActive: "\x1b[1;7;33m")
        };

        public static int Index { get; private set; }

        public static Palette Current
        {
            get { return All[Index]; }
        }

        private static string SettingsPath
        {
            get
            {
                // Явный путь полезен для переносной установки и для тестов.
                string custom = Environment.GetEnvironmentVariable("WINDISKMAP_THEME_FILE");
                if (!string.IsNullOrWhiteSpace(custom)) return custom;

                string root = Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(root)) return null;

                // Не Path.Combine: известные литералы надёжнее и не дают ложного
                // предупреждения CodeQL о «потерянных» аргументах.
                return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar + "WinDiskMap"
                       + Path.DirectorySeparatorChar + "theme.txt";
            }
        }

        /// <summary>Читает сохранённую тему; неизвестное значение — тема по умолчанию.</summary>
        public static void Init()
        {
            Index = 0; // здоровое состояние при любом исходе чтения файла
            string path = SettingsPath;
            if (path == null) return;
            try
            {
                if (!File.Exists(path)) return;
                string id = File.ReadAllText(path).Trim();
                for (int i = 0; i < All.Length; i++)
                {
                    if (string.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        Index = i;
                        return;
                    }
                }
            }
            catch (IOException ex) { Note("read", ex); }
            catch (UnauthorizedAccessException ex) { Note("read", ex); }
            catch (ArgumentException ex) { Note("read", ex); }
            catch (NotSupportedException ex) { Note("read", ex); }
            catch (SecurityException ex) { Note("read", ex); }
            // Нет прав, битый файл — продолжаем с темой по умолчанию.
        }

        public static Palette Cycle()
        {
            Index = (Index + 1) % All.Length;
            Save();
            return Current;
        }

        private static void Save()
        {
            string path = SettingsPath;
            if (path == null) return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Current.Id);
            }
            catch (IOException ex) { Note("write", ex); }
            catch (UnauthorizedAccessException ex) { Note("write", ex); }
            catch (ArgumentException ex) { Note("write", ex); }
            catch (NotSupportedException ex) { Note("write", ex); }
            catch (SecurityException ex) { Note("write", ex); }
            // Сохранение best-effort: тема продолжает работать в этой сессии.
        }

        private static void Note(string action, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "WinDiskMap theme " + action + " failed: " + ex.Message);
        }

        /// <summary>
        /// Физическая клавиша T. В .NET её VirtualKey обычно остаётся ConsoleKey.T
        /// и в русской раскладке; на всякий случай принимаем и кириллическую Е
        /// (U+0435/U+0415), которую та же клавиша печатает в ЙЦУКЕН. Латинская e
        /// (U+0065) — это соседняя клавиша экспорта и сюда не попадает.
        /// </summary>
        internal static bool IsThemeKey(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.T) return true;
            return key.KeyChar == '\u0435' || key.KeyChar == '\u0415';
        }
    }
}
