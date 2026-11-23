using System;
using System.Collections.Generic;

namespace WinDiskMap.Core
{
    /// <summary>Строка файла, «материализованная» в строки списка при раскрытии папки.</summary>
    public sealed class FileRow
    {
        public string Name;
        public string FullPath;
        public long Size;
        /// <summary>«На диске»: реально занятые байты (кластеры).</summary>
        public long Allocated;
        /// <summary>FILE_ATTRIBUTE_* битмаска.</summary>
        public uint Attributes;
        public Category Category;

        /// <summary>FILETIME (UTC). 0 — дата неизвестна (например, старый NDJSON-агент).</summary>
        public long CreatedUtc;
        public long LastWriteUtc;
    }

    /// <summary>Файл «живого» сканера: имя+размер+даты известны сразу при обходе каталога.</summary>
    public sealed class LiveFile
    {
        public string Name;
        public long Size;
        public long Allocated;
        public uint Attributes;
        public long CreatedUtc;
        public long LastWriteUtc;
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

        /// <summary>«На диске» по всему поддереву (аналог Size, но в занятых кластерах).</summary>
        public long Allocated;

        /// <summary>«На диске» только файлы непосредственно в этой папке.</summary>
        public long OwnAllocated;

        /// <summary>FILE_ATTRIBUTE_* у каталога (у MFT-пути — из $STANDARD_INFORMATION).</summary>
        public uint Attributes;

        /// <summary>Счётчики поддерева для CSV (FillSubtreeCounts вызывается перед экспортом).</summary>
        public long SubtreeFiles;
        public long SubtreeDirs;

        /// <summary>Даты каталога в FILETIME (UTC). 0 — неизвестно.</summary>
        public long CreatedUtc;
        public long LastWriteUtc;

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
        public void AddLiveFile(string name, long size, long createdUtc = 0, long lastWriteUtc = 0,
            long allocated = 0, uint attributes = 0)
        {
            if (LiveFiles == null) LiveFiles = new List<LiveFile>();
            LiveFiles.Add(new LiveFile
            {
                Name = name,
                Size = size,
                Allocated = allocated,
                Attributes = attributes,
                CreatedUtc = createdUtc,
                LastWriteUtc = lastWriteUtc
            });
            _filesCache = null;
            OwnSize += size;
            OwnAllocated += allocated;
            for (Node p = this; p != null; p = p.Parent)
            {
                p.Size += size;
                p.Allocated += allocated;
            }
        }

        /// <summary>
        /// Удаление файла из папки после реального удаления с диска. Размеры предков
        /// caller'ом не трогаются — он вычитает одну и ту же величину по всей цепочке.
        /// </summary>
        public bool RemoveFileByName(string name, long size)
        {
            if (LiveFiles != null)
            {
                for (int i = 0; i < LiveFiles.Count; i++)
                {
                    if (!string.Equals(LiveFiles[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    OwnAllocated -= LiveFiles[i].Allocated;
                    LiveFiles.RemoveAt(i);
                    _filesCache = null;
                    OwnSize -= size;
                    return true;
                }
                return false;
            }

            for (int i = 0; i < FileIndices.Count; i++)
            {
                int idx = FileIndices[i];
                if (!string.Equals(_index.Names[idx], name, StringComparison.OrdinalIgnoreCase)) continue;
                OwnAllocated -= _index.Allocated[idx];
                FileIndices.RemoveAt(i);
                _filesCache = null;
                OwnSize -= size;
                return true;
            }
            return false;
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
                        Allocated = f.Allocated,
                        Attributes = f.Attributes,
                        Category = CategoryClassifier.ClassifyFile(f.Name, full),
                        CreatedUtc = f.CreatedUtc,
                        LastWriteUtc = f.LastWriteUtc
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
                        Allocated = _index.Allocated[idx],
                        Attributes = _index.Attributes[idx],
                        Category = CategoryClassifier.ClassifyFile(name, full),
                        CreatedUtc = _index.CreatedUtc[idx],
                        LastWriteUtc = _index.LastWriteUtc[idx]
                    });
                }
            }

            rows.Sort((a, b) => b.Size.CompareTo(a.Size));
            _filesCache = rows;
            return rows;
        }
    }
}
