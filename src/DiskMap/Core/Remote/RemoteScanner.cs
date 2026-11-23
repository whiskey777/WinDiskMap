using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DiskMap.Core.Remote
{
    /// <summary>
    /// Клиентская сторона удалённого скана: читает NDJSON-поток агента и строит
    /// живое дерево Node у себя (TUI пополняется по мере поступления записей).
    /// Пути в дереве показываются так, как их видит пользователь (displayRoot),
    /// а не как их называл агент, — дерево остаётся узнаваемым для UNC/--path.
    /// </summary>
    public static class RemoteScanner
    {
        /// <summary>
        /// targetPath — путь на удалённом хосте (C:\Windows); displayRoot — как
        /// показывать корень дерева (обычно \\host\C$\Windows или заданный --path).
        /// </summary>
        public static void Consume(ScanSession session, Stream ndjson, string displayRoot)
        {
            ScanProgress prog = session.Progress;
            var tree = new TreeBuilder(session, prog);
            tree.Begin(displayRoot);

            bool began = false;
            bool ended = false;
            string streamNotes = null;

            using (var reader = new StreamReader(ndjson, new UTF8Encoding(false), false, 1 << 16))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    string t = NdJson.GetString(line, "t");
                    if (t == null) continue;

                    switch (t)
                    {
                        case "meta":
                            session.Mode = NdJson.GetString(line, "mode") ?? session.Mode;
                            break;

                        case "phase":
                            prog.Phase = NdJson.GetString(line, "v") ?? prog.Phase;
                            break;

                        case "note":
                            {
                                string v = NdJson.GetString(line, "v");
                                if (!string.IsNullOrEmpty(v))
                                    streamNotes = streamNotes != null ? streamNotes + "; " + v : v;
                            }
                            break;

                        case "dir":
                            {
                                int id = NdJson.GetInt(line, "id");
                                int parent = NdJson.GetInt(line, "parent", -1);
                                string name = NdJson.GetString(line, "name");
                                bool reparse = NdJson.GetBool(line, "reparse");
                                if (parent < 0 && !began)
                                {
                                    // Корень агента: имя/путь подменяем на displayRoot,
                                    // чтобы дерево выглядело как указано хостом.
                                    began = true;
                                    break;
                                }
                                tree.Dir(id, parent, name, reparse);
                            }
                            break;

                        case "file":
                            {
                                int id = NdJson.GetInt(line, "id");
                                int parent = NdJson.GetInt(line, "parent", -1);
                                string name = NdJson.GetString(line, "name");
                                long size = NdJson.GetLong(line, "size");
                                tree.File(id, parent, name, size);
                            }
                            break;

                        case "end":
                            ended = true;
                            break;

                        case "error":
                            throw new IOException(NdJson.GetString(line, "v") ?? "agent error");
                    }
                }
            }

            if (!ended && session.Error == null)
                session.Progress.Note = Join(session.Progress.Note,
                    Loc.T("Агент завершился без финальной записи (поток оборван).",
                          "Agent ended without a final record (stream broken)."));

            if (streamNotes != null)
                session.Progress.Note = Join(session.Progress.Note, streamNotes);
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "; " + b;
        }
    }
}
