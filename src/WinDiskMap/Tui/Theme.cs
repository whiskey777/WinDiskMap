using System;
using System.IO;
using System.Security;

namespace WinDiskMap.Tui
{
    /// <summary>
    /// Набор ANSI-атрибутов одного варианта оформления. Все значения — готовые
    /// escape-последовательности: рендер просто подставляет их в StringBuilder.
    /// Цвета заданы в RGB, чтобы внешний вид не зависел от colorScheme терминала.
    /// </summary>
    internal sealed class Palette
    {
        public readonly string Id;
        public readonly string NameRu;
        public readonly string NameEn;
        /// <summary>Обычный текст и фон; возвращается после любой подсветки.</summary>
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
    /// Modern Dark (по умолчанию) → Norton/FAR → классическая тема beta.1.
    /// Все палитры используют 24-битный RGB и собственный фон: результат одинаков
    /// в Windows Terminal, классическом conhost и встроенном терминале VS Code.
    /// </summary>
    internal static class Theme
    {
        /// <summary>Возврат к настройкам терминала при выходе из программы.</summary>
        public const string HardReset = "\x1b[0m";

        public static readonly Palette[] All =
        {
            // Современная тёмная тема в духе GitHub Dark: тёмный фон #121314,
            // светлый текст #C9D1D9, приглушённый серый и синяя подсветка.
            new Palette(
                id: "modern-dark",
                nameRu: "Modern Dark",
                nameEn: "Modern Dark",
                reset: "\x1b[0m\x1b[38;2;201;209;217m\x1b[48;2;18;19;20m",
                dim: "\x1b[38;2;139;148;158m",
                header: "\x1b[48;2;36;79;120m\x1b[38;2;230;237;243m",
                folder: "\x1b[38;2;88;166;255m",
                file: "\x1b[38;2;201;209;217m",
                temp: "\x1b[38;2;210;153;102m",
                dump: "\x1b[38;2;163;113;196m",
                warn: "\x1b[1;38;2;248;81;73m\x1b[48;2;55;20;20m",
                sortActive: "\x1b[1;48;2;36;79;120m\x1b[38;2;255;255;255m"),

            // Классика Norton Commander / Far Manager: синие панели, жёлтые
            // каталоги, белая шапка, контрастный красный диалог удаления.
            // Цвета фиксированы в RGB и не берутся из палитры conhost.
            new Palette(
                id: "nc-far",
                nameRu: "Нортон / FAR",
                nameEn: "Norton / FAR",
                reset: "\x1b[0m\x1b[38;2;255;255;255m\x1b[48;2;0;0;128m",
                dim: "\x1b[38;2;192;192;192m",
                header: "\x1b[1;48;2;0;0;160m\x1b[38;2;255;255;255m",
                folder: "\x1b[1;38;2;255;255;0m",
                file: "\x1b[38;2;255;255;255m",
                temp: "\x1b[38;2;0;255;255m",
                dump: "\x1b[38;2;255;0;255m",
                warn: "\x1b[1;48;2;192;0;0m\x1b[38;2;255;255;255m",
                sortActive: "\x1b[1;48;2;0;0;160m\x1b[38;2;255;255;0m"),

            // Прежняя палитра beta.1, переписанная в RGB: тот же «характер»,
            // но без зависимости от ColorTable реестра и схемы Windows Terminal.
            new Palette(
                id: "classic",
                nameRu: "Классика WinDiskMap",
                nameEn: "WinDiskMap classic",
                reset: "\x1b[0m\x1b[38;2;204;204;204m\x1b[48;2;12;12;12m",
                dim: "\x1b[38;2;128;128;128m",
                header: "\x1b[38;2;12;12;12m\x1b[48;2;204;204;204m",
                folder: "\x1b[1;38;2;0;192;192m",
                file: "\x1b[38;2;204;204;204m",
                temp: "\x1b[38;2;220;220;0m",
                dump: "\x1b[38;2;220;0;220m",
                warn: "\x1b[1;38;2;255;100;100m",
                sortActive: "\x1b[38;2;12;12;12m\x1b[48;2;220;220;0m")
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
                // Старый идентификатор из beta.1 указывал на ту же палитру.
                if (string.Equals(id, "vscode-dark", StringComparison.OrdinalIgnoreCase))
                    id = All[0].Id;
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
