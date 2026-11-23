using System;
using System.IO;
using WinDiskMap.Core.Mft;

namespace WinDiskMap.Core.Remote
{
    /// <summary>
    /// Роль «агент»: запускается на удалённом хосте (или локально для отладки),
    /// сканирует ЛОКАЛЬНЫЙ путь и пишет результат в stdout потоком NDJSON.
    /// Хост (клиент WinRM) читает этот поток и строит дерево у себя.
    ///
    /// Агент выбирает режим так же, как обычный запуск: MFT при админе и локальном
    /// NTFS (быстро и точно), иначе — обход каталогов. Точность та же, что у
    /// локального WinDiskMap: NDJSON передаёт ровно те записи, что и обычный сканер.
    /// </summary>
    internal static class AgentRunner
    {
        /// <summary>
        /// path — локальный путь на удалённом хосте (например C:\Windows).
        /// restrictMft — принудительный обход (аналог --no-mft).
        /// </summary>
        public static int Run(string path, bool restrictMft)
        {
            var writer = new NdJsonWriter(Console.OpenStandardOutput());
            var prog = new ScanProgress();

            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    writer.Error("agent: path is required");
                    return 1;
                }

                string target = Path.GetFullPath(path).TrimEnd('\\');
                if (!Directory.Exists(target))
                {
                    writer.Error("agent: directory not found: " + target);
                    return 2;
                }

                bool admin = ScanRunner.IsAdministrator();
                string volumeRoot = Path.GetPathRoot(target);
                bool localNtfs = !ScanRunner.IsNetworkPath(target)
                                 && volumeRoot != null
                                 && volumeRoot.Length >= 1
                                 && IsNtfs(volumeRoot);

                if (!restrictMft && localNtfs && admin)
                {
                    try
                    {
                        prog.Phase = Loc.T("MFT (агент)", "MFT (agent)");
                        ScanIndex idx = MftScanner.Scan(volumeRoot, prog);
                        EmitIndex(writer, idx, target, Loc.T("MFT (прямое чтение $MFT)", "MFT (direct $MFT read)"));
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        // MFT не удался — честно сообщаем и падаем на обход.
                        writer.Note(Loc.T("MFT-режим на агенте недоступен", "MFT mode unavailable on agent")
                                    + " (" + ex.GetType().Name + "): " + ex.Message);
                    }
                }

                string mode = Loc.T("Обход каталогов", "Directory walk")
                              + (admin ? "" : Loc.T(" (без админа)", " (without admin)"));
                writer.Meta(mode);

                var sink = new NdJsonSink(writer, prog);
                DirectoryWalk.Run(target, sink, prog, out long skippedLinks);

                if (skippedLinks > 0)
                    writer.Note(Loc.T("Пропущено junction/symlink-каталогов: ",
                                      "Skipped junction/symlink directories: ")
                                + skippedLinks.ToString("N0"));

                writer.End(prog.Files, prog.Dirs, prog.Bytes);
                writer.Flush();
                return 0;
            }
            catch (Exception ex)
            {
                writer.Error(ex.GetType().Name + ": " + ex.Message);
                return 2;
            }
        }

        /// <summary>
        /// Разворачивает готовый ScanIndex в NDJSON, обходя поддерево целевого
        /// каталога в ширину: родитель всегда выдаётся раньше ребёнка, id нумеруются
        /// подряд (0 — корень поддерева). Так хост строит дерево тем же кодом, что и
        /// для обхода каталогов.
        /// </summary>
        private static void EmitIndex(NdJsonWriter writer, ScanIndex idx, string target, string mode)
        {
            writer.Meta(mode);

            int rootIndex = idx.FindDirectory(target);
            if (rootIndex < 0)
            {
                writer.Error("agent: directory not found on volume: " + target);
                return;
            }

            idx.BuildChildMap();

            long files = 0, dirs = 0, bytes = 0;
            writer.Dir(0, -1, idx.Names[rootIndex], idx.IsReparse[rootIndex]);
            dirs++;

            var queue = new System.Collections.Generic.Queue<int[]>();
            queue.Enqueue(new[] { rootIndex, 0 });
            int nextId = 1;

            while (queue.Count > 0)
            {
                int[] cur = queue.Dequeue();
                System.Collections.Generic.List<int> kids = idx.ChildrenOf(cur[0]);
                for (int i = 0; i < kids.Count; i++)
                {
                    int k = kids[i];
                    if (idx.IsDir[k])
                    {
                        writer.Dir(nextId, cur[1], idx.Names[k], idx.IsReparse[k]);
                        dirs++;
                        queue.Enqueue(new[] { k, nextId });
                        nextId++;
                    }
                    else
                    {
                        writer.File(nextId, cur[1], idx.Names[k], idx.Sizes[k]);
                        files++;
                        bytes += idx.Sizes[k];
                        nextId++;
                    }
                }
            }

            writer.End(files, dirs, bytes);
            writer.Flush();
        }

        private static bool IsNtfs(string root)
        {
            try
            {
                var drive = new DriveInfo(root.Substring(0, 1));
                return string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Приёмник обхода: каждую запись сразу отдаёт в NDJSON-поток.</summary>
        private sealed class NdJsonSink : IWalkSink
        {
            private readonly NdJsonWriter _writer;
            private readonly ScanProgress _prog;

            public NdJsonSink(NdJsonWriter writer, ScanProgress prog)
            {
                _writer = writer;
                _prog = prog;
            }

            public void Begin(string displayRoot)
            {
                _writer.Dir(0, -1, displayRoot, false);
            }

            public void Dir(int id, int parent, string name, bool reparse)
            {
                _writer.Dir(id, parent, name, reparse);
                _prog.AddDir();
            }

            public void File(int id, int parent, string name, long size)
            {
                _writer.File(id, parent, name, size);
                _prog.AddFile(size);
            }

            public void End(long skippedLinks)
            {
                _writer.End(_prog.Files, _prog.Dirs, _prog.Bytes);
            }
        }
    }
}
