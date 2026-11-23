using System;
using System.Collections.Generic;

namespace DiskMap.Core
{
    /// <summary>Строка файла, «материализованная» в строки списка при раскрытии папки.</summary>
    public sealed class FileRow
    {
        public string Name;
        public string FullPath;
        public long Size;
        public Category Category;
    }

    /// <summary>Файл «живого» сканера: имя+размер известны сразу при обходе каталога.</summary>
    public sealed class LiveFile
    {
        public string Name;
        public long Size;
    }

    /// <summary>
    /// Узел дерева папок. Поддерживаются два источника файлов:
    ///  - FileIndices + ScanIndex (MFT-путь: индексы в плоском индексе, ленивая материализация);
    ///  - LiveFiles (обход каталогов: файлы добавляются по мере скана, дерево живое).
    /// Ленивая материализация на 5 млн файлов экономит сотни мегабайт памяти.
    /// </summary>
    public sealed class Node
    {
        public string Name;
        public string FullPath;
        public int Depth;
        public bool IsDir;

        /// <summary>Reparse-папка (junction/symlink): видна в дереве, но не входит в счёт папок (как у WizTree).</summary>
        public bool IsReparse;

        /// <summary>Итоговый размер: собственные файлы + все потомки (на момент чтения).</summary>
        public long Size;

        /// <summary>Размер только файлов, лежащих непосредственно в этой папке.</summary>
        public long OwnSize;

        public Node Parent;
        public List<Node> Dirs;

        /// <summary>Индексы файлов непосредственно в папке (MFT-путь).</summary>
        public List<int> FileIndices;

        /// <summary>Файлы живого сканера (путь обхода каталогов); null для MFT-пути.</summary>
        public List<LiveFile> LiveFiles;

        public bool Expanded;

        private List<FileRow> _filesCache;
        private readonly ScanIndex _index;

        public Node(ScanIndex index)
        {
            _index = index;
            Dirs = new List<Node>();
            FileIndices = new List<int>();
        }

        public int FileCount
        {
            get { return FileIndices.Count + (LiveFiles != null ? LiveFiles.Count : 0); }
        }

        /// <summary>
        /// Добавление файла живым сканером. Вызывается под lock(ScanSession.Sync):
        /// обновляет кэш строк и прибавляет размер к цепочке предков, поэтому Tree.Size
        /// корректен на любом этапе сканирования (без пост-порядочной агрегации).
        /// </summary>
        public void AddLiveFile(string name, long size)
        {
            if (LiveFiles == null) LiveFiles = new List<LiveFile>();
            LiveFiles.Add(new LiveFile { Name = name, Size = size });
            _filesCache = null;
            OwnSize += size;
            for (Node p = this; p != null; p = p.Parent)
                p.Size += size;
        }

        /// <summary>Материализация файлов папки, отсортированных по убыванию размера. Кэшируется.</summary>
        public List<FileRow> GetFileRows()
        {
            if (_filesCache != null) return _filesCache;

            int count = FileCount;
            var rows = new List<FileRow>(count);

            if (LiveFiles != null)
            {
                for (int i = 0; i < LiveFiles.Count; i++)
                {
                    LiveFile f = LiveFiles[i];
                    string full = FullPath + "\\" + f.Name;
                    rows.Add(new FileRow
                    {
                        Name = f.Name,
                        FullPath = full,
                        Size = f.Size,
                        Category = CategoryClassifier.ClassifyFile(f.Name, full)
                    });
                }
            }
            else
            {
                for (int i = 0; i < FileIndices.Count; i++)
                {
                    int idx = FileIndices[i];
                    string name = _index.Names[idx];
                    string full = FullPath + "\\" + name;
                    rows.Add(new FileRow
                    {
                        Name = name,
                        FullPath = full,
                        Size = _index.Sizes[idx],
                        Category = CategoryClassifier.ClassifyFile(name, full)
                    });
                }
            }

            rows.Sort((a, b) => b.Size.CompareTo(a.Size));
            _filesCache = rows;
            return rows;
        }
    }
}
