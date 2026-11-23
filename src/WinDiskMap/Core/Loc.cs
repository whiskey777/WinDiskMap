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

        /// <summary>Подсказка клавиш в футере TUI.</summary>
        public static string KeysHint
        {
            get
            {
                return _ru
                    ? " ↑↓ навигация  ← свернуть/к родителю  → развернуть  Enter открыть файл/папку  f фильтр  s сортировка+направление  e экспорт  Del удалить  q выход"
                    : " ↑↓ navigate  ← collapse/parent  → expand  Enter open file/folder  f filter  s sort+direction  e export  Del delete  q quit";
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
