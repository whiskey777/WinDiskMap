using System;
using System.IO;
using System.Text;

namespace WinDiskMap.Core.Remote
{
    /// <summary>
    /// Поток записей NDJSON в stdout агента. Каждая запись — отдельная строка,
    /// сбрасывается сразу (хост читает поток живьём, счётчики растут по мере скана).
    /// Вывод строго UTF-8 без BOM: хост декодирует его как UTF-8.
    /// </summary>
    internal sealed class NdJsonWriter
    {
        private readonly StreamWriter _out;
        private readonly StringBuilder _sb = new StringBuilder(256);

        public NdJsonWriter(Stream stream)
        {
            // leaveOpen: true — поток stdout закрывается вместе с процессом, вручную не трогаем.
            _out = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16)
            {
                AutoFlush = false
            };
        }

        public void Meta(string mode)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"meta\",\"mode\":");
            NdJson.WriteString(_sb, mode);
            _sb.Append('}');
            Line();
        }

        public void Phase(string value)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"phase\",\"v\":");
            NdJson.WriteString(_sb, value);
            _sb.Append('}');
            Line();
        }

        public void Note(string value)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"note\",\"v\":");
            NdJson.WriteString(_sb, value);
            _sb.Append('}');
            Line();
        }

        public void Dir(int id, int parent, string name, bool reparse)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"dir\",\"id\":").Append(id)
               .Append(",\"parent\":").Append(parent)
               .Append(",\"reparse\":").Append(reparse ? "true" : "false")
               .Append(",\"name\":");
            NdJson.WriteString(_sb, name);
            _sb.Append('}');
            Line();
        }

        public void File(int id, int parent, string name, long size)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"file\",\"id\":").Append(id)
               .Append(",\"parent\":").Append(parent)
               .Append(",\"size\":").Append(size)
               .Append(",\"name\":");
            NdJson.WriteString(_sb, name);
            _sb.Append('}');
            Line();
        }

        public void End(long files, long dirs, long bytes)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"end\",\"files\":").Append(files)
               .Append(",\"dirs\":").Append(dirs)
               .Append(",\"bytes\":").Append(bytes)
               .Append('}');
            Line();
        }

        public void Error(string message)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"error\",\"v\":");
            NdJson.WriteString(_sb, message);
            _sb.Append('}');
            Line();
        }

        /// <summary>Дозапись строки и сброс буфера: хост видит данные без задержки.</summary>
        private void Line()
        {
            _out.Write(_sb.ToString());
            _out.Write('\n');
            _out.Flush();
        }

        public void Flush() { _out.Flush(); }
    }
}
