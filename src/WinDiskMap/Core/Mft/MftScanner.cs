using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace WinDiskMap.Core.Mft
{
    /// <summary>
    /// Прямое чтение $MFT с тома (то, что делает WizTree): boot-сектор → запись 0 ($MFT)
    /// → её $DATA runlist → последовательное чтение и парсинг FILE-записей.
    /// Требует прав администратора и локального NTFS-тома.
    /// </summary>
    internal static class MftScanner
    {
        private const long CurrAttribute = 0xFFFFFFFF;
        // Важно: типы атрибутов NTFS — $FILE_NAME = 0x30, $STANDARD_INFORMATION = 0x10.
        // При путанице «имена» читаются из SI (timestamps/USN) — мусорные имена и
        // неразрешимые родители (orphans ≈ entries, поиски путей не находят ничего).
        private const int AttrStdInfo = 0x10;   // $STANDARD_INFORMATION
        private const int AttrAttrList = 0x20;  // $ATTRIBUTE_LIST
        private const int AttrFileName = 0x30;  // $FILE_NAME
        private const int AttrData = 0x80;      // $DATA

        private struct Run
        {
            public long StartLcn;
            public long Clusters;
            public bool Sparse;
        }

        /// <summary>
        /// Диагностика (--debug-mft): подстрока имени (без учёта регистра). Для каждой
        /// записи, у которой есть FN с таким именем, в лог пишется дамп всех атрибутов
        /// (тип, namespace, VCN, allocated/real/init sizes). Требует --log.
        /// </summary>
        internal static string DebugMask;

        public static ScanIndex Scan(string volumeRoot, ScanProgress prog)
        {
            string device = @"\\.\" + volumeRoot.TrimEnd('\\');
            using (var vol = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
            {
                prog.Phase = Loc.T("Чтение загрузочной записи", "Reading boot sector");
                byte[] boot = new byte[4096];
                ReadExact(vol, 0, boot, 0, boot.Length);

                if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
                    throw new IOException(Loc.T("Том не является NTFS: ", "Volume is not NTFS: ") + volumeRoot);

                int sectorSize = BitConverter.ToUInt16(boot, 0x0B);
                int sectorsPerCluster = boot[0x0D];
                if (sectorSize <= 0 || sectorsPerCluster <= 0)
                    throw new IOException(Loc.T("Параметры тома повреждены (boot-сектор).", "Volume parameters corrupted (boot sector)."));

                long clusterSize = (long)sectorSize * sectorsPerCluster;
                long mftLcn = BitConverter.ToInt64(boot, 0x30);
                sbyte clustersPerRecord = unchecked((sbyte)boot[0x40]);
                int recordSize = clustersPerRecord > 0
                    ? clustersPerRecord * (int)clusterSize
                    : 1 << (-clustersPerRecord);

                if (recordSize < 512 || recordSize > 64 * 1024 || (recordSize & (recordSize - 1)) != 0)
                    throw new IOException(Loc.T("Неподдерживаемый размер записи MFT: ", "Unsupported MFT record size: ") + recordSize);

                // Запись 0 — сам $MFT: из неё берём runlist его собственных данных.
                prog.Phase = Loc.T("Чтение записи $MFT", "Reading $MFT record");
                byte[] rec0 = new byte[recordSize];
                ReadExact(vol, mftLcn * clusterSize, rec0, 0, recordSize);
                ApplyFixups(rec0, recordSize, sectorSize);

                List<Run> runs = ExtractDataRuns(rec0, recordSize);
                if (runs == null || runs.Count == 0)
                    throw new IOException(Loc.T("Не удалось разобрать runlist $MFT.", "Failed to parse $MFT runlist."));

                long mftBytes = 0;
                for (int i = 0; i < runs.Count; i++)
                    if (!runs[i].Sparse) mftBytes += runs[i].Clusters * clusterSize;

                prog.Phase = Loc.T("Парсинг записей MFT", "Parsing MFT records");

                var collector = new Collector();
                var scratch = new FnScratch(); // переиспользуемый буфер $FILE_NAME-ссылок записи
                var rec = new byte[recordSize];
                int bufSize = (int)Math.Min(mftBytes, 8L * 1024 * 1024);
                bufSize -= bufSize % recordSize;
                if (bufSize < recordSize) bufSize = recordSize;
                byte[] buf = new byte[bufSize];

                long pos = 0;
                int filled = ReadChunk(vol, runs, clusterSize, sectorSize, pos, buf, 0, bufSize);
                int recNo = 0;
                int torn = 0;
                byte[] slot5 = null; // снимок записи корня тома для диагностики

                while (filled > 0)
                {
                    int off = 0;
                    while (off + recordSize <= filled)
                    {
                        Buffer.BlockCopy(buf, off, rec, 0, recordSize);
                        if (recNo == ScanIndex.RootRecord && slot5 == null)
                            slot5 = (byte[])rec.Clone(); // до мутаций ApplyFixups
                        if (!ParseRecord(rec, recordSize, sectorSize, recNo, collector, scratch))
                            torn++;
                        off += recordSize;
                        recNo++;

                        if ((recNo & 0x3FFF) == 0)
                        {
                            prog.Phase = Loc.T("Парсинг записей MFT", "Parsing MFT records")
                                         + " (" + recNo.ToString("N0") + ")";
                        }
                    }

                    int rem = filled - off;
                    if (rem > 0) Buffer.BlockCopy(buf, off, buf, 0, rem);
                    long nextReadAt = pos + off + rem; // следующий непрочитанный байт $MFT
                    int got = rem >= bufSize
                        ? 0
                        : ReadChunk(vol, runs, clusterSize, sectorSize, nextReadAt, buf, rem, bufSize - rem);
                    if (got == 0 && rem < recordSize) break; // хвост < одной записи
                    pos = nextReadAt;
                    filled = rem + got;
                    if (got == 0) break;
                }

                // Слияние расширенных записей ($ATTRIBUTE_LIST): hardlink-пути и размеры,
                // которых нет в базовых записях. До ToIndex — иначе строки не попадут в индекс.
                collector.MergeExtensions();

                if (torn > 0)
                {
                    string note = Loc.T("Пропущено повреждённых (torn) записей: ", "Skipped damaged (torn) records: ")
                                  + torn.ToString("N0");
                    prog.Note = prog.Note != null ? prog.Note + "; " + note : note;
                }

                if (collector.ReparseDirCount > 0)
                {
                    string rnote = Loc.T("Reparse-каталогов (вне счёта папок): ",
                                         "Reparse directories (excluded from folder count): ")
                                   + collector.ReparseDirCount.ToString("N0");
                    prog.Note = prog.Note != null ? prog.Note + "; " + rnote : rnote;
                }

                ScanIndex index;
                try
                {
                    index = collector.ToIndex();
                }
                catch (IOException ex)
                {
                    // Диагностика для траблшутинга: что реально со слотом 5 и с чтением в целом.
                    throw new IOException(ex.Message + " " +
                        BuildStats(collector, slot5, recNo, torn, mftBytes, recordSize, sectorSize, clusterSize, runs));
                }
                return index;
            }
        }

        /// <summary>Возвращает false для повреждённых записей.</summary>
        private static bool ParseRecord(byte[] rec, int recordSize, int sectorSize, int recNo,
            Collector collector, FnScratch scratch)
        {
            if (rec[0] != 'F' || rec[1] != 'I' || rec[2] != 'L' || rec[3] != 'E') return false;
            if (!ApplyFixups(rec, recordSize, sectorSize)) return false;

            ushort flags = BitConverter.ToUInt16(rec, 0x16);
            if ((flags & 0x01) == 0) return true; // запись не используется

            long baseRec = BitConverter.ToInt64(rec, 0x20) & 0x0000FFFFFFFFFFFF;
            // Расширенная запись (attribute list): её атрибуты принадлежат записи baseRec.
            // FN-ссылки расширения — дополнительные hardlink-пути, $DATA-расширения несут
            // тот же real size. Раньше такие записи пропускались целиком — из-за этого
            // терялись хардлинки и размер файлов, чей $DATA уехал из базовой записи.
            bool isExtension = baseRec != 0;

            bool isDir = (flags & 0x02) != 0;
            long size = 0;
            long allocated = 0;   // «На диске»: allocated size из $DATA (offset +0x28)
            uint fileAttributes = 0; // FILE_ATTRIBUTE_* из $STANDARD_INFORMATION / $FILE_NAME
            bool gotSize = false;
            bool reparse = false;
            long fnSize = 0;
            bool haveFnSize = false;
            // Даты: $STANDARD_INFORMATION приоритетнее (то, что показывает Проводник),
            // $FILE_NAME — фолбэк для повреждённых/устаревших SI. FILETIME в UTC.
            long siCreated = 0, siLastWrite = 0;
            long fnCreated = 0, fnLastWrite = 0;

            scratch.Reset();

            int attrOfs = BitConverter.ToUInt16(rec, 0x14);
            while (attrOfs >= 0 && attrOfs + 8 <= recordSize)
            {
                int type = BitConverter.ToInt32(rec, attrOfs);
                if (type == unchecked((int)CurrAttribute)) break;
                int len = BitConverter.ToInt32(rec, attrOfs + 4);
                if (len < 24 || attrOfs + len > recordSize) break;

                byte nonRes = rec[attrOfs + 8];
                byte nameLen = rec[attrOfs + 9];

                if (type == AttrStdInfo && nameLen == 0 && nonRes == 0)
                {
                    // FILE_ATTRIBUTE_REPARSE_POINT живёт и в $STANDARD_INFORMATION (value+0x20).
                    //junction-папки Windows часто несут флаг только там — без второй проверки
                    // reparse-папки поддерева не распознаются и попадают в счётчик.
                    int siLen = BitConverter.ToInt32(rec, attrOfs + 0x10);
                    int siOfs = BitConverter.ToUInt16(rec, attrOfs + 0x14);
                    if (siLen >= 0x24 && siOfs >= 0x18 && attrOfs + siOfs + 0x24 <= recordSize)
                    {
                        if (siLen >= 0x18)
                        {
                            siCreated = BitConverter.ToInt64(rec, attrOfs + siOfs);
                            siLastWrite = BitConverter.ToInt64(rec, attrOfs + siOfs + 0x08);
                        }
                        uint siAttrs = BitConverter.ToUInt32(rec, attrOfs + siOfs + 0x20);
                        if (fileAttributes == 0) fileAttributes = siAttrs;
                        if ((siAttrs & 0x400) != 0) reparse = true;
                    }
                }
                else if (type == AttrFileName && nameLen == 0 && nonRes == 0)
                {
                    int contentLen = BitConverter.ToInt32(rec, attrOfs + 0x10);
                    int contentOfs = BitConverter.ToUInt16(rec, attrOfs + 0x14);
                    if (contentLen >= 0x42 && contentOfs >= 0x18 && attrOfs + contentOfs + contentLen <= recordSize)
                    {
                        int c = attrOfs + contentOfs;
                        if (fnCreated == 0)
                        {
                            fnCreated = BitConverter.ToInt64(rec, c + 0x08);
                            fnLastWrite = BitConverter.ToInt64(rec, c + 0x10);
                        }
                        byte nlen = rec[c + 0x40];
                        byte ns = rec[c + 0x41];
                        if (nlen > 0 && c + 0x42 + nlen * 2 <= recordSize)
                        {
                            // Каждый $FILE_NAME — отдельный hardlink-путь (у NTFS запись
                            // на каждого владельца + DOS-дубликат). Добавляем все пути с полным
                            // размером файла: так считает WizTree — файл входит в размер каждого
                            // каталога-владельца (WinSxS ↔ System32 и т.п., 34 → 72 ГБ).
                            scratch.AddLink(
                                BitConverter.ToInt64(rec, c) & 0x0000FFFFFFFFFFFF,
                                Encoding.Unicode.GetString(rec, c + 0x42, nlen * 2),
                                ns);
                        }
                        // FN несёт real size файла — резервный размер, если $DATA уехал
                        // в расширенную запись (attribute list) и в базовой записи его нет.
                        fnSize = BitConverter.ToInt64(rec, c + 0x30);
                        haveFnSize = true;
                        // FILE_ATTRIBUTE_REPARSE_POINT в флагах FN: junction/symlink-папки
                        // видны в дереве, но не входят в счётчик папок (как у WizTree).
                        uint fnAttrs = BitConverter.ToUInt32(rec, c + 0x38);
                        if (fileAttributes == 0) fileAttributes = fnAttrs;
                        if ((fnAttrs & 0x400) != 0) reparse = true;
                    }
                }
                else if (type == AttrData && nameLen == 0 && !gotSize)
                {
                    if (nonRes == 0)
                    {
                        int contentLen = BitConverter.ToInt32(rec, attrOfs + 0x10);
                        if (contentLen >= 0) { size = contentLen; gotSize = true; }
                    }
                    else
                    {
                        // Логический (real) размер данных — как основной размер файла у WizTree.
                        // Выделенный размер лежит на 8 байт раньше (+0x28) — это и есть
                        // колонка «На диске» в экспорте WizTree (сумма кластеров data runs).
                        allocated = BitConverter.ToInt64(rec, attrOfs + 0x28);
                        size = BitConverter.ToInt64(rec, attrOfs + 0x30);
                        gotSize = true;
                    }
                }

                attrOfs += len;
            }

            // FN несёт real size — фолбэк, когда $DATA целиком уехал в расширенную запись
            // (attribute list): без этого файл считается с нулевым размером.
            if (!isDir && !gotSize && haveFnSize)
            {
                size = fnSize;
                gotSize = true;
            }

            long createdUtc = siCreated != 0 ? siCreated : fnCreated;
            long lastWriteUtc = siLastWrite != 0 ? siLastWrite : fnLastWrite;

            if (isExtension)
            {
                // Расширенная запись: FN-ссылки = дополнительные hardlink-пути базовой записи,
                // $DATA-расширения = её размер. Складываем в словарь и сливаем после прохода
                // (расширение может встретиться и до, и после базовой записи).
                if ((scratch.Count > 0 || gotSize) && baseRec > 0 && baseRec <= int.MaxValue)
                    collector.AddExtension((int)baseRec, scratch, size, gotSize, isDir, reparse,
                                           createdUtc, lastWriteUtc, allocated, fileAttributes);
                MaybeDump(rec, recordSize, recNo, baseRec, true, isDir, scratch, size, gotSize,
                          fnSize, haveFnSize);
                return true;
            }

            if (scratch.Count == 0)
            {
                // Корень тома (запись 5) может не иметь $FILE_NAME: его имя — метка тома,
                // при пустой метке длина имени равна 0. Без фикса корень «не найден».
                if (recNo == ScanIndex.RootRecord && isDir)
                    collector.Add(recNo, ".", -1, 0, true, false, 2, createdUtc, lastWriteUtc,
                                  allocated, fileAttributes);
                return true; // без имени — пропускаем молча
            }

            for (int i = 0; i < scratch.Count; i++)
                collector.Add(recNo, scratch.Names[i], scratch.Parents[i],
                              isDir ? 0 : size, isDir, isDir && reparse, scratch.Ranks[i],
                              createdUtc, lastWriteUtc, allocated, fileAttributes);
            MaybeDump(rec, recordSize, recNo, baseRec, false, isDir, scratch, size, gotSize,
                      fnSize, haveFnSize);
            return true;
        }

        /// <summary>Дамп атрибутов записи, если имя попало под --debug-mft (только при --log).</summary>
        private static void MaybeDump(byte[] rec, int recordSize, int recNo, long baseRec,
            bool isExtension, bool isDir, FnScratch scratch, long size, bool gotSize,
            long fnSize, bool haveFnSize)
        {
            if (DebugMask == null || !AppLog.Enabled || scratch.Count == 0) return;

            string[] masks = DebugMask.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            bool match = false;
            for (int i = 0; i < scratch.Count && !match; i++)
                for (int m = 0; m < masks.Length; m++)
                    if (scratch.Names[i].IndexOf(masks[m], StringComparison.OrdinalIgnoreCase) >= 0)
                    { match = true; break; }
            if (!match) return;

            var sb = new StringBuilder(1024);
            sb.Append("rec=").Append(recNo)
              .Append(" ext=").Append(isExtension ? 1 : 0)
              .Append(" base=").Append(baseRec)
              .Append(" dir=").Append(isDir ? 1 : 0)
              .Append(" flags=0x").Append(BitConverter.ToUInt16(rec, 0x16).ToString("X4"))
              .Append(" chosenSize=").Append(size)
              .Append(" gotData=").Append(gotSize ? 1 : 0)
              .Append(" fnSize=").Append(fnSize)
              .Append(" haveFn=").Append(haveFnSize ? 1 : 0);
            AppLog.Write("DEBUG", sb.ToString());

            int attrOfs = BitConverter.ToUInt16(rec, 0x14);
            while (attrOfs >= 0 && attrOfs + 8 <= recordSize)
            {
                int type = BitConverter.ToInt32(rec, attrOfs);
                if (type == unchecked((int)CurrAttribute)) break;
                int len = BitConverter.ToInt32(rec, attrOfs + 4);
                if (len < 24 || attrOfs + len > recordSize) break;

                byte nonRes = rec[attrOfs + 8];
                byte nameLen = rec[attrOfs + 9];
                var a = new StringBuilder(256);
                a.Append("  rec=").Append(recNo).Append(" attr=0x").Append(type.ToString("X2"))
                 .Append(' ').Append(AttrName(type))
                 .Append(" nonRes=").Append(nonRes)
                 .Append(" nameLen=").Append(nameLen)
                 .Append(" attrId=0x").Append(BitConverter.ToUInt16(rec, attrOfs + 0x0E).ToString("X4"));

                if (nonRes != 0)
                {
                    a.Append(" startVCN=").Append(BitConverter.ToInt64(rec, attrOfs + 0x10))
                     .Append(" lastVCN=").Append(BitConverter.ToInt64(rec, attrOfs + 0x18))
                     .Append(" compUnit=").Append(BitConverter.ToUInt16(rec, attrOfs + 0x22))
                     .Append(" alloc=").Append(BitConverter.ToInt64(rec, attrOfs + 0x28))
                     .Append(" real=").Append(BitConverter.ToInt64(rec, attrOfs + 0x30))
                     .Append(" init=").Append(BitConverter.ToInt64(rec, attrOfs + 0x38));
                }
                else
                {
                    a.Append(" contentLen=").Append(BitConverter.ToInt32(rec, attrOfs + 0x10))
                     .Append(" contentOfs=").Append(BitConverter.ToUInt16(rec, attrOfs + 0x14));
                }

                if (type == AttrFileName && nameLen == 0 && nonRes == 0)
                {
                    int contentLen = BitConverter.ToInt32(rec, attrOfs + 0x10);
                    int contentOfs = BitConverter.ToUInt16(rec, attrOfs + 0x14);
                    if (contentLen >= 0x42 && contentOfs >= 0x18 && attrOfs + contentOfs + contentLen <= recordSize)
                    {
                        int c = attrOfs + contentOfs;
                        byte nlen = rec[c + 0x40];
                        byte ns = rec[c + 0x41];
                        string nm = nlen > 0 && c + 0x42 + nlen * 2 <= recordSize
                            ? Encoding.Unicode.GetString(rec, c + 0x42, nlen * 2) : "?";
                        a.Append(" parent=").Append(BitConverter.ToInt64(rec, c) & 0x0000FFFFFFFFFFFF)
                         .Append(" ns=").Append(ns)
                         .Append(" fnReal=").Append(BitConverter.ToInt64(rec, c + 0x30))
                         .Append(" fnFlags=0x").Append(BitConverter.ToUInt32(rec, c + 0x38).ToString("X8"))
                         .Append(" name='").Append(nm).Append('\'');
                    }
                }
                else if (nameLen > 0)
                {
                    int nofs = BitConverter.ToUInt16(rec, attrOfs + 0x0A);
                    if (nofs >= 0x18 && attrOfs + nofs + nameLen * 2 <= recordSize)
                        a.Append(" name='").Append(Encoding.Unicode.GetString(rec, attrOfs + nofs, nameLen * 2)).Append('\'');
                }

                AppLog.Write("DEBUG", a.ToString());
                attrOfs += len;
            }
        }

        private static string AttrName(int type)
        {
            switch (type)
            {
                case AttrStdInfo: return "$STANDARD_INFORMATION";
                case AttrAttrList: return "$ATTRIBUTE_LIST";
                case AttrFileName: return "$FILE_NAME";
                case AttrData: return "$DATA";
                default: return "$0x" + type.ToString("X2");
            }
        }

        /// <summary>Однострочная техническая сводка для сообщения «корень не найден».</summary>
        private static string BuildStats(Collector collector, byte[] slot5, int recNo, int torn,
            long mftBytes, int recordSize, int sectorSize, long clusterSize, List<Run> runs)
        {
            var sb = new StringBuilder(256);
            sb.Append("[stats: recordsRead=").Append(recNo)
              .Append(" entries=").Append(collector.Count)
              .Append(" dirs=").Append(collector.DirCount)
              .Append(" torn=").Append(torn)
              .Append(" mftBytes=").Append(mftBytes)
              .Append(" recordSize=").Append(recordSize)
              .Append(" cluster=").Append(clusterSize)
              .Append(" runs=").Append(runs.Count);
            if (runs.Count > 0)
                sb.Append(" run0=lcn:").Append(runs[0].StartLcn)
                  .Append("*").Append(runs[0].Clusters);
            sb.Append(' ').Append(DescribeSlot5(slot5, recordSize, sectorSize));
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>Что мы увидели в слоте записи 5 (до исправлений — только чтение).</summary>
        private static string DescribeSlot5(byte[] r, int recordSize, int sectorSize)
        {
            if (r == null) return "slot5=not-read";
            if (r.Length < 0x18 || r[0] != 'F' || r[1] != 'I' || r[2] != 'L' || r[3] != 'E')
            {
                var printable = new StringBuilder(8);
                for (int i = 0; i < 4 && i < r.Length; i++)
                    printable.Append(r[i] >= 0x20 && r[i] < 0x7F ? (char)r[i] : '.');
                return "slot5=magic=" + printable;
            }

            ushort usaOfs = BitConverter.ToUInt16(r, 0x04);
            ushort usaCount = BitConverter.ToUInt16(r, 0x06);
            ushort flags = BitConverter.ToUInt16(r, 0x16);
            ushort attrOfs = BitConverter.ToUInt16(r, 0x14);

            int sectors = recordSize / sectorSize;
            bool fixupOk = usaOfs != 0 && usaCount != 0
                           && usaOfs + usaCount * 2 <= r.Length
                           && usaCount == sectors + 1
                           && usaOfs + 1 < r.Length
                           && r[usaOfs] == r[sectorSize - 2] && r[usaOfs + 1] == r[sectorSize - 1];

            // Первый $FILE_NAME (без мутаций) — есть ли вообще имя у корня.
            string fn = "$FN:absent";
            int a = attrOfs;
            while (a >= 0 && a + 8 <= recordSize)
            {
                int type = BitConverter.ToInt32(r, a);
                if (type == unchecked((int)CurrAttribute)) break;
                int len = BitConverter.ToInt32(r, a + 4);
                if (len < 24 || a + len > recordSize) { fn = "$FN:broken-attr"; break; }

                byte nonRes = r[a + 8];
                byte nameLen = r[a + 9];
                if (type == AttrFileName && nameLen == 0 && nonRes == 0)
                {
                    int contentLen = BitConverter.ToInt32(r, a + 0x10);
                    int contentOfs = BitConverter.ToUInt16(r, a + 0x14);
                    if (contentLen >= 0x42 && contentOfs >= 0x18 && a + contentOfs + contentLen <= recordSize)
                    {
                        int c = a + contentOfs;
                        byte nlen = r[c + 0x40];
                        byte ns = r[c + 0x41];
                        string name = nlen > 0 && c + 0x42 + nlen * 2 <= recordSize
                            ? Encoding.Unicode.GetString(r, c + 0x42, nlen * 2)
                            : "";
                        fn = "$FN:clen=" + contentLen + " cofs=" + contentOfs
                             + " nlen=" + nlen + " ns=" + ns + " name=\"" + name + "\"";
                    }
                    else
                    {
                        fn = "$FN:clen=" + contentLen + " cofs=" + contentOfs;
                    }
                    break;
                }
                a += len;
            }

            return "slot5=fixup:" + (fixupOk ? "ok" : "bad")
                   + " flags=0x" + flags.ToString("X4")
                   + " attrOfs=" + attrOfs
                   + " " + fn;
        }

        /// <summary>Применение USA-фиксаций. false = запись повреждена (torn).</summary>
        private static bool ApplyFixups(byte[] rec, int recordSize, int sectorSize)
        {
            ushort usaOfs = BitConverter.ToUInt16(rec, 0x04);
            ushort usaCount = BitConverter.ToUInt16(rec, 0x06);
            if (usaOfs == 0 || usaCount == 0 || usaOfs + usaCount * 2 > rec.Length) return false;
            int sectors = recordSize / sectorSize;
            if (usaCount != sectors + 1) return false;

            byte usn0 = rec[usaOfs];
            byte usn1 = rec[usaOfs + 1];
            for (int i = 1; i < usaCount; i++)
            {
                int end = i * sectorSize - 2;
                if (rec[end] != usn0 || rec[end + 1] != usn1) return false;
                rec[end] = rec[usaOfs + i * 2];
                rec[end + 1] = rec[usaOfs + i * 2 + 1];
            }
            return true;
        }

        private static List<Run> ExtractDataRuns(byte[] rec, int recordSize)
        {
            int attrOfs = BitConverter.ToUInt16(rec, 0x14);
            while (attrOfs >= 0 && attrOfs + 8 <= recordSize)
            {
                int type = BitConverter.ToInt32(rec, attrOfs);
                if (type == unchecked((int)CurrAttribute)) break;
                int len = BitConverter.ToInt32(rec, attrOfs + 4);
                if (len < 24 || attrOfs + len > recordSize) break;

                byte nonRes = rec[attrOfs + 8];
                byte nameLen = rec[attrOfs + 9];
                if (type == AttrData && nameLen == 0 && nonRes != 0)
                {
                    int runOfs = BitConverter.ToUInt16(rec, attrOfs + 0x20);
                    return ParseRuns(rec, attrOfs + runOfs, recordSize);
                }
                attrOfs += len;
            }
            return null;
        }

        private static List<Run> ParseRuns(byte[] rec, int start, int recordSize)
        {
            var runs = new List<Run>(4);
            long lcn = 0;
            int p = start;
            while (p < recordSize && rec[p] != 0)
            {
                byte hdr = rec[p++];
                int lenSize = hdr & 0x0F;
                int offSize = hdr >> 4;
                if (lenSize == 0 || p + lenSize + offSize > recordSize) break;

                long clusters = ReadLe(rec, ref p, lenSize, false);
                if (offSize == 0)
                {
                    runs.Add(new Run { StartLcn = -1, Clusters = clusters, Sparse = true });
                }
                else
                {
                    long off = ReadLe(rec, ref p, offSize, true);
                    lcn += off;
                    runs.Add(new Run { StartLcn = lcn, Clusters = clusters });
                }
            }
            return runs;
        }

        private static long ReadLe(byte[] buf, ref int p, int size, bool signed)
        {
            long v = 0;
            for (int i = 0; i < size; i++)
                v |= ((long)buf[p + i]) << (8 * i);
            if (signed && size < 8 && (buf[p + size - 1] & 0x80) != 0)
                v |= -1L << (8 * size);
            p += size;
            return v;
        }

        /// <summary>
        /// Чтение байтов $MFT по его runlist в буфер. Неспарсовые (sparse) диапазоны обнуляются.
        /// Чтения с тома выравниваются по размеру сектора.
        /// </summary>
        private static int ReadChunk(FileStream vol, List<Run> runs, long clusterSize, int sectorSize,
            long mftOffset, byte[] buf, int dstOff, int count)
        {
            int remaining = count;
            int dst = dstOff;
            long cur = 0;
            long mftEnd = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                long runBytes = runs[i].Clusters * clusterSize;
                if (!runs[i].Sparse) mftEnd = Math.Max(mftEnd, cur + runBytes);
                cur += runBytes;
            }

            cur = 0;
            for (int i = 0; i < runs.Count && remaining > 0; i++)
            {
                Run run = runs[i];
                long runBytes = run.Clusters * clusterSize;
                long runStart = cur;
                long runEnd = cur + runBytes;
                cur = runEnd;

                if (mftOffset + remaining <= runStart || mftOffset >= runEnd) continue;

                long overlapStart = Math.Max(mftOffset, runStart);
                long overlapEnd = Math.Min(mftOffset + remaining, runEnd);
                int segmentLen = (int)(overlapEnd - overlapStart);
                int bufPos = dst + (int)(overlapStart - mftOffset);

                if (run.Sparse)
                {
                    Array.Clear(buf, bufPos, segmentLen);
                }
                else
                {
                    long volOffset = run.StartLcn * clusterSize + (overlapStart - runStart);
                    int aligned = segmentLen - (int)(volOffset % sectorSize);
                    aligned -= aligned % sectorSize;
                    if (aligned > 0)
                        ReadExact(vol, volOffset, buf, bufPos, aligned);
                    if (aligned < segmentLen)
                        Array.Clear(buf, bufPos + aligned, segmentLen - aligned);
                }
            }

            if (mftOffset >= mftEnd) return 0;
            // Важно: mftEnd может превышать int.MaxValue ($MFT > 2 ГБ) — кастить
            // разницу в int нельзя: переполнение даёт отрицательное «прочитано байт»
            // и цикл парсинга не выполняется вовсе (записей: 0).
            long available = mftEnd - mftOffset;
            return available >= count ? count : (int)available;
        }

        private static void ReadExact(FileStream vol, long offset, byte[] buf, int dstOff, int count)
        {
            vol.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = vol.Read(buf, dstOff + read, count - read);
                if (n <= 0) throw new EndOfStreamException(Loc.T("Неожиданный конец тома при чтении $MFT.", "Unexpected end of volume reading $MFT."));
                read += n;
            }
        }

        /// <summary>
        /// Переиспользуемый буфер $FILE_NAME-ссылок одной записи MFT: NTFS хранит отдельный
        /// $FILE_NAME на каждого hardlink-владельца (+ DOS-дубликат 8.3 на ту же папку).
        /// Несколько хардлинков в ОДНОМ каталоге — это разные имена (WizTree считает каждый
        /// путь), поэтому отбрасываем только DOS-алиасы (ns=2), если рядом есть длинное имя.
        /// </summary>
        private sealed class FnScratch
        {
            private readonly List<long> _parents = new List<long>(4);
            private readonly List<string> _names = new List<string>(4);
            private readonly List<byte> _ranks = new List<byte>(4);

            public int Count { get { return _parents.Count; } }
            public List<string> Names { get { return _names; } }
            public List<long> Parents { get { return _parents; } }
            public List<byte> Ranks { get { return _ranks; } }

            public void Reset()
            {
                _parents.Clear();
                _names.Clear();
                _ranks.Clear();
            }

            private static byte Rank(byte ns)
            {
                if (ns == 1 || ns == 3) return 2; // Win32 / Win32+DOS
                return ns == 0 ? (byte)1 : (byte)0; // POSIX / DOS 8.3
            }

            public void AddLink(long parentRec, string name, byte ns)
            {
                byte rank = Rank(ns);
                int sameName = -1;
                bool hasLong = false;
                for (int i = 0; i < _parents.Count; i++)
                {
                    if (_parents[i] != parentRec) continue;
                    if (_names[i] == name) sameName = i;
                    if (_ranks[i] > 0) hasLong = true;
                }

                if (sameName >= 0)
                {
                    if (rank > _ranks[sameName]) _ranks[sameName] = rank;
                    return;
                }

                if (rank == 0)
                {
                    // DOS-алиас: нужен, только если длинного имени в этом каталоге нет.
                    if (hasLong) return;
                }
                else
                {
                    // Длинное имя вытесняет ранее добавленный DOS-алиас той же папки.
                    for (int i = _parents.Count - 1; i >= 0; i--)
                        if (_parents[i] == parentRec && _ranks[i] == 0) RemoveAt(i);
                }

                _parents.Add(parentRec);
                _names.Add(name);
                _ranks.Add(rank);
            }

            private void RemoveAt(int i)
            {
                _parents.RemoveAt(i);
                _names.RemoveAt(i);
                _ranks.RemoveAt(i);
            }
        }

        /// <summary>Сбор сырых записей и преобразование в ScanIndex с маппингом recordNumber → индекс.</summary>
        private sealed class Collector
        {
            private readonly List<int> _recNos = new List<int>(1 << 16);
            private readonly List<string> _names = new List<string>(1 << 16);
            private readonly List<long> _parentRecs = new List<long>(1 << 16);
            private readonly List<long> _sizes = new List<long>(1 << 16);
            private readonly List<long> _allocated = new List<long>(1 << 16);
            private readonly List<uint> _attributes = new List<uint>(1 << 16);
            private readonly List<long> _created = new List<long>(1 << 16);
            private readonly List<long> _lastWrite = new List<long>(1 << 16);
            private readonly List<byte> _ranks = new List<byte>(1 << 16);
            private readonly List<bool> _isDirs = new List<bool>(1 << 16);
            private readonly List<bool> _isReparse = new List<bool>(1 << 16);
            private int _dirCount;
            private int _reparseDirCount;

            /// <summary>Расширенные записи ($ATTRIBUTE_LIST): baseRec → FN-ссылки и размер.</summary>
            private sealed class ExtData
            {
                public readonly List<long> Parents = new List<long>(2);
                public readonly List<string> Names = new List<string>(2);
                public readonly List<byte> Ranks = new List<byte>(2);
                public long Size;
                public long Allocated;
                public uint Attributes;
                public bool GotSize;
                public bool IsDir;
                public bool Reparse;
                public long CreatedUtc;
                public long LastWriteUtc;
            }

            private readonly Dictionary<int, ExtData> _ext = new Dictionary<int, ExtData>(64);

            public int Count { get { return _recNos.Count; } }
            public int DirCount { get { return _dirCount; } }
            public int ReparseDirCount { get { return _reparseDirCount; } }

            public int Add(int recNo, string name, long parentRec, long size, bool isDir, bool reparse,
                byte nameRank = 2, long createdUtc = 0, long lastWriteUtc = 0,
                long allocated = 0, uint attributes = 0)
            {
                _recNos.Add(recNo);
                _names.Add(name);
                _parentRecs.Add(parentRec);
                _sizes.Add(size);
                _allocated.Add(allocated);
                _attributes.Add(attributes);
                _created.Add(createdUtc);
                _lastWrite.Add(lastWriteUtc);
                _ranks.Add(nameRank);
                _isDirs.Add(isDir);
                _isReparse.Add(reparse);
                if (isDir)
                {
                    _dirCount++;
                    if (reparse) _reparseDirCount++;
                }
                return _recNos.Count - 1;
            }

            /// <summary>FN-ссылки и размер из расширенной записи $ATTRIBUTE_LIST по базовой записи.</summary>
            public void AddExtension(int baseRec, FnScratch scratch, long size, bool gotSize, bool isDir,
                bool reparse, long createdUtc = 0, long lastWriteUtc = 0,
                long allocated = 0, uint attributes = 0)
            {
                ExtData e;
                if (!_ext.TryGetValue(baseRec, out e))
                {
                    e = new ExtData();
                    _ext[baseRec] = e;
                }
                if (e.CreatedUtc == 0 && createdUtc != 0) e.CreatedUtc = createdUtc;
                if (e.LastWriteUtc == 0 && lastWriteUtc != 0) e.LastWriteUtc = lastWriteUtc;
                for (int i = 0; i < scratch.Count; i++)
                    AddExtLink(e, scratch.Parents[i], scratch.Names[i], scratch.Ranks[i]);
                // Несколько extension-$DATA (split-атрибут): размер осмысленен у экстента
                // с VCN 0; у остальных поля могут быть нулевыми/меньшими — берём максимум.
                if (gotSize && size > e.Size) { e.Size = size; e.GotSize = true; }
                if (allocated > e.Allocated) e.Allocated = allocated;
                if (e.Attributes == 0 && attributes != 0) e.Attributes = attributes;
                if (isDir) e.IsDir = true;
                if (reparse) e.Reparse = true;
            }

            /// <summary>Добавляет FN расширения: (родитель, имя) уникальны, DOS-алиас уступает длинному.</summary>
            private static void AddExtLink(ExtData e, long parentRec, string name, byte rank)
            {
                int sameName = -1;
                bool hasLong = false;
                for (int i = 0; i < e.Parents.Count; i++)
                {
                    if (e.Parents[i] != parentRec) continue;
                    if (e.Names[i] == name) sameName = i;
                    if (e.Ranks[i] > 0) hasLong = true;
                }
                if (sameName >= 0)
                {
                    if (rank > e.Ranks[sameName]) e.Ranks[sameName] = rank;
                    return;
                }
                if (rank == 0)
                {
                    if (hasLong) return;
                }
                else
                {
                    for (int i = e.Parents.Count - 1; i >= 0; i--)
                        if (e.Parents[i] == parentRec && e.Ranks[i] == 0)
                        {
                            e.Parents.RemoveAt(i); e.Names.RemoveAt(i); e.Ranks.RemoveAt(i);
                        }
                }
                e.Parents.Add(parentRec);
                e.Names.Add(name);
                e.Ranks.Add(rank);
            }

            /// <summary>
            /// Слияние расширенных записей с базовыми после прохода по MFT:
            ///  - FN-ссылки расширения = дополнительные hardlink-пути базовой записи;
            ///  - $DATA-расширения = размер, если в базовой записи $DATA не было;
            ///  - если базовая запись не попала в индекс (torn/без FN) — строки из расширения.
            /// </summary>
            public void MergeExtensions()
            {
                if (_ext.Count == 0) return;

                // recNo → первая строка базовой записи (строки одной записи идут подряд).
                var firstRow = new Dictionary<int, int>(_ext.Count);
                for (int i = 0; i < _recNos.Count && firstRow.Count < _ext.Count; i++)
                {
                    int rec = _recNos[i];
                    if (_ext.ContainsKey(rec) && !firstRow.ContainsKey(rec)) firstRow[rec] = i;
                }

                foreach (var kv in _ext)
                {
                    int recNo = kv.Key;
                    ExtData e = kv.Value;

                    int first;
                    if (!firstRow.TryGetValue(recNo, out first))
                    {
                        // Базовая запись не собрана — создаём строки из расширения (FN там есть).
                        for (int j = 0; j < e.Parents.Count; j++)
                            Add(recNo, e.Names[j], e.Parents[j],
                                e.IsDir ? 0 : e.Size, e.IsDir, e.IsDir && e.Reparse, e.Ranks[j],
                                e.CreatedUtc, e.LastWriteUtc, e.Allocated, e.Attributes);
                        continue;
                    }

                    bool isDir = _isDirs[first];
                    bool reparse = _isReparse[first] || e.Reparse;

                    // Строки записи: сначала подряд идущие строки базовой записи,
                    // затем добавленные здесь имена из расширений.
                    var rows = new List<int>(4);
                    int last = first;
                    while (last + 1 < _recNos.Count && _recNos[last + 1] == recNo) last++;
                    for (int k = first; k <= last; k++) rows.Add(k);

                    // SI базовой записи приоритетнее; расширение подставляет даты,
                    // если в базе они повреждены/отсутствуют (все hardlink-строки сразу).
                    if (e.CreatedUtc != 0 || e.LastWriteUtc != 0)
                        for (int r = 0; r < rows.Count; r++)
                        {
                            int k = rows[r];
                            if (_created[k] == 0) _created[k] = e.CreatedUtc;
                            if (_lastWrite[k] == 0) _lastWrite[k] = e.LastWriteUtc;
                        }

                    // $DATA уехал в расширение (split-атрибут): в базовой записи остался
                    // либо 0, либо FN-фолбэк с устаревшим размером. Реальный размер $DATA
                    // (максимум по экстентам) должен побеждать фолбэк.
                    if (!isDir && e.GotSize && e.Size > _sizes[first])
                        for (int r = 0; r < rows.Count; r++) _sizes[rows[r]] = e.Size;

                    // Аналогично для «На диске» и атрибутов из extension-записи.
                    if (!isDir && e.Allocated > _allocated[first])
                        for (int r = 0; r < rows.Count; r++) _allocated[rows[r]] = e.Allocated;
                    if (e.Attributes != 0)
                        for (int r = 0; r < rows.Count; r++)
                            if (_attributes[rows[r]] == 0) _attributes[rows[r]] = e.Attributes;

                    // reparse-флаг мог быть только в расширении (FN/SI там же).
                    if (isDir && reparse && !_isReparse[first])
                    {
                        if (e.Reparse) _reparseDirCount++;
                        for (int j = first; j <= last; j++) _isReparse[j] = true;
                    }

                    // Дополнительные имена из расширенных записей. Ключ строки — пара
                    // (родитель, имя): хардлинки в одном каталоге дают разные строки,
                    // а DOS-алиас (ns=2) уступает длинному имени.
                    long size = _sizes[first];
                    long allocated = _allocated[first];
                    uint attributes = _attributes[first];
                    for (int j = 0; j < e.Parents.Count; j++)
                    {
                        long p = e.Parents[j];
                        int same = -1, dosRow = -1;
                        bool hasLong = false;
                        for (int r = 0; r < rows.Count; r++)
                        {
                            int k = rows[r];
                            if (_parentRecs[k] != p) continue;
                            if (_names[k] == e.Names[j]) same = k;
                            if (_ranks[k] > 0) hasLong = true;
                            else if (dosRow < 0) dosRow = k;
                        }

                        if (same >= 0)
                        {
                            if (e.Ranks[j] > _ranks[same]) _ranks[same] = e.Ranks[j];
                            continue;
                        }
                        if (e.Ranks[j] == 0)
                        {
                            if (!hasLong)
                                rows.Add(Add(recNo, e.Names[j], p, isDir ? 0 : size, isDir, isDir && reparse,
                                             0, e.CreatedUtc, e.LastWriteUtc, allocated, attributes));
                            continue;
                        }
                        if (dosRow >= 0)
                        {
                            _names[dosRow] = e.Names[j];
                            _ranks[dosRow] = e.Ranks[j];
                            continue;
                        }
                        rows.Add(Add(recNo, e.Names[j], p, isDir ? 0 : size, isDir, isDir && reparse,
                                     e.Ranks[j], e.CreatedUtc, e.LastWriteUtc, allocated, attributes));
                    }
                }
            }

            public ScanIndex ToIndex()
            {
                var dirByRec = new Dictionary<int, int>((int)Math.Min(_names.Count, 1 << 20));
                for (int i = 0; i < _recNos.Count; i++)
                    if (_isDirs[i]) dirByRec[_recNos[i]] = i;

                var index = new ScanIndex();
                var indexOf = new int[_recNos.Count];

                for (int i = 0; i < _recNos.Count; i++)
                {
                    // Родителя разрешаем и для папок, и для файлов — иначе иерархия
                    // каталогов не строится ( дети папок зависят от dirByRec).
                    int parent = -1;
                    int pi;
                    if (_parentRecs[i] >= 0 && _parentRecs[i] <= int.MaxValue &&
                        dirByRec.TryGetValue((int)_parentRecs[i], out pi))
                        parent = pi;
                    indexOf[i] = index.Add(_names[i], parent, _sizes[i], _isDirs[i], _isReparse[i],
                                           _created[i], _lastWrite[i], _allocated[i], _attributes[i]);
                }

                int root;
                if (!dirByRec.TryGetValue(ScanIndex.RootRecord, out root))
                    throw new IOException(Loc.T("Запись корня тома (5) не найдена — MFT повреждён.",
                                                "Root record (5) not found — MFT corrupted."));
                index.RootIndex = indexOf[root];
                index.Parents[index.RootIndex] = -1;
                return index;
            }
        }
    }
}
