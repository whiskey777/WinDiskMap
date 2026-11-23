using System;
using System.Collections.Generic;
using System.IO;

namespace WinDiskMap.Core
{
    /// <summary>
    /// Построение живого дерева Node из потока записей (dir/file) по id-ссылкам.
    /// Используется и локальным обходом (NodeSink), и удалённым сканером (NDJSON
    /// с агента): одни и те же правила построения — одинаковые цифры.
    /// </summary>
    public sealed class TreeBuilder
    {
        private readonly ScanSession _session;
        private readonly ScanProgress _prog;
        private readonly bool _countProgress;
        private Node _root;
        private readonly List<Node> _byId = new List<Node> { null };

        /// <param name="countProgress">Обновлять ScanProgress (локальный режим — да;
        /// удалённый берёт счётчики из потока агента — тоже да, для живого прогресса).</param>
        public TreeBuilder(ScanSession session, ScanProgress prog, bool countProgress = true)
        {
            _session = session;
            _prog = prog;
            _countProgress = countProgress;
        }

        public Node Root { get { return _root; } }

        /// <summary>Создаёт корень поддерева. id корня — 0.</summary>
        public void Begin(string displayRoot, bool reparse = false,
            long createdUtc = 0, long lastWriteUtc = 0, long allocated = 0, uint attributes = 0)
        {
            string rootName = Path.GetFileName(displayRoot);
            if (string.IsNullOrEmpty(rootName)) rootName = displayRoot;

            _root = new Node(null)
            {
                Name = rootName,
                FullPath = (displayRoot ?? "").TrimEnd('\\'),
                Depth = 0,
                IsDir = true,
                IsReparse = reparse,
                Attributes = attributes,
                Allocated = allocated,
                OwnAllocated = allocated,
                CreatedUtc = createdUtc,
                LastWriteUtc = lastWriteUtc,
                Parent = null,
                Expanded = true
            };
            _byId[0] = _root;
            lock (_session.Sync) _session.Root = _root;
        }

        public void Dir(int id, int parent, string name, bool reparse,
            long createdUtc = 0, long lastWriteUtc = 0, long allocated = 0, uint attributes = 0)
        {
            Node parentNode = Resolve(parent);
            var child = new Node(null)
            {
                Name = name,
                FullPath = parentNode.FullPath + "\\" + name,
                Depth = parentNode.Depth + 1,
                IsDir = true,
                IsReparse = reparse,
                Attributes = attributes,
                Allocated = allocated,
                OwnAllocated = allocated,
                CreatedUtc = createdUtc,
                LastWriteUtc = lastWriteUtc,
                Parent = parentNode
            };
            lock (_session.Sync)
            {
                parentNode.Dirs.Add(child);
                Put(id, child);
            }
            if (_countProgress) _prog.AddDir();
        }

        public void File(int id, int parent, string name, long size,
            long createdUtc = 0, long lastWriteUtc = 0, long allocated = 0, uint attributes = 0)
        {
            Node parentNode = Resolve(parent);
            Put(id, null);
            lock (_session.Sync) parentNode.AddLiveFile(name, size, createdUtc, lastWriteUtc,
                                                        allocated, attributes);
            if (_countProgress) _prog.AddFile(size);
        }

        private Node Resolve(int parent)
        {
            if (parent < 0) return _root;
            if (parent >= _byId.Count) throw new InvalidDataException("parent id out of range: " + parent);
            Node n = _byId[parent];
            if (n == null) throw new InvalidDataException("parent id is not a directory: " + parent);
            return n;
        }

        private void Put(int id, Node node)
        {
            while (_byId.Count <= id) _byId.Add(null);
            _byId[id] = node;
        }
    }
}
