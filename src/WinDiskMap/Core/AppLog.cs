using System;
using System.IO;
using System.Text;

namespace WinDiskMap.Core
{
    /// <summary>
    /// Опциональный журнал с таймстемпами для диагностики (--log). Потокобезопасен.
    /// Если не инициализирован — все вызовы игнорируются (почти нулевые накладные).
    /// </summary>
    public static class AppLog
    {
        private static readonly object Sync = new object();
        private static StreamWriter _writer;

        public static bool Enabled
        {
            get { return _writer != null; }
        }

        public static void Init(string path)
        {
            var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.AutoFlush = true;
            lock (Sync) { _writer = writer; }
        }

        public static void Write(string level, string message)
        {
            var w = _writer;
            if (w == null) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                          + " [" + level + "] " + message;
            lock (Sync)
            {
                if (_writer == null) return;
                _writer.WriteLine(line);
            }
        }

        public static void Close()
        {
            lock (Sync)
            {
                if (_writer == null) return;
                try { _writer.Dispose(); }
                catch { }
                _writer = null;
            }
        }
    }
}
