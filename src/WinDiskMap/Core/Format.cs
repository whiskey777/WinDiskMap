using System;
using System.Globalization;

namespace WinDiskMap.Core
{
    internal static class Format
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>Человекочитаемый размер (1024-основание, как у WizTree).</summary>
        public static string Bytes(long value)
        {
            if (value < 1024) return value.ToString(CultureInfo.InvariantCulture) + " B";
            double v = value;
            int u = 0;
            while (v >= 1024 && u < Units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + Units[u];
        }

        public static string Number(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>Процент от общего объёма (берущегося из root), аккуратный вывод.</summary>
        public static string Percent(long part, long whole)
        {
            if (whole <= 0) return "0.0%";
            double p = part * 100.0 / whole;
            return p.ToString(p >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// Дата из FILETIME (UTC) в местном времени. 0 или битой дате соответствует "-",
        /// чтобы колонки не «прыгали» при скане через старого агента без дат.
        /// </summary>
        public static string Time(long filetimeUtc, bool brief = false)
        {
            if (filetimeUtc <= 0) return "-";
            try
            {
                DateTime local = DateTime.FromFileTimeUtc(filetimeUtc).ToLocalTime();
                if (local.Year < 1900 || local.Year > 2100) return "-";
                return local.ToString(brief ? "MM-dd HH:mm" : "yyyy-MM-dd HH:mm",
                                      CultureInfo.InvariantCulture);
            }
            catch
            {
                return "-";
            }
        }
    }
}
