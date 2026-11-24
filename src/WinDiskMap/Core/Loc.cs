using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WinDiskMap.Core
{
    /// <summary>
    /// Локализация интерфейса. Русский выбирается ТОЛЬКО если язык интерфейса ОС русский
    /// (по умолчанию, без учёта --lang) — на англоязычной ОС русского текста нет вовсе,
    /// что исключает проблемы со шрифтами/кодировками.
    /// </summary>
    public static class Loc
    {
        /// <summary>Одна подсказка футера: клавиша и её описание.</summary>
        public struct KeyHint
        {
            public readonly string Key;
            public readonly string Description;

            public KeyHint(string key, string description)
            {
                Key = key;
                Description = description;
            }
        }

        private static readonly KeyHint[] RuKeyHints =
        {
            new KeyHint("↑↓", "навигация"),
            new KeyHint("t", "тема"),
            new KeyHint("←", "свернуть/к родителю"),
            new KeyHint("→", "развернуть"),
            new KeyHint("Enter", "открыть файл/папку"),
            new KeyHint("f", "фильтр"),
            new KeyHint("s", "сортировка+направление"),
            new KeyHint("e", "экспорт"),
            new KeyHint("Del", "удалить"),
            new KeyHint("q", "выход")
        };

        private static readonly KeyHint[] EnKeyHints =
        {
            new KeyHint("↑↓", "navigate"),
            new KeyHint("t", "theme"),
            new KeyHint("←", "collapse/parent"),
            new KeyHint("→", "expand"),
            new KeyHint("Enter", "open file/folder"),
            new KeyHint("f", "filter"),
            new KeyHint("s", "sort+direction"),
            new KeyHint("e", "export"),
            new KeyHint("Del", "delete"),
            new KeyHint("q", "quit")
        };

        private static bool _ru;

        public static bool Ru { get { return _ru; } }

        /// <summary>lang: "ru" | "en" | null (автоопределение по ОС).</summary>
        public static void Init(string lang)
        {
            if (lang == "ru") _ru = true;
            else if (lang == "en") _ru = false;
            else _ru = DetectOsRussian();
        }

        public static string T(string ru, string en)
        {
            return _ru ? ru : en;
        }

        /// <summary>Подсказки футера TUI: клавиша + описание (клавишу красим отдельно).</summary>
        public static KeyHint[] KeyHints
        {
            get { return _ru ? RuKeyHints : EnKeyHints; }
        }

        /// <summary>Плоский текст подсказок — для логов и тестов; рендер красит клавиши.</summary>
        public static string KeysHint
        {
            get
            {
                var sb = new System.Text.StringBuilder(160);
                KeyHint[] hints = KeyHints;
                for (int i = 0; i < hints.Length; i++)
                {
                    if (i > 0) sb.Append("  ");
                    sb.Append(hints[i].Key).Append(' ').Append(hints[i].Description);
                }
                return sb.ToString();
            }
        }

        private static bool DetectOsRussian()
        {
            // GetUserDefaultUILanguage не зависит от InvariantGlobalization и от CultureInfo.
            try
            {
                if ((GetUserDefaultUILanguage() & 0x3FF) == 0x0019) return true; // ru-RU
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();
    }
}
