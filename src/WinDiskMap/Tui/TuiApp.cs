using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using WinDiskMap.Core;

namespace WinDiskMap.Tui
{
    internal enum SortMode { Size, Name }
    internal enum FilterMode { Folders, Files, Temp, Dumps }

    /// <summary>
    /// Интерактивный просмотр дерева. Работает в двух режимах:
    ///  - идёт сканирование: дерево пополняется по мере обхода каталогов (MFT-путь — пока
    ///    экран «Идёт сканирование», дерево появляется в конце, скан быстрый);
    ///  - сканирование завершено: обычное взаимодействие.
    /// Мышь: колесо — прокрутка дерева, левый клик — установка курсора (SGR mouse mode).
    /// </summary>
    internal sealed class TuiApp
    {
        private const string ColorReset = "\x1b[0m";
        private const string ColorDim = "\x1b[90m";
        private const string ColorHeader = "\x1b[7m";
        private const string ColorFolder = "\x1b[1;36m";
        private const string ColorFile = "\x1b[37m";
        private const string ColorTemp = "\x1b[33m";
        private const string ColorDump = "\x1b[35m";

        private struct Row
        {
            public Node Node;       // для строк-папок; null для файлов и заглушек
            public FileRow File;    // для файлных строк; null для папок/заглушек
            public bool IsDir;
            public bool Hint;       // строка-заглушка "..." для пустых/скрытых фильтром папок
            public int Depth;
            public string Prefix;
            public string Name;
            public string FullPath;
            public long Size;
            public Category Category;
        }

        private readonly ScanSession _s;
        private readonly List<Row> _rows = new List<Row>();
        private Node _root;                 // кэш дерева для рендера (обновляется в RebuildRows)
        private int _cursor;
        private int _top;
        private SortMode _sort = SortMode.Size;
        private FilterMode _filter = FilterMode.Folders;
        private string _status = "";
        private bool _running = true;
        private bool _doneHandled;
        private long _filesDone = -1;       // счётчики, закэшированные после завершения скана

        public TuiApp(ScanSession session)
        {
            _s = session;
        }

        public void Run()
        {
            Console.Write("\x1b[?25l");
            EnableMouse(true);
            try
            {
                while (_running)
                {
                    UpdateState();
                    RebuildRows();
                    Render();

                    if (!_s.Done)
                    {
                        PollInput();
                        Thread.Sleep(90);
                    }
                    else
                    {
                        ConsoleKeyInfo key;
                        try
                        {
                            key = Console.ReadKey(true);
                        }
                        catch (InvalidOperationException)
                        {
                            _running = false; // ввод перенаправлен — консоли нет
                            break;
                        }
                        HandleInput(key);
                    }
                }
            }
            finally
            {
                EnableMouse(false);
                Console.Write("\x1b[?25h" + ColorReset);
            }
        }

        // ---------- Состояние (вызывается до рендера) ----------

        private void UpdateState()
        {
            if (!_s.Done || _doneHandled) return;
            _doneHandled = true;

            if (_s.Error != null)
            {
                _status = Loc.T("Ошибка сканирования: ", "Scan error: ") + _s.Error;
                return;
            }

            lock (_s.Sync)
            {
                if (_s.Root != null)
                {
                    _filesDone = CountFiles(_s.Root);
                }
            }

            string note = _s.Progress.Note;
            _status = Loc.T("Сканирование завершено", "Scan complete")
                      + (string.IsNullOrEmpty(note) ? "" : " │ " + note);
        }

        // ---------- Ввод ----------

        private void PollInput()
        {
            try
            {
                if (Console.KeyAvailable) HandleInput(Console.ReadKey(true));
            }
            catch (InvalidOperationException)
            {
                _running = false; // нет консоли
            }
        }

        private void HandleInput(ConsoleKeyInfo key)
        {
            // Любая клавиша при ошибке и отсутствии дерева — выход.
            if (_s.Error != null && _s.Root == null)
            {
                _running = false;
                return;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                // ESC может быть началом VT-последовательности (мышь): даём ей прийти.
                if (!Console.KeyAvailable) Thread.Sleep(15);
                if (Console.KeyAvailable)
                {
                    ReadSequence();
                    return;
                }
                _running = false; // одиночный Esc — выход
                return;
            }

            HandleKey(key);
        }

        /// <summary>Чтение хвоста escape-последовательности и разбор SGR-мыши.</summary>
        private void ReadSequence()
        {
            var sb = new StringBuilder(32);
            try
            {
                while (Console.KeyAvailable && sb.Length < 64)
                    sb.Append(Console.ReadKey(true).KeyChar);
            }
            catch (InvalidOperationException) { return; }
            ParseMouse(sb.ToString());
        }

        private void ParseMouse(string seq)
        {
            // Формат: "[<<btn>;<x>;<y>M" (нажатие/колесо) или "...m" (отпускание).
            if (seq.Length < 5 || seq[0] != '[' || seq[1] != '<') return;

            int i = 2;
            if (!ReadInt(seq, ref i, out int btn)) return;
            if (i >= seq.Length || seq[i] != ';') return;
            i++;
            if (!ReadInt(seq, ref i, out int x)) return;
            if (i >= seq.Length || seq[i] != ';') return;
            i++;
            if (!ReadInt(seq, ref i, out int y)) return;
            if (i >= seq.Length) return;
            char final = seq[i];

            if (btn == 64) { _cursor -= 3; ClampCursor(); }          // колесо вверх
            else if (btn == 65) { _cursor += 3; ClampCursor(); }     // колесо вниз
            else if (btn == 0 && final == 'M')                       // левый клик
            {
                int rowIndex = _top + (y - 2);                       // строка 1 — шапка
                if (rowIndex >= 0 && rowIndex < _rows.Count)
                {
                    _cursor = rowIndex;
                    Row row = _rows[rowIndex];
                    if (row.IsDir && !row.Node.Expanded) row.Node.Expanded = true;
                    else if (row.IsDir && row.Node.Expanded && row.Node != _root) row.Node.Expanded = false;
                    _status = "";
                }
            }
        }

        private static bool ReadInt(string s, ref int i, out int value)
        {
            value = 0;
            bool any = false;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                value = value * 10 + (s[i] - '0');
                if (value > 100000) value = 100000;
                i++;
                any = true;
            }
            return any;
        }

        private void HandleKey(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: _cursor--; break;
                case ConsoleKey.DownArrow: _cursor++; break;
                case ConsoleKey.PageUp: _cursor -= 10; break;
                case ConsoleKey.PageDown: _cursor += 10; break;
                case ConsoleKey.Home: _cursor = 0; break;
                case ConsoleKey.End: _cursor = _rows.Count - 1; break;

                case ConsoleKey.LeftArrow:
                    {
                        Row row = CurrentRow();
                        if (row.IsDir && row.Node.Expanded && row.Node != _root)
                        {
                            row.Node.Expanded = false;
                            _status = Loc.T("Свёрнута: ", "Collapsed: ") + row.Name;
                        }
                        else if (row.IsDir && row.Node.Parent != null)
                        {
                            _cursor = FindRowByNode(row.Node.Parent);
                            _status = "";
                        }
                    }
                    break;

                case ConsoleKey.RightArrow:
                    {
                        Row row = CurrentRow();
                        if (row.IsDir && !row.Node.Expanded)
                        {
                            row.Node.Expanded = true;
                            _status = Loc.T("Развёрнута: ", "Expanded: ") + row.Name;
                        }
                        else if (row.IsDir)
                        {
                            int child = FindFirstChildRow(_cursor);
                            if (child >= 0) _cursor = child;
                        }
                    }
                    break;

                case ConsoleKey.Enter:
                    {
                        Row row = CurrentRow();
                        if (row.IsDir && row.Node != _root)
                        {
                            row.Node.Expanded = !row.Node.Expanded;
                            _status = "";
                        }
                    }
                    break;

                case ConsoleKey.F:
                    _filter = (FilterMode)(((int)_filter + 1) % 4);
                    _status = Loc.T("Фильтр: ", "Filter: ") + FilterName(_filter);
                    break;

                case ConsoleKey.S:
                    _sort = _sort == SortMode.Size ? SortMode.Name : SortMode.Size;
                    _status = Loc.T("Сортировка: ", "Sort: ")
                              + (_sort == SortMode.Size
                                  ? Loc.T("по размеру", "size")
                                  : Loc.T("по имени", "name"));
                    break;

                case ConsoleKey.E:
                    ExportCsv();
                    break;

                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    _running = false;
                    break;
            }

            ClampCursor();
        }

        private void ClampCursor()
        {
            if (_rows.Count == 0) { _cursor = 0; return; }
            if (_cursor < 0) _cursor = 0;
            if (_cursor >= _rows.Count) _cursor = _rows.Count - 1;
        }

        private void ExportCsv()
        {
            Node root;
            lock (_s.Sync) root = _s.Root;
            if (root == null)
            {
                _status = Loc.T("Сканирование ещё идёт — экспорт недоступен",
                                "Scan still running — export unavailable");
                return;
            }

            string path = Path.Combine(Directory.GetCurrentDirectory(),
                "windiskmap_export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            try
            {
                // Под локом: дерево может ещё дополняться сканером.
                lock (_s.Sync)
                {
                    long rows = CsvExporter.ExportVisible(root, path);
                    _status = Loc.T("Экспортировано строк: ", "Rows exported: ")
                              + rows.ToString("N0") + " → " + path;
                }
            }
            catch (Exception ex)
            {
                _status = Loc.T("Ошибка экспорта: ", "Export error: ") + ex.Message;
            }
        }

        // ---------- Построение строк представления ----------

        private void RebuildRows()
        {
            lock (_s.Sync)
            {
                Node root = _s.Root;
                _root = root;

                // Якорь: сохраняем позицию курсора по пути — строки могут переупорядочиваться,
                // пока сканер дополняет размеры папок.
                string anchor = (_cursor >= 0 && _cursor < _rows.Count) ? _rows[_cursor].FullPath : null;

                _rows.Clear();
                if (root == null) return;

                _rows.Add(new Row
                {
                    Node = root,
                    IsDir = true,
                    Depth = 0,
                    Prefix = "",
                    Name = root.FullPath,
                    FullPath = root.FullPath,
                    Size = root.Size,
                    Category = Category.Folder
                });
                AppendChildren(root, "");

                if (anchor != null)
                {
                    for (int i = 0; i < _rows.Count; i++)
                        if (_rows[i].FullPath == anchor) { _cursor = i; break; }
                }
                ClampCursor();
            }
        }

        private void AppendChildren(Node dir, string childPrefix)
        {
            List<Node> dirs = SortedDirs(dir);
            List<FileRow> files = _filter == FilterMode.Folders ? EmptyFiles : FilteredFiles(dir);

            int total = dirs.Count + files.Count;
            if (total == 0)
            {
                // Пустая папка (или ничего не прошло фильтр): показываем заглушку.
                if (dir.Expanded)
                {
                    _rows.Add(new Row
                    {
                        Hint = true,
                        IsDir = false,
                        Depth = dir.Depth + 1,
                        Prefix = childPrefix + "└─ ",
                        Name = "...",
                        FullPath = dir.FullPath + "\\...",
                        Size = 0,
                        Category = Category.File
                    });
                }
                return;
            }

            int index = 0;
            for (int d = 0; d < dirs.Count; d++, index++)
            {
                Node child = dirs[d];
                bool isLast = index == total - 1;
                _rows.Add(new Row
                {
                    Node = child,
                    IsDir = true,
                    Depth = dir.Depth + 1,
                    Prefix = childPrefix + (isLast ? "└─ " : "├─ "),
                    Name = child.Name,
                    FullPath = child.FullPath,
                    Size = child.Size,
                    Category = Category.Folder
                });
                if (child.Expanded)
                    AppendChildren(child, childPrefix + (isLast ? "   " : "│  "));
            }

            for (int f = 0; f < files.Count; f++, index++)
            {
                FileRow file = files[f];
                bool isLast = index == total - 1;
                _rows.Add(new Row
                {
                    File = file,
                    IsDir = false,
                    Depth = dir.Depth + 1,
                    Prefix = childPrefix + (isLast ? "└─ " : "├─ "),
                    Name = file.Name,
                    FullPath = file.FullPath,
                    Size = file.Size,
                    Category = file.Category
                });
            }
        }

        private static readonly List<FileRow> EmptyFiles = new List<FileRow>();

        private List<FileRow> FilteredFiles(Node dir)
        {
            List<FileRow> all = dir.GetFileRows();
            if (_filter == FilterMode.Files) return all;
            Category want = _filter == FilterMode.Temp ? Category.Temp : Category.Dump;
            var result = new List<FileRow>();
            for (int i = 0; i < all.Count; i++)
                if (all[i].Category == want) result.Add(all[i]);
            return result;
        }

        private List<Node> SortedDirs(Node dir)
        {
            var list = new List<Node>(dir.Dirs);
            if (_sort == SortMode.Size)
                list.Sort((a, b) => b.Size.CompareTo(a.Size));
            else
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private Row CurrentRow()
        {
            if (_rows.Count == 0) return default(Row);
            int i = Math.Max(0, Math.Min(_cursor, _rows.Count - 1));
            return _rows[i];
        }

        private int FindRowByNode(Node node)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Node == node) return i;
            return _cursor;
        }

        private int FindFirstChildRow(int dirRowIndex)
        {
            Node node = _rows[dirRowIndex].Node;
            int depth = _rows[dirRowIndex].Depth;
            for (int i = dirRowIndex + 1; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (r.Depth <= depth) return -1;
                if ((r.Node != null && r.Node.Parent == node) || (r.File != null && r.Depth == depth + 1))
                    return i;
                if (r.Hint && r.Depth == depth + 1) return i;
            }
            return -1;
        }

        private static long CountFiles(Node dir)
        {
            long n = dir.FileCount;
            for (int i = 0; i < dir.Dirs.Count; i++) n += CountFiles(dir.Dirs[i]);
            return n;
        }

        // ---------- Рендеринг ----------

        private void Render()
        {
            int width = SafeWidth();
            int height = SafeHeight();

            if (_root == null)
            {
                RenderScanning(width, height);
                return;
            }

            int listHeight = Math.Max(1, height - 3);
            if (_cursor < _top) _top = _cursor;
            if (_cursor >= _top + listHeight) _top = _cursor - listHeight + 1;
            int maxTop = Math.Max(0, _rows.Count - listHeight);
            if (_top > maxTop) _top = maxTop;
            if (_top < 0) _top = 0;

            var sb = new StringBuilder(8192);
            sb.Append("\x1b[H");

            AppendHeaderLine(sb, width);
            sb.Append("\x1b[K\r\n");

            int end = Math.Min(_rows.Count, _top + listHeight);
            for (int i = _top; i < end; i++)
            {
                AppendRowLine(sb, _rows[i], i == _cursor, width);
                sb.Append(ColorReset);
                sb.Append("\x1b[K\r\n");
            }

            AppendFooterLines(sb, width);
            sb.Append("\x1b[J");
            Console.Write(sb.ToString());
        }

        private void AppendHeaderLine(StringBuilder sb, int width)
        {
            bool done = _s.Done;
            string files = done && _filesDone >= 0
                ? Format.Number(_filesDone)
                : Format.Number(_s.Progress.Files);

            string text = " WinDiskMap │ " + _root.FullPath
                + " │ " + (done ? Loc.T("Итого", "Total") : Loc.T("Идёт сканирование", "Scanning"))
                + ": " + Format.Bytes(_root.Size)
                + " │ " + Loc.T("Файлов", "Files") + ": " + files
                + " │ " + _s.Mode + " ";
            sb.Append(ColorHeader);
            sb.Append(PadOrTruncate(text, width));
        }

        private void AppendRowLine(StringBuilder sb, Row row, bool cursor, int width)
        {
            string meta;
            if (row.Hint)
            {
                meta = "";
            }
            else
            {
                meta = "  " + Format.Bytes(row.Size) + " " + Format.Percent(row.Size, _root.Size);
            }

            string color = row.Hint ? ColorDim : ColorFor(row.Category);

            int nameRoom = width - 1 - row.Prefix.Length - meta.Length;
            if (nameRoom < 4) nameRoom = 4;
            string name = row.Name.Length > nameRoom
                ? row.Name.Substring(0, nameRoom - 1) + "…"
                : row.Name;

            int pad = width - 1 - row.Prefix.Length - name.Length - meta.Length;
            if (pad < 1) pad = 1;

            if (cursor) sb.Append(ColorHeader);
            sb.Append(row.Prefix);
            sb.Append(color);
            sb.Append(name);
            sb.Append(ColorReset);
            if (cursor) sb.Append(ColorHeader);
            sb.Append(new string(' ', pad));
            if (meta.Length > 0)
            {
                sb.Append(ColorDim);
                sb.Append(meta);
            }
        }

        private void AppendFooterLines(StringBuilder sb, int width)
        {
            string line1 = " " + (_cursor + 1).ToString("N0") + "/" + _rows.Count.ToString("N0")
                + " │ " + Loc.T("Фильтр", "Filter") + ": " + FilterName(_filter)
                + " │ " + Loc.T("Сортировка", "Sort") + ": " + (_sort == SortMode.Size
                    ? Loc.T("размер", "size")
                    : Loc.T("имя", "name"))
                + (_status.Length > 0 ? " │ " + _status : "");
            sb.Append(ColorDim);
            sb.Append(PadOrTruncate(line1, width));
            sb.Append("\x1b[K\r\n");
            sb.Append(PadOrTruncate(Loc.KeysHint, width));
            sb.Append("\x1b[K");
        }

        private void RenderScanning(int width, int height)
        {
            var sb = new StringBuilder(2048);
            sb.Append("\x1b[H");
            sb.Append(ColorHeader);
            sb.Append(PadOrTruncate(
                _s.Error != null
                    ? " WinDiskMap "
                    : " WinDiskMap — " + Loc.T("Идёт сканирование", "Scanning") + " ",
                width));
            sb.Append(ColorReset);
            sb.Append("\x1b[K\r\n\r\n");

            if (_s.Error != null)
            {
                sb.Append(ColorReset);
                sb.Append(Loc.T("Ошибка сканирования: ", "Scan error: "));
                sb.Append(_s.Error);
                sb.Append("\x1b[K\r\n\r\n");
                sb.Append(ColorDim);
                sb.Append(Loc.T("Нажмите любую клавишу для выхода", "Press any key to exit"));
                sb.Append("\x1b[K");
                sb.Append("\x1b[J");
                Console.Write(sb.ToString());
                return;
            }

            char[] spin = { '|', '/', '-', '\\' };
            int tick = Environment.TickCount / 120 % 4;
            sb.Append("  ");
            sb.Append(spin[tick]);
            sb.Append("  ");
            sb.Append(Loc.T("Идёт сканирование…", "Scanning…"));
            sb.Append("\x1b[K\r\n\r\n");

            ScanProgress p = _s.Progress;
            sb.Append("  " + Loc.T("Файлов", "Files") + ":  " + Format.Number(p.Files));
            sb.Append("\x1b[K\r\n");
            sb.Append("  " + Loc.T("Папок", "Folders") + ":   " + Format.Number(p.Dirs));
            sb.Append("\x1b[K\r\n");
            sb.Append("  " + Loc.T("Объём", "Size") + ":   " + Format.Bytes(p.Bytes));
            sb.Append("\x1b[K\r\n");
            if (!string.IsNullOrEmpty(_s.Mode))
            {
                sb.Append(ColorDim);
                sb.Append("  " + _s.Mode);
                sb.Append(ColorReset);
                sb.Append("\x1b[K");
            }
            else
            {
                sb.Append("\x1b[K");
            }
            sb.Append("\x1b[J");
            Console.Write(sb.ToString());
        }

        private static string ColorFor(Category c)
        {
            switch (c)
            {
                case Category.Folder: return ColorFolder;
                case Category.Temp: return ColorTemp;
                case Category.Dump: return ColorDump;
                default: return ColorFile;
            }
        }

        private static string FilterName(FilterMode f)
        {
            switch (f)
            {
                case FilterMode.Files:
                    return Loc.T("папки+файлы", "folders+files");
                case FilterMode.Temp:
                    return "tmp/temp";
                case FilterMode.Dumps:
                    return Loc.T("дампы", "dumps");
                default:
                    return Loc.T("только папки", "folders only");
            }
        }

        private static string PadOrTruncate(string text, int width)
        {
            if (width <= 0) return text;
            if (text.Length > width) return text.Substring(0, Math.Max(0, width - 1)) + "…";
            return text + new string(' ', width - text.Length);
        }

        private static int SafeWidth()
        {
            try { return Math.Max(40, Console.WindowWidth); }
            catch { return 120; }
        }

        private static int SafeHeight()
        {
            try { return Math.Max(8, Console.WindowHeight); }
            catch { return 30; }
        }

        private static void EnableMouse(bool on)
        {
            try { Console.Write(on ? "\x1b[?1000h\x1b[?1006h" : "\x1b[?1006l\x1b[?1000l"); }
            catch { }
        }

        // ---------- Экран прогресса (режим --csv, без TUI) ----------

        public static void DrawProgress(ScanProgress prog, int spinnerTick)
        {
            int width = SafeWidth();
            var sb = new StringBuilder(1024);
            sb.Append("\x1b[H");
            sb.Append(ColorHeader);
            sb.Append(PadOrTruncate(
                " WinDiskMap — " + Loc.T("Идёт сканирование", "Scanning") + " ", width));
            sb.Append(ColorReset);
            sb.Append("\x1b[K\r\n\r\n");

            char[] spin = { '|', '/', '-', '\\' };
            sb.Append("  ");
            sb.Append(spin[spinnerTick % 4]);
            sb.Append("  ");
            sb.Append(prog.Phase);
            sb.Append("\x1b[K\r\n\r\n");
            sb.Append("  " + Loc.T("Файлов", "Files") + ":  " + Format.Number(prog.Files));
            sb.Append("\x1b[K\r\n");
            sb.Append("  " + Loc.T("Папок", "Folders") + ":   " + Format.Number(prog.Dirs));
            sb.Append("\x1b[K\r\n");
            sb.Append("  " + Loc.T("Объём", "Size") + ":   " + Format.Bytes(prog.Bytes));
            sb.Append("\x1b[K");
            sb.Append("\x1b[J");
            Console.Write(sb.ToString());
        }
    }
}
