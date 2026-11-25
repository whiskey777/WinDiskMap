using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        /// <summary>Версия билда из InformationalVersion (Example: 1.0.0-beta.3).</summary>
        private static readonly string VersionLabel = ReadVersion();
        private static readonly string VersionLabelTitle =
            VersionLabel.Length > 0 ? " " + VersionLabel : "";

        private static string ReadVersion()
        {
            // Чтение атрибута собственной сборки не требует catch: при отсутствии
            // InformationalVersion fallback — четырехчисловая версия из AssemblyName.
            var asm = typeof(TuiApp).Assembly;
            var attrs = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (attrs.Length > 0)
            {
                var v = ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    int plus = v.IndexOf('+');
                    return plus > 0 ? v.Substring(0, plus) : v;
                }
            }
            var version = asm.GetName().Version;
            return version != null ? version.ToString() : "";
        }

        // Цвета берутся из активной темы; при переключении T достаточно одного
        // полного ререндера — остальной код работает с этими св-вами как раньше.
        private static string ColorReset { get { return Theme.Current.Reset; } }
        private static string ColorDim { get { return Theme.Current.Dim; } }
        private static string ColorHeader { get { return Theme.Current.Header; } }
        private static string ColorFolder { get { return Theme.Current.Folder; } }
        private static string ColorFile { get { return Theme.Current.File; } }
        private static string ColorTemp { get { return Theme.Current.Temp; } }
        private static string ColorDump { get { return Theme.Current.Dump; } }
        private static string ColorWarn { get { return Theme.Current.Warn; } }       // опасные действия
        private static string ColorSortActive { get { return Theme.Current.SortActive; } } // активная колонка
        private static string ColorKey { get { return Theme.Current.Key; } }             // клавиши в подсказках

        // Воздух между колонками. 2 пробела — достаточно для читаемости; перед
        // группой дат делаем шире, чтобы отделить «временны́е» колонки от размеров.
        private const int Gutter = 2;
        private const int GroupGap = 4;

        private struct Row
        {
            public Node Node;       // для строк-папок; null для файлов и заглушек
            public Node Parent;     // владелец файла/заглушки — нужен при удалении
            public FileRow File;    // для файлных строк; null для папок/заглушек
            public bool IsDir;
            public bool Hint;       // строка-заглушка для пустых/скрытых фильтром папок
            public bool ShowMeta;   // у <Filtered> показываем размеры скрытых файлов
            public int Depth;
            public string Prefix;
            public string Name;
            public string FullPath;
            public long Size;
            public long CreatedUtc;
            public long LastWriteUtc;
            public Category Category;
        }

        /// <summary>Набор правых колонок, подогнанный под ширину терминала.</summary>
        private struct Columns
        {
            public bool Percent;
            public bool Dates;
            public int SizeW;
            public int PctW;
            public int DateW;
        }

        private readonly ScanSession _s;
        private readonly List<Row> _rows = new List<Row>();
        private Node _root;                 // кэш дерева для рендера (обновляется в RebuildRows)
        private int _cursor;
        private int _top;
        private SortMode _sort = SortMode.Size;
        private bool _sortDesc = true;      // размер ↓ по умолчанию (как в WizTree)
        private FilterMode _filter = FilterMode.Folders;
        private string _status = "";
        private bool _running = true;
        private bool _doneHandled;
        private long _filesDone = -1;       // счётчики, закэшированные после завершения скана
        private int _lastWidth = -1;        // для авто-перерисовки при ресайзе консоли
        private int _lastHeight = -1;

        public TuiApp(ScanSession session)
        {
            _s = session;
        }

        public void Run()
        {
            Console.Write("\x1b[?25l");
            EnableMouse(true);
            // Заливаем экран фоном активной темы до первого кадра: иначе
            // незаполненные строки остались бы в цвете схемы терминала.
            ClearWithThemeBackground();
            try
            {
                while (_running)
                {
                    // Ввод опрашивается и после завершения скана: блокирующий ReadKey
                    // «замораживал» бы экран при ресайзе окна.
                    bool hadInput = false;
                    // Флаг меняется ТОЛЬКО здесь, в UI-потоке (UpdateState), поэтому
                    // переход гарантированно виден. Сырой _s.Done выставляется сканером
                    // во время Sleep() и к началу следующего прохода уже равен true —
                    // детектировать завершение по нему ненадёжно (MFT-ветка под админом).
                    bool wasDoneHandled = _doneHandled;
                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            HandleInput(Console.ReadKey(true));
                            hadInput = true;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        _running = false; // ввод перенаправлен — консоли нет
                        break;
                    }

                    UpdateState();

                    // Размер консоли меняется без нажатия клавиш — сравниваем сырые
                    // значения (SafeWidth clamp'ит минимум, поэтому здесь не подходит).
                    int rawW = RawWidth();
                    int rawH = RawHeight();
                    bool resized = rawW != _lastWidth || rawH != _lastHeight;
                    if (resized) { _lastWidth = rawW; _lastHeight = rawH; }

                    if (resized) ClearWithThemeBackground();

                    // Пока идёт скан — рисуем каждый проход (живой прогресс); после —
                    // только по действию пользователя, изменению размера или в момент
                    // завершения (иначе "<Пусто>" появится лишь после следующей клавиши).
                    bool justDone = !wasDoneHandled && _doneHandled;
                    if (resized || hadInput || !_s.Done || justDone)
                    {
                        RebuildRows();
                        Render();
                    }

                    if (!hadInput) Thread.Sleep(60);
                }
            }
            finally
            {
                EnableMouse(false);
                // Возвращаем терминалу его собственные цвета: после выхода
                // приглашение командной строки не должно остаться в нашей теме.
                Console.Write("\x1b[?25h" + Theme.HardReset);
            }
        }

        /// <summary>Полная очистка экрана с фоновым цветом активной темы.</summary>
        private static void ClearWithThemeBackground()
        {
            try
            {
                Console.Write(ColorReset + "\x1b[2J\x1b[H");
            }
            catch (IOException ex) { NoteClear(ex); }
            catch (ObjectDisposedException ex) { NoteClear(ex); }
        }

        private static void NoteClear(Exception ex)
        {
            Debug.WriteLine("WinDiskMap TUI screen clear failed: " + ex.Message);
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
                int rowIndex = _top + (y - 3);                       // строки 1-2 — шапка и заголовок колонок
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
            // T (и та же физическая клавиша в русской раскладке) — цикл тем.
            // Проверяем до switch: в ru-раскладке ConsoleKey может быть E, и тогда
            // обычный case ConsoleKey.E запустил бы экспорт вместо смены темы.
            if (Theme.IsThemeKey(key))
            {
                // Название темы теперь постоянный блок в футере; transient-статус
                // им больше не затирается.
                Theme.Cycle();
                // Старые строки могли остаться в фоне предыдущей темы.
                ClearWithThemeBackground();
                return;
            }

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
                        if (row.Hint) break;
                        if (row.IsDir)
                        {
                            if (row.Node != _root)
                            {
                                row.Node.Expanded = !row.Node.Expanded;
                                _status = "";
                            }
                        }
                        else if (row.File != null)
                        {
                            OpenFile(row);
                        }
                    }
                    break;

                case ConsoleKey.F:
                    _filter = (FilterMode)(((int)_filter + 1) % 4);
                    _status = Loc.T("Фильтр: ", "Filter: ") + FilterName(_filter);
                    break;

                case ConsoleKey.S:
                    // Цикл из 4 состояний: размер ↓ → размер ↑ → имя ↑ → имя ↓.
                    // Повторное нажатие на той же колонке меняет направление,
                    // следующее — переходит к другой колонке с естественным для неё
                    // порядком (размер — по убыванию, имя — по возрастанию).
                    if (_sort == SortMode.Size)
                    {
                        if (_sortDesc) _sortDesc = false;
                        else { _sort = SortMode.Name; _sortDesc = false; }
                    }
                    else
                    {
                        if (!_sortDesc) _sortDesc = true;
                        else { _sort = SortMode.Size; _sortDesc = true; }
                    }
                    _status = Loc.T("Сортировка: ", "Sort: ") + SortCaption();
                    break;

                case ConsoleKey.E:
                    ExportCsv();
                    break;

                case ConsoleKey.Delete:
                    DeleteCurrent();
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

        // ---------- Открытие файла ----------

        /// <summary>Стандартный запуск через ассоциации Windows (ShellExecute).</summary>
        private void OpenFile(Row row)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = row.FullPath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                _status = Loc.T("Открыто: ", "Opened: ") + row.Name;
            }
            catch (Exception ex)
            {
                _status = Loc.T("Не удалось открыть: ", "Cannot open: ") + ex.Message;
            }
        }

        // ---------- Удаление объекта ----------

        private void DeleteCurrent()
        {
            Row row = CurrentRow();
            if (row.Hint || (row.Node == null && row.File == null)) return;

            if (!_s.Done)
            {
                _status = Loc.T("Сканирование ещё идёт — удаление недоступно",
                                "Scan still running — delete unavailable");
                return;
            }
            if (row.Node == _root)
            {
                _status = Loc.T("Корень скана удалять нельзя", "Cannot delete the scan root");
                return;
            }

            string kind = row.IsDir
                ? Loc.T("папку", "folder")
                : Loc.T("файл", "file");
            string question = " " + Loc.T("Удалить", "Delete") + " " + kind + " \""
                              + row.Name + "\"? [y/N] ";
            if (!Confirm(question))
            {
                _status = Loc.T("Отменено", "Cancelled");
                return;
            }

            string error;
            if (!DeleteHelper.TryDelete(row.FullPath, out error))
            {
                _status = Loc.T("Ошибка удаления: ", "Delete error: ") + error;
                return;
            }

            // Диск уже изменился — синхронно убираем объект из модели, чтобы цифры
            // в дереве не расходились с реальностью и следующий ре-рендер не вернул его.
            lock (_s.Sync)
            {
                if (row.IsDir)
                {
                    Node dir = row.Node;
                    Node parent = dir.Parent;
                    if (parent != null)
                    {
                        parent.Dirs.Remove(dir);
                        for (Node p = parent; p != null; p = p.Parent) p.Size -= dir.Size;
                    }
                }
                else if (row.Parent != null && row.File != null)
                {
                    Node parent = row.Parent;
                    if (parent.RemoveFileByName(row.File.Name, row.Size))
                        for (Node p = parent; p != null; p = p.Parent) p.Size -= row.Size;
                }
                if (_s.Root != null) _filesDone = CountFiles(_s.Root);
            }

            _status = Loc.T("Удалено: ", "Deleted: ") + row.Name;
        }

        /// <summary>
        /// Модальный запрос y/N: центрированный блок с ярко-красной рамкой. Для
        /// деструктивного действия это ключевые правила TUI — высокий контраст,
        /// центр экрана (туда смотрит глаз) и безопасный ответ по умолчанию (N).
        /// </summary>
        private static bool Confirm(string question)
        {
            int width = SafeWidth();
            int height = SafeHeight();
            int boxW = Math.Min(Math.Max(width - 6, 24), Math.Max(46, question.Length + 6));
            int x = Math.Max(1, (width - boxW) / 2 + 1);
            int y = Math.Max(2, height / 2 - 1);

            string title = " " + Loc.T("Удаление", "Delete") + " ";
            string top = "╔" + title
                         + new string('═', Math.Max(0, boxW - 2 - title.Length)) + "╗";
            string mid = "║ " + PadOrTruncate(question, boxW - 4) + " ║";
            string bot = "╚" + new string('═', Math.Max(0, boxW - 2)) + "╝";

            try
            {
                var sb = new StringBuilder(512);
                sb.Append("\x1b[").Append(y).Append(';').Append(x).Append('H').Append(ColorWarn);
                sb.Append(top).Append(ColorReset).Append("\x1b[K\r\n");
                sb.Append("\x1b[").Append(y + 1).Append(';').Append(x).Append('H').Append(ColorWarn);
                sb.Append(mid).Append(ColorReset).Append("\x1b[K\r\n");
                sb.Append("\x1b[").Append(y + 2).Append(';').Append(x).Append('H').Append(ColorWarn);
                sb.Append(bot).Append(ColorReset).Append("\x1b[K");
                sb.Append("\x1b[").Append(y + 1).Append(';').Append(x + 2).Append('H');
                Console.Write(sb.ToString());

                while (true)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Y)
                    {
                        Console.Write(ColorReset + "\x1b[2K");
                        return true;
                    }
                    if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.N
                        || key.Key == ConsoleKey.Enter)
                    {
                        Console.Write(ColorReset + "\x1b[2K");
                        return false;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return false; // ввод перенаправлен — считаем отменой
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
                    CreatedUtc = root.CreatedUtc,
                    LastWriteUtc = root.LastWriteUtc,
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

            // Материализуем файлы один раз и сразу считаем то, что спрятано фильтром.
            // Иначе в режиме «только папки» размер скрытых данных был бы недоступен.
            List<FileRow> all = _s.Done && _filter != FilterMode.Files
                ? dir.GetFileRows()
                : null;
            List<FileRow> files = _filter == FilterMode.Folders
                ? EmptyFiles
                : FilteredFiles(dir, all);

            long hiddenSize = 0;
            long newestCreated = 0;
            long newestWrite = 0;
            int hiddenCount = 0;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    FileRow f = all[i];
                    bool hidden = _filter == FilterMode.Folders
                        || (_filter == FilterMode.Temp && f.Category != Category.Temp)
                        || (_filter == FilterMode.Dumps && f.Category != Category.Dump);
                    if (!hidden) continue;
                    hiddenCount++;
                    hiddenSize += f.Size;
                    if (f.CreatedUtc > newestCreated) newestCreated = f.CreatedUtc;
                    if (f.LastWriteUtc > newestWrite) newestWrite = f.LastWriteUtc;
                }
            }

            bool addFiltered = hiddenCount > 0;
            int total = dirs.Count + files.Count + (addFiltered ? 1 : 0);
            if (total == 0)
            {
                // Во время скана данные могут ещё появиться — показываем "...".
                // После завершения "<Пусто>" означает реально пустую папку.
                if (dir.Expanded)
                {
                    string hint = !_s.Done
                        ? "..."
                        : Loc.T("<Пусто>", "<Empty>");
                    _rows.Add(new Row
                    {
                        Hint = true,
                        IsDir = false,
                        Parent = dir,
                        Depth = dir.Depth + 1,
                        Prefix = childPrefix + "└─ ",
                        Name = hint,
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
                    Parent = dir,
                    IsDir = true,
                    Depth = dir.Depth + 1,
                    Prefix = childPrefix + (isLast ? "└─ " : "├─ "),
                    Name = child.Name,
                    FullPath = child.FullPath,
                    Size = child.Size,
                    CreatedUtc = child.CreatedUtc,
                    LastWriteUtc = child.LastWriteUtc,
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
                    Parent = dir,
                    IsDir = false,
                    Depth = dir.Depth + 1,
                    Prefix = childPrefix + (isLast ? "└─ " : "├─ "),
                    Name = file.Name,
                    FullPath = file.FullPath,
                    Size = file.Size,
                    CreatedUtc = file.CreatedUtc,
                    LastWriteUtc = file.LastWriteUtc,
                    Category = file.Category
                });
            }

            if (addFiltered)
            {
                _rows.Add(new Row
                {
                    Hint = true,
                    ShowMeta = true,
                    IsDir = false,
                    Parent = dir,
                    Depth = dir.Depth + 1,
                    Prefix = childPrefix + "└─ ",
                    Name = Loc.T("<Отфильтровано>", "<Filtered>")
                           + " (" + Format.Number(hiddenCount) + ")",
                    FullPath = dir.FullPath + "\\...",
                    Size = hiddenSize,
                    CreatedUtc = newestCreated,
                    LastWriteUtc = newestWrite,
                    Category = Category.File
                });
            }
        }

        private static readonly List<FileRow> EmptyFiles = new List<FileRow>();

        private List<FileRow> FilteredFiles(Node dir, List<FileRow> all = null)
        {
            // GetFileRows() возвращает кэшированный список, отсортированный по убыванию
            // размера, — его нельзя менять на месте. Всегда делаем свою копию.
            if (all == null) all = dir.GetFileRows();
            var result = new List<FileRow>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                FileRow f = all[i];
                if (_filter == FilterMode.Files
                    || f.Category == (_filter == FilterMode.Temp ? Category.Temp : Category.Dump))
                    result.Add(f);
            }
            SortFileRows(result);
            return result;
        }

        private void SortFileRows(List<FileRow> rows)
        {
            if (_sort == SortMode.Size)
            {
                rows.Sort((a, b) =>
                {
                    int c = a.Size.CompareTo(b.Size);
                    return _sortDesc ? -c : c;
                });
            }
            else
            {
                rows.Sort((a, b) =>
                {
                    int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return _sortDesc ? -c : c;
                });
            }
        }

        private List<Node> SortedDirs(Node dir)
        {
            var list = new List<Node>(dir.Dirs);
            if (_sort == SortMode.Size)
                list.Sort((a, b) =>
                {
                    int c = a.Size.CompareTo(b.Size);
                    return _sortDesc ? -c : c;
                });
            else
                list.Sort((a, b) =>
                {
                    int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return _sortDesc ? -c : c;
                });
            return list;
        }

        private string SortCaption()
        {
            string name = _sort == SortMode.Size
                ? Loc.T("размер", "size")
                : Loc.T("имя", "name");
            return name + " " + (_sortDesc ? "↓" : "↑");
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

            Columns cols = MeasureColumns(width);
            int listHeight = Math.Max(1, height - 4);
            if (_cursor < _top) _top = _cursor;
            if (_cursor >= _top + listHeight) _top = _cursor - listHeight + 1;
            int maxTop = Math.Max(0, _rows.Count - listHeight);
            if (_top > maxTop) _top = maxTop;
            if (_top < 0) _top = 0;

            var sb = new StringBuilder(8192);
            sb.Append("\x1b[H");

            AppendHeaderLine(sb, width);
            sb.Append(ColorReset);
            sb.Append("\x1b[K\r\n");

            AppendColumnHeader(sb, width, cols, _sort, _sortDesc);
            sb.Append(ColorReset);
            sb.Append("\x1b[K\r\n");

            int end = Math.Min(_rows.Count, _top + listHeight);
            for (int i = _top; i < end; i++)
            {
                AppendRowLine(sb, _rows[i], i == _cursor, width, cols);
                sb.Append(ColorReset);
                sb.Append("\x1b[K\r\n");
            }

            AppendFooterLines(sb, width, height);
            Console.Write(sb.ToString());
        }

        private void AppendHeaderLine(StringBuilder sb, int width)
        {
            bool done = _s.Done;
            string files = done && _filesDone >= 0
                ? Format.Number(_filesDone)
                : Format.Number(_s.Progress.Files);

            string title = " WinDiskMap"
                + (VersionLabel.Length > 0 ? " " + VersionLabel : "")
                + " │ ";
            string text = title + _root.FullPath
                + " │ " + (done ? Loc.T("Итого", "Total") : Loc.T("Идёт сканирование", "Scanning"))
                + ": " + Format.Bytes(_root.Size)
                + " │ " + Loc.T("Файлов", "Files") + ": " + files
                + " │ " + _s.Mode + " ";
            sb.Append(ColorHeader);
            sb.Append(PadOrTruncate(text, width));
        }

        private static int MetaWidth(Columns c)
        {
            int w = Gutter + c.SizeW;
            if (c.Percent) w += Gutter + c.PctW;
            if (c.Dates)
                w += (c.Percent ? GroupGap : Gutter) + c.DateW + Gutter + c.DateW;
            return w;
        }

        private static Columns MeasureColumns(int width)
        {
            var full = new Columns { SizeW = 10, PctW = 7, DateW = 16, Percent = true, Dates = true };
            var noDates = new Columns { SizeW = 10, PctW = 7, DateW = 16, Percent = true };
            // Оставляем минимум места под имя/дерево; на узком терминале сначала
            // пропадают даты, затем процент, размер остаётся всегда.
            if (width >= 24 + MetaWidth(full)) return full;
            if (width >= 20 + MetaWidth(noDates)) return noDates;
            return new Columns { SizeW = 10, PctW = 7, DateW = 16 };
        }

        private static void AppendColumnHeader(StringBuilder sb, int width, Columns c,
                                               SortMode sort, bool sortDesc)
        {
            string arrow = sortDesc ? "↓" : "↑";

            // Активная колонка выделяется цветом активной сортировки из текущей темы
            // и несёт стрелку направления; остальные остаются обычной шапкой.
            // Ширина колонок фиксированная,
            // поэтому вставка стрелки не сдвигает правые колонки.
            bool nameActive = sort == SortMode.Name;
            string left = " " + Loc.T("Путь", "Path") + (nameActive ? " " + arrow : "");
            AppendHeaderSegment(sb, left, nameActive);

            int pad = width - 1 - left.Length - MetaWidth(c);
            if (pad < 1) pad = 1;
            sb.Append(ColorHeader); // не растягиваем подсветку активной колонки на весь зазор
            sb.Append(new string(' ', pad));

            bool sizeActive = sort == SortMode.Size;
            string sizeLabel = PadLeft(
                Loc.T("Размер", "Size") + (sizeActive ? " " + arrow : ""), c.SizeW);
            AppendHeaderSegment(sb, new string(' ', Gutter), false);
            AppendHeaderSegment(sb, sizeLabel, sizeActive);

            if (c.Percent)
            {
                AppendHeaderSegment(sb, new string(' ', Gutter), false);
                AppendHeaderSegment(sb, PadLeft("%", c.PctW), false);
            }
            if (c.Dates)
            {
                AppendHeaderSegment(sb, new string(' ', c.Percent ? GroupGap : Gutter), false);
                AppendHeaderSegment(sb, PadLeft(Loc.T("Создан", "Created on"), c.DateW), false);
                AppendHeaderSegment(sb, new string(' ', Gutter), false);
                AppendHeaderSegment(sb, PadLeft(Loc.T("Посл. запись", "Last write"), c.DateW), false);
            }
        }

        private static void AppendHeaderSegment(StringBuilder sb, string text, bool active)
        {
            sb.Append(active ? ColorSortActive : ColorHeader);
            sb.Append(text);
        }

        private static string PadLeft(string text, int width)
        {
            if (text.Length > width) return text.Substring(text.Length - width);
            return text.Length == width ? text : new string(' ', width - text.Length) + text;
        }

        private static string BuildMeta(Row row, Columns c, long rootSize)
        {
            var sb = new StringBuilder(80);
            sb.Append(new string(' ', Gutter)).Append(PadLeft(Format.Bytes(row.Size), c.SizeW));
            if (c.Percent)
                sb.Append(new string(' ', Gutter))
                  .Append(PadLeft(Format.Percent(row.Size, rootSize), c.PctW));
            if (c.Dates)
            {
                sb.Append(new string(' ', c.Percent ? GroupGap : Gutter));
                sb.Append(PadLeft(Format.Time(row.CreatedUtc), c.DateW));
                sb.Append(new string(' ', Gutter))
                  .Append(PadLeft(Format.Time(row.LastWriteUtc), c.DateW));
            }
            return sb.ToString();
        }

        private void AppendRowLine(StringBuilder sb, Row row, bool cursor, int width, Columns c)
        {
            string meta = row.Hint && !row.ShowMeta ? "" : BuildMeta(row, c, _root.Size);

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

        private void AppendFooterLines(StringBuilder sb, int width, int height)
        {
            // Сначала чистим хвост экрана после последней строки дерева, затем
            // позиционируем обе статусные строки АБСОЛЮТНО по нижнему краю окна.
            // Иначе они «прилипают» к дереву и прыгают при раскрытии/фильтрации.
            sb.Append("\x1b[J");
            sb.Append("\x1b[").Append(height - 1).Append(";1H");

            string line1 = " " + (_cursor + 1).ToString("N0") + "/" + _rows.Count.ToString("N0")
                + " │ " + Loc.T("Фильтр", "Filter") + ": " + FilterName(_filter)
                + " │ " + Loc.T("Сортировка", "Sort") + ": " + SortCaption()
                + " │ " + Loc.T("Тема", "Theme") + ": " + Theme.Current.Name(Loc.Ru)
                + (_status.Length > 0 ? " │ " + _status : "");
            sb.Append(ColorDim);
            sb.Append(PadOrTruncate(line1, width));
            sb.Append("\x1b[K\r\n");

            sb.Append("\x1b[").Append(height).Append(";1H");
            AppendKeysHint(sb, width);
            sb.Append("\x1b[K");
        }

        /// <summary>
        /// Подсказка клавиш: сама клавиша выделена акцентом, описание остаётся
        /// приглушённым. На узком окне описания отбрасываются целиком, чтобы не
        /// обрезались последние подсказки (в том числе q — выход).
        /// </summary>
        private static void AppendKeysHint(StringBuilder sb, int width)
        {
            Loc.KeyHint[] hints = Loc.KeyHints;
            int full = 1; // ведущий пробел, как и в обычной строке
            for (int i = 0; i < hints.Length; i++)
            {
                if (i > 0) full += 2;
                full += hints[i].Key.Length + 1 + hints[i].Description.Length;
            }
            bool compact = full > width;

            int col = 0;
            sb.Append(ColorDim).Append(' ');
            col++;
            for (int i = 0; i < hints.Length; i++)
            {
                string sep = i > 0 ? "  " : "";
                int tokenLen = sep.Length + hints[i].Key.Length
                    + (compact ? 0 : 1 + hints[i].Description.Length);
                if (col + tokenLen > width) break;

                sb.Append(ColorDim).Append(sep);
                sb.Append(ColorKey).Append(hints[i].Key);
                if (!compact)
                    sb.Append(ColorDim).Append(' ').Append(hints[i].Description);
                col += tokenLen;
            }
            sb.Append(ColorReset);
        }

        private void RenderScanning(int width, int height)
        {
            var sb = new StringBuilder(2048);
            sb.Append("\x1b[H");
            sb.Append(ColorHeader);
            sb.Append(PadOrTruncate(
                _s.Error != null
                    ? " WinDiskMap" + VersionLabelTitle + " "
                    : " WinDiskMap" + VersionLabelTitle + " — "
                      + Loc.T("Идёт сканирование", "Scanning") + " ",
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
            int tick = SpinnerIndex(Environment.TickCount);
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

        /// <summary>Сырой размер окна — только для обнаружения ресайза (без clamp'а).</summary>
        private static int RawWidth()
        {
            try { return Console.WindowWidth; } catch { return 0; }
        }

        private static int RawHeight()
        {
            try { return Console.WindowHeight; } catch { return 0; }
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

        /// <summary>
        /// Индекс спиннера 0..3. Environment.TickCount — Int32 и уходит в минус
        /// после ~24.86 суток аптайма; беззнаковое деление не даёт отрицательный остаток.
        /// </summary>
        internal static int SpinnerIndex(int tickCount)
        {
            return (int)(((uint)tickCount / 120u) % 4u);
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
                " WinDiskMap" + VersionLabelTitle + " — "
                + Loc.T("Идёт сканирование", "Scanning") + " ", width));
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
            sb.Append("\x1b[K\r\n");
            if (!string.IsNullOrEmpty(prog.Note))
            {
                sb.Append("  ").Append(prog.Note);
                sb.Append("\x1b[K\r\n");
            }
            sb.Append("\x1b[J");
            Console.Write(sb.ToString());
        }
    }
}
