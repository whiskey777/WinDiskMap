using System;
using System.Globalization;
using System.Text;

namespace WinDiskMap.Core.Remote
{
    /// <summary>
    /// Минимальный NDJSON: одна JSON-запись на строку, без зависимостей (работает и на
    /// net48, и на net8.0). Формат намеренно узкий — только то, что нужно протоколу
    /// агента: строки, числа, bool. Полноценный JSON-парсер не нужен и не тащится.
    ///
    /// Строки экранируются по правилам JSON (кавычка, обратный слэш, управляющие
    /// символы, включая \uXXXX) — имена файлов могут содержать что угодно.
    /// </summary>
    internal static class NdJson
    {
        public static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                            {
                                sb.Append("\\u");
                                sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// Извлекает строковое значение поля из JSON-объекта (одна строка). Ищет
        /// <c>"key":</c> и читает либо строку в кавычках, либо число/bool как есть.
        /// Возвращает null, если поля нет. Экранирование строк разворачивается.
        /// </summary>
        public static string GetRaw(string line, string key)
        {
            string needle = "\"" + key + "\":";
            int at = line.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return null;
            int p = at + needle.Length;
            if (p >= line.Length) return null;

            if (line[p] == '"' ) return ParseString(line, ref p);

            int start = p;
            while (p < line.Length && line[p] != ',' && line[p] != '}') p++;
            return line.Substring(start, p - start).Trim();
        }

        public static string GetString(string line, string key)
        {
            return GetRaw(line, key);
        }

        public static long GetLong(string line, string key, long fallback = 0)
        {
            string raw = GetRaw(line, key);
            long v;
            if (raw != null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }

        public static int GetInt(string line, string key, int fallback = 0)
        {
            return (int)GetLong(line, key, fallback);
        }

        public static bool GetBool(string line, string key)
        {
            string raw = GetRaw(line, key);
            return raw != null && raw == "true";
        }

        private static string ParseString(string line, ref int p)
        {
            p++; // открывающая кавычка
            var sb = new StringBuilder(32);
            while (p < line.Length)
            {
                char c = line[p++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }

                if (p >= line.Length) break;
                char e = line[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (p + 4 <= line.Length)
                        {
                            int code;
                            if (int.TryParse(line.Substring(p, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                p += 4;
                            }
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
