namespace WinDiskMap.Core
{
    /// <summary>
    /// Совместное состояние сканирования и TUI. Root строится либо сразу (живой обход
    /// каталогов — TUI пополняется по мере скана), либо в конце (MFT — быстрый путь).
    /// Все мутации дерева и его чтение из UI происходят под Sync.
    /// </summary>
    public sealed class ScanSession
    {
        public readonly object Sync = new object();

        public volatile string DisplayPath;
        public volatile Node Root;
        public volatile bool Done;
        public volatile string Error;
        public volatile string Mode = "";

        public readonly ScanProgress Progress = new ScanProgress();
    }
}
