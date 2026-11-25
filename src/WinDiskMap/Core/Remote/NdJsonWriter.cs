using System;
using System.IO;
using System.Text;

namespace WinDiskMap.Core.Remote
{
    /// <summary>
    /// Поток записей NDJSON в stdout агента. Каждая запись — отдельная строка; записи
    /// буферизуются пачками примерно по 64 КБ и сбрасываются вместе. При скорости
    /// агента ~14 МБ/с задержка на пачку &lt;5 мс, зато исчезают сотни тысяч Flush().
    /// End() и Error() сбрасывают буфер сразу. Вывод строго UTF-8 без BOM.
    /// </summary>
    internal sealed class NdJsonWriter
    {
        private const int BatchChars = 1 << 16;
        private readonly StreamWriter _out;
        private readonly StringBuilder _sb = new StringBuilder(256);
        private readonly StringBuilder _batch = new StringBuilder(BatchChars + 4096);

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

        public void Dir(int id, int parent, string name, bool reparse,
            long createdUtc = 0, long lastWriteUtc = 0, long allocated = 0, uint attributes = 0)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"dir\",\"id\":").Append(id)
               .Append(",\"parent\":").Append(parent)
               .Append(",\"reparse\":").Append(reparse ? "true" : "false")
               .Append(",\"c\":").Append(createdUtc)
               .Append(",\"w\":").Append(lastWriteUtc)
               .Append(",\"a\":").Append(allocated)
               .Append(",\"f\":").Append(attributes)
               .Append(",\"name\":");
            NdJson.WriteString(_sb, name);
            _sb.Append('}');
            Line();
        }

        public void File(int id, int parent, string name, long size,
            long createdUtc = 0, long lastWriteUtc = 0, long allocated = 0, uint attributes = 0)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"file\",\"id\":").Append(id)
               .Append(",\"parent\":").Append(parent)
               .Append(",\"size\":").Append(size)
               .Append(",\"c\":").Append(createdUtc)
               .Append(",\"w\":").Append(lastWriteUtc)
               .Append(",\"a\":").Append(allocated)
               .Append(",\"f\":").Append(attributes)
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
            Flush();
        }

        public void Error(string message)
        {
            _sb.Length = 0;
            _sb.Append("{\"t\":\"error\",\"v\":");
            NdJson.WriteString(_sb, message);
            _sb.Append('}');
            Line();
            Flush();
        }

        /// <summary>Дозапись строки в пачку; настоящий Flush — по порогу или явно.</summary>
        private void Line()
        {
            _batch.Append(_sb).Append('\n');
            if (_batch.Length >= BatchChars) Flush();
        }

        public void Flush()
        {
            if (_batch.Length > 0)
            {
                _out.Write(_batch.ToString());
                _batch.Length = 0;
            }
            _out.Flush();
        }
    }
}
