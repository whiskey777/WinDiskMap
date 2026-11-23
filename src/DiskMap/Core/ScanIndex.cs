using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DiskMap.Core
{
    /// <summary>
    /// Плоский индекс просканированных объектов: массивы вместо объектов на каждый файл
    /// (5 млн файлов ≈ 100 МБ вместо гигабайтов PSCustomObject-подобных структур).
    /// Источник данных любой: MFT-парсер или обычный обход каталогов.
    /// </summary>
    public sealed class ScanIndex
    {
        public const int RootRecord = 5; // в MFT корень тома — запись 5

        public readonly List<string> Names = new List<string>(1 << 16);
        public readonly List<int> Parents = new List<int>(1 << 16);
        public readonly List<long> Sizes = new List<long>(1 << 16);   // только для файлов
        public readonly List<bool> IsDir = new List<bool>(1 << 16);
        public readonly List<bool> IsReparse = new List<bool>(1 << 16);

        public int RootIndex;
        public long TotalFiles;
        public long TotalDirs;
        public long TotalReparseDirs;
        public long TotalBytes;

        private Dictionary<int, List<int>> _children;

        public int Add(string name, int parent, long size, bool isDir, bool isReparse)
        {
            int i = Names.Count;
            Names.Add(name);
            Parents.Add(parent);
            Sizes.Add(size);
            IsDir.Add(isDir);
            IsReparse.Add(isReparse);
            // Reparse-папки (junction/symlink) в счётчик папок не входят — как у WizTree;
            // в дереве они остаются (лист с нулевым размером).
            if (isDir) { if (isReparse) TotalReparseDirs++; else TotalDirs++; }
            else { TotalFiles++; TotalBytes += size; }
            return i;
        }

        /// <summary>Группировка детей по родителю (вызывается один раз после скана).</summary>
        public void BuildChildMap()
        {
            _children = new Dictionary<int, List<int>>(TotalDirs > int.MaxValue ? 0 : (int)Math.Min(TotalDirs + 1, 1 << 20));
            for (int i = 0; i < Names.Count; i++)
            {
                int p = Parents[i];
                if (p < 0) continue;
                List<int> list;
                if (!_children.TryGetValue(p, out list))
                {
                    list = new List<int>();
                    _children[p] = list;
                }
                list.Add(i);
            }
        }

        public List<int> ChildrenOf(int index)
        {
            List<int> list;
            if (_children != null && _children.TryGetValue(index, out list)) return list;
            return Empty;
        }

        private static readonly List<int> Empty = new List<int>();

        /// <summary>
        /// Поиск записи каталога по полному пути (например D:\Games\Mods), начиная от корня тома.
        /// Возвращает индекс или -1.
        /// </summary>
        public int FindDirectory(string fullPath)
        {
            string normalized = fullPath.TrimEnd('\\');
            string root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root)) return -1;

            int current = RootIndex;
            string rest = normalized.Substring(root.Length).Trim('\\');
            if (rest.Length == 0) return current;

            string[] parts = rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (int p = 0; p < parts.Length; p++)
            {
                List<int> kids = ChildrenOf(current);
                int found = -1;
                for (int i = 0; i < kids.Count; i++)
                {
                    int k = kids[i];
                    if (!IsDir[k]) continue;
                    if (string.Equals(Names[k], parts[p], StringComparison.OrdinalIgnoreCase))
                    {
                        found = k;
                        break;
                    }
                }
                if (found < 0) return -1;
                current = found;
            }
            return current;
        }

        /// <summary>
        /// Техническая сводка для разбора неудачи FindDirectory: сколько записей в индексе,
        /// какие дети у корня, где встречаются записи с искомым именем и цела ли их цепочка родителей.
        /// </summary>
        public string DescribeLookupFailure(string fullPath)
        {
            var sb = new StringBuilder(512);
            sb.Append("[lookup: entries=").Append(Names.Count)
              .Append(" dirs=").Append(TotalDirs)
              .Append(" files=").Append(TotalFiles);

            long orphans = 0;
            for (int i = 0; i < Parents.Count; i++)
                if (i != RootIndex && Parents[i] < 0) orphans++;
            sb.Append(" orphans=").Append(orphans);

            sb.Append(" rootIdx=").Append(RootIndex)
              .Append(" rootName=").Append(Quote(RootIndex >= 0 && RootIndex < Names.Count ? Names[RootIndex] : "?"));

            List<int> kids = ChildrenOf(RootIndex);
            sb.Append(" rootChildren=").Append(kids.Count)
              .Append(" rootKidNames=");
            for (int i = 0; i < kids.Count && i < 12; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(Names[kids[i]])).Append(IsDir[kids[i]] ? "/" : "");
            }
            if (kids.Count > 12) sb.Append(",…");

            // Искомый последний компонент пути: где встречается и как ведёт цепочка к корню.
            string normalized = fullPath.TrimEnd('\\');
            string last = normalized;
            int slash = normalized.LastIndexOf('\\');
            if (slash >= 0 && slash + 1 < normalized.Length) last = normalized.Substring(slash + 1);
            sb.Append(" find=").Append(Quote(last));

            int total = 0;
            for (int i = 0; i < Names.Count; i++)
                if (IsDir[i] && string.Equals(Names[i], last, StringComparison.OrdinalIgnoreCase)) total++;
            sb.Append(" dirMatches=").Append(total);

            for (int i = 0, shown = 0; i < Names.Count && shown < 3; i++)
            {
                if (!IsDir[i] || !string.Equals(Names[i], last, StringComparison.OrdinalIgnoreCase)) continue;
                shown++;
                int parent = Parents[i];
                sb.Append(" match").Append(shown).Append("=[idx=").Append(i)
                  .Append(" parent=").Append(parent)
                  .Append(" parentName=").Append(Quote(parent >= 0 && parent < Names.Count ? Names[parent] : "-"));

                int cur = parent, steps = 0;
                bool reachedRoot = false;
                while (cur >= 0 && steps++ < 64)
                {
                    if (cur == RootIndex) { reachedRoot = true; break; }
                    cur = Parents[cur];
                }
                sb.Append(" chain=").Append(reachedRoot ? "to-root" : "broken");

                // Два верхних элемента цепочки: «... < 'Users' < 'Windows'».
                int up = parent;
                for (int d = 0; d < 2 && up >= 0 && up < Names.Count; d++)
                {
                    sb.Append(" <").Append(Quote(Names[up]));
                    up = Parents[up];
                }
                sb.Append(']');
            }
            if (total == 0) sb.Append(" dirMatches=none");
            sb.Append(']');
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"-\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char ch in s)
                sb.Append(ch >= 0x20 && ch < 0x7F ? ch : '?');
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Построение дерева Node от целевого каталога вниз с агрегацией размеров снизу вверх.
        /// </summary>
        public Node BuildTree(int rootIndex, string displayPath)
        {
            var root = new Node(this)
            {
                Name = string.IsNullOrEmpty(displayPath) ? Names[rootIndex] : displayPath,
                FullPath = displayPath.TrimEnd('\\'),
                Depth = 0,
                IsDir = true,
                Parent = null,
                Expanded = true
            };

            BuildNode(root, rootIndex);

            // Агрегация снизу вверх: размер папки = собственные файлы + сумма потомков.
            AggregateSizes(root);

            return root;
        }

        /// <summary>Пост-порядочный обход: сначала дети, потом родитель. Глубина дерева мала (&lt;100).</summary>
        private long AggregateSizes(Node node)
        {
            long sum = node.OwnSize;
            for (int i = 0; i < node.Dirs.Count; i++)
                sum += AggregateSizes(node.Dirs[i]);
            node.Size = sum;
            return sum;
        }

        private void BuildNode(Node node, int index)
        {
            List<int> kids = ChildrenOf(index);
            for (int i = 0; i < kids.Count; i++)
            {
                int k = kids[i];
                if (IsDir[k])
                {
                    var child = new Node(this)
                    {
                        Name = Names[k],
                        FullPath = node.FullPath + "\\" + Names[k],
                        Depth = node.Depth + 1,
                        IsDir = true,
                        IsReparse = IsReparse[k],
                        Parent = node
                    };
                    node.Dirs.Add(child);
                    BuildNode(child, k);
                }
                else
                {
                    node.FileIndices.Add(k);
                    node.OwnSize += Sizes[k];
                }
            }
        }
    }
}
