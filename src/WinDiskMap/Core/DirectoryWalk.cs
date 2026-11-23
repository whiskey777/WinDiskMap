using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace WinDiskMap.Core
{
    /// <summary>
    /// Приёмник результатов обхода каталогов. Реализация решает, что делать с
    /// найденными записями: локальный сканер строит дерево Node, агент пишет NDJSON.
    /// Так у локального и удалённого режимов ОДНА реализация обхода — цифры совпадают.
    /// Все методы вызываются в одном потоке (сборщик) по порядку.
    /// </summary>
    public interface IWalkSink
    {
        /// <summary>Корень обхода (создаётся до чтения каталогов). Возвращает идентификатор корня.</summary>
        void Begin(string displayRoot);

        /// <summary>Каталог: id, id родителя (-1 у корня), имя, reparse-признак.</summary>
        void Dir(int id, int parent, string name, bool reparse);

        /// <summary>Файл: id, id родителя, имя, размер.</summary>
        void File(int id, int parent, string name, long size);

        /// <summary>Обход завершён. skippedLinks — отброшенные junction/symlink-каталоги.</summary>
        void End(long skippedLinks);
    }

    /// <summary>
    /// Обычный обход каталогов. Три роли:
    ///  - локальный живой скан (NodeSink строит дерево, TUI пополняется);
    ///  - агент удалённого скана (NdJsonSink пишет NDJSON в stdout);
    ///  - счётчики прогресса в ScanProgress.
    ///
    /// Junction/symlink-каталоги не обходятся (иначе петли в профиле вроде
    /// «Application Data» приводят к взрыву счётчиков), но видны в дереве/потоке.
    ///
    /// Чтение каталогов — напрямую FindFirstFileEx (см. Win32Find), распараллелено,
    /// но приёмник получает записи только из потока-сборщика, поэтому результат
    /// не зависит от числа потоков.
    /// </summary>
    public static class DirectoryWalk
    {
        /// <summary>Обход с живым деревом Node (локальный режим).</summary>
        public static void BuildLive(ScanSession session)
        {
            ScanProgress prog = session.Progress;
            prog.Phase = Loc.T("Обход каталогов", "Directory walk");

            var sink = new NodeSink(session, prog);
            Run(session.DisplayPath, sink, prog, out long skippedLinks);

            if (skippedLinks > 0)
            {
                string note = Loc.T("Пропущено junction/symlink-каталогов: ", "Skipped junction/symlink directories: ")
                              + skippedLinks.ToString("N0");
                prog.Note = prog.Note != null ? prog.Note + "; " + note : note;
            }
        }

        /// <summary>
        /// Ядро обхода. rootDisplay — путь как его показывать (для локального режима —
        /// дружелюбный путь; для агента — тот же путь, что задал хост).
        /// </summary>
        public static void Run(string rootDisplay, IWalkSink sink, ScanProgress prog, out long skippedLinks)
        {
            string displayRoot = (rootDisplay ?? "").TrimEnd('\\');
            if (displayRoot.Length == 0) displayRoot = rootDisplay;

            sink.Begin(displayRoot);
            prog.AddDir();

            long links = 0;
            new Reader(prog, sink, LongPath(displayRoot)).Run(ref links);
            sink.End(links);
            skippedLinks = links;
        }

        /// <summary>
        /// Приёмник локального режима: строит дерево Node под lock(Sync), корень
        /// появляется сразу (TUI читает дерево по ходу скана).
        /// </summary>
        private sealed class NodeSink : IWalkSink
        {
            private readonly TreeBuilder _tree;

            public NodeSink(ScanSession session, ScanProgress prog)
            {
                _tree = new TreeBuilder(session, prog);
            }

            public void Begin(string displayRoot) { _tree.Begin(displayRoot); }

            public void Dir(int id, int parent, string name, bool reparse)
            {
                _tree.Dir(id, parent, name, reparse);
            }

            public void File(int id, int parent, string name, long size)
            {
                _tree.File(id, parent, name, size);
            }

            public void End(long skippedLinks) { }
        }

        /// <summary>
        /// Параллельный обход: пул потоков читает каталоги, результаты складываются в
        /// очередь; поток-сборщик (вызывающий) применяет их к приёмнику. Счётчик
        /// _outstanding уменьшает только сборщик — иначе гонка завершения.
        /// </summary>
        private sealed class Reader
        {
            private struct Chunk
            {
                public int Id;
                public Win32Entry[] Entries;
            }

            private struct Job
            {
                public int Id;
                public string Path;
            }

            private readonly ScanProgress _prog;
            private readonly IWalkSink _sink;
            private readonly string _rootPath;

            private readonly Queue<Job> _jobs = new Queue<Job>();
            private readonly Queue<Chunk> _done = new Queue<Chunk>();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private readonly object _gate = new object();

            private int _nextId = 1;
            private int _outstanding;
            private long _links;

            /// <summary>Полные пути каталогов по id (нужны, чтобы прочитать следующий уровень).</summary>
            private readonly List<string> _paths = new List<string> { "" };

            public Reader(ScanProgress prog, IWalkSink sink, string rootPath)
            {
                _prog = prog;
                _sink = sink;
                _rootPath = rootPath;
                _paths[0] = rootPath;
            }

            public void Run(ref long skippedLinks)
            {
                int workers = WorkerCount();
                var threads = new Thread[workers];

                Enqueue(new Job { Id = 0, Path = _rootPath });

                for (int t = 0; t < workers; t++)
                {
                    threads[t] = new Thread(Worker) { IsBackground = true, Name = "WinDiskMap-read-" + t };
                    threads[t].Start();
                }

                Drain();

                for (int t = 0; t < threads.Length; t++) threads[t].Join();
                skippedLinks = Interlocked.Read(ref _links);
            }

            private static int WorkerCount()
            {
                int cpus = Environment.ProcessorCount;
                int workers = cpus / 2;
                if (workers < 2) workers = 2;
                if (workers > 8) workers = 8;
                return workers;
            }

            private void Enqueue(Job job)
            {
                lock (_gate)
                {
                    _outstanding++;
                    _jobs.Enqueue(job);
                }
                _signal.Set();
            }

            private void Worker()
            {
                while (true)
                {
                    Job job;
                    bool idle;
                    lock (_gate)
                    {
                        if (_outstanding == 0) return;
                        idle = _jobs.Count == 0;
                        job = idle ? default(Job) : _jobs.Dequeue();
                    }

                    if (idle)
                    {
                        _signal.WaitOne(10);
                        continue;
                    }

                    Win32Entry[] entries;
                    long links;
                    bool ok = Win32Find.TryReadDirectory(job.Path, out entries, out links);
                    if (links > 0) Interlocked.Add(ref _links, links);
                    if (!ok || entries == null) entries = EmptyEntries;

                    lock (_gate)
                    {
                        _done.Enqueue(new Chunk { Id = job.Id, Entries = entries });
                    }
                    _signal.Set();
                }
            }

            /// <summary>Применение прочитанных каталогов к приёмнику (поток-сборщик).</summary>
            private void Drain()
            {
                var stack = new Stack<Job>();

                while (true)
                {
                    Chunk chunk;
                    chunk.Id = -1;
                    chunk.Entries = null;
                    bool have = false;

                    lock (_gate)
                    {
                        if (_done.Count > 0)
                        {
                            chunk = _done.Dequeue();
                            have = true;
                        }
                        else if (_outstanding == 0)
                        {
                            return;
                        }
                    }

                    if (!have)
                    {
                        _signal.WaitOne(5);
                        continue;
                    }

                    for (int i = 0; i < chunk.Entries.Length; i++)
                    {
                        Win32Entry e = chunk.Entries[i];
                        string full = _paths[chunk.Id] + "\\" + e.Name;
                        if (e.IsDir)
                        {
                            int id = _nextId++;
                            _sink.Dir(id, chunk.Id, e.Name, e.IsReparse);
                            PutPath(id, full);
                            stack.Push(new Job { Id = id, Path = LongPath(full) });
                        }
                        else
                        {
                            int id = _nextId++;
                            _sink.File(id, chunk.Id, e.Name, e.Size);
                        }
                    }

                    while (stack.Count > 0)
                        Enqueue(stack.Pop());

                    lock (_gate) _outstanding--;
                }
            }

            private void PutPath(int id, string path)
            {
                while (_paths.Count <= id) _paths.Add(null);
                _paths[id] = path;
            }
        }

        private static readonly Win32Entry[] EmptyEntries = new Win32Entry[0];

        internal static string LongPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }
    }
}
