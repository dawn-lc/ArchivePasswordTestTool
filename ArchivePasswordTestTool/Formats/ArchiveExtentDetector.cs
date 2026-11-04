using System.Buffers.Binary;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 归档数据在文件中的真实范围 <c>[Start, End)</c>。
    /// <para>
    /// <see cref="Start"/> 之前与 <see cref="End"/> 之后的字节都不属于归档，例如自解压包的 PE stub、
    /// 被拼接在头部的其它文件、下载工具追加的尾部垃圾。<see cref="End"/> 为 <see langword="null"/>
    /// 表示无法确定（头部被加密、结构损坏或超出扫描窗口），此时只能给出起始位置。
    /// </para>
    /// </summary>
    internal sealed record ArchiveExtent(long Start, long? End)
    {
        /// <summary>尾随的非归档字节数；<see cref="End"/> 未知时为 <see langword="null"/></summary>
        public long? TrailingBytes(long fileLength) => End is long end ? Math.Max(0, fileLength - end) : null;
    }

    /// <summary>
    /// 计算归档在文件中的精确范围：起始已知（格式检测得到签名偏移），终点按各格式自己的结构走一遍得出。
    /// <para>
    /// 为什么值得单独算终点：7-Zip 对尾随垃圾是宽容的（实测三种格式各追加 16MB 仍能完整通过测试），
    /// 所以「能读」并不需要它；需要它是为了**分离**——把 <c>[前缀][归档][尾随]</c> 里的归档单独拷出来，
    /// 得到一个其它工具也能直接用的干净文件。
    /// </para>
    /// <para>
    /// 各格式的判定依据（都是格式自身的结构，不是启发式）：
    /// <list type="bullet">
    /// <item>7z：起始头里的 <c>NextHeaderOffset</c> / <c>NextHeaderSize</c>，终点 = 32 + 两者之和</item>
    /// <item>RAR4：块链走到 <c>0x7B</c>（结束块），每块的 <c>HEAD_SIZE</c> 加可选的 <c>ADD_SIZE</c></item>
    /// <item>RAR5：头部链走到类型 5（结束头）；遇到类型 4（归档加密头）即停止并对终点返回未知</item>
    /// <item>Zip：从文件尾向前找中央目录结束记录，并用「中央目录紧贴在它之前」这一事实校验候选</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class ArchiveExtentDetector
    {
        /// <summary>尾随数据的扫描上限。超出即认为终点未知——尾部追加超过 8MB 垃圾极不常见，不值得为它读更多</summary>
        private const int MaxTrailingScanBytes = 8 * 1024 * 1024;

        /// <summary>头部链遍历的次数上限，防御畸形文件导致的上百万次随机读</summary>
        private const int MaxBlockIterations = 1 << 20;

        private static readonly byte[] Signature7z = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

        /// <summary>Zip 中央目录结束记录（EOCD）签名</summary>
        private static readonly byte[] SignatureZipEnd = [0x50, 0x4B, 0x05, 0x06];

        /// <summary>Zip 中央目录文件头签名（校验 EOCD 候选时用）</summary>
        private static readonly byte[] SignatureZipCentral = [0x50, 0x4B, 0x01, 0x02];

        private const int ZipEocdFixedSize = 22;

        /// <summary>
        /// 探测归档范围。失败或无法确定时返回 <see langword="null"/>，调用方应视为「没有可分离的多余数据」。
        /// </summary>
        /// <param name="archivePath">归档路径</param>
        /// <param name="format">格式 GUID（来自格式检测）</param>
        /// <param name="signatureOffset">签名偏移（来自格式检测）</param>
        public static ArchiveExtent? Detect(string archivePath, Guid format, long signatureOffset)
        {
            if (signatureOffset < 0)
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(archivePath);
                long fileLength = stream.Length;

                if (fileLength <= signatureOffset)
                {
                    return null;
                }

                if (format == SevenZipGuids.CLSID_CFormat7z)
                {
                    return DetectSevenZip(stream, signatureOffset, fileLength);
                }

                if (format == SevenZipGuids.CLSID_CFormatZip)
                {
                    return DetectZip(stream, signatureOffset, fileLength);
                }

                // RAR5 与 RAR4 在 7-Zip 里是两个不同的 CLSID，但格式检测返回的是同一个家族的 GUID 之一
                if (format == SevenZipGuids.CLSID_CFormatRar5)
                {
                    return DetectRar5(stream, signatureOffset, fileLength);
                }

                if (format == SevenZipGuids.CLSID_CFormatRar)
                {
                    return DetectRar4(stream, signatureOffset, fileLength);
                }

                return null;
            }
            catch (Exception)
            {
                // 探测失败不该影响测密本身——范围只用于展示与分离，读不到就当作未知
                return null;
            }
        }

        // ---------- 7z ----------

        /// <summary>
        /// 7z 的起始头固定 32 字节，其中 <c>NextHeaderOffset</c>(12..20) 与 <c>NextHeaderSize</c>(20..28)
        /// 都是相对起始头之后的位置计算，因此终点可以直接算出来，不需要读整个文件。
        /// </summary>
        private static ArchiveExtent? DetectSevenZip(FileStream stream, long start, long fileLength)
        {
            Span<byte> header = stackalloc byte[32];
            stream.Position = start;

            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            {
                return null;
            }

            if (!header.StartsWith(Signature7z))
            {
                return null;
            }

            ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
            ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);

            // 用无符号运算判断溢出，再与文件长度比较；畸形值一律当作未知
            ulong end = 32UL + nextHeaderOffset + nextHeaderSize;
            if (end > (ulong)fileLength || end < 32UL + nextHeaderOffset)
            {
                return null;
            }

            return new ArchiveExtent(start, start + (long)end);
        }

        // ---------- RAR4 ----------

        /// <summary>
        /// RAR4 块头：<c>HEAD_CRC(2) HEAD_TYPE(1) HEAD_FLAGS(2) HEAD_SIZE(2)</c>，标志位 <c>0x8000</c>
        /// 表示头后还有 <c>ADD_SIZE(4)</c> 的附加数据。块链走到 <c>0x7B</c>（结束块）即归档末尾。
        /// </summary>
        private static ArchiveExtent? DetectRar4(FileStream stream, long start, long fileLength)
        {
            const ushort FlagAddSize = 0x8000;
            const byte EndBlockType = 0x7B;
            const int BlockHeaderSize = 7;

            Span<byte> header = stackalloc byte[BlockHeaderSize];

            // stackalloc 必须在循环外：写在循环体内会随迭代累积栈空间（CA2014）
            Span<byte> addSize = stackalloc byte[4];
            long position = start + 7; // 跳过签名

            for (int i = 0; i < MaxBlockIterations; i++)
            {
                if (position + BlockHeaderSize > fileLength)
                {
                    return null;
                }

                stream.Position = position;
                if (stream.ReadAtLeast(header, BlockHeaderSize, throwOnEndOfStream: false) != BlockHeaderSize)
                {
                    return null;
                }

                byte type = header[2];
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[3..]);
                ushort headSize = BinaryPrimitives.ReadUInt16LittleEndian(header[5..]);

                if (headSize < BlockHeaderSize)
                {
                    return null;
                }

                // 只认 RAR4 实际会出现的块类型；出现别的说明结构不符（例如头部把整包加密了），不要瞎猜
                if (type is not ((byte)0x72) and not ((byte)0x73) and not ((byte)0x74)
                    and not ((byte)0x75) and not ((byte)0x76) and not ((byte)0x77)
                    and not ((byte)0x78) and not ((byte)0x79) and not ((byte)0x7A)
                    and not ((byte)0x7B))
                {
                    return null;
                }

                long blockLength = headSize;
                if ((flags & FlagAddSize) != 0)
                {
                    if (position + BlockHeaderSize + 4 > fileLength)
                    {
                        return null;
                    }

                    stream.Position = position + BlockHeaderSize;
                    if (stream.ReadAtLeast(addSize, 4, throwOnEndOfStream: false) != 4)
                    {
                        return null;
                    }

                    blockLength += BinaryPrimitives.ReadUInt32LittleEndian(addSize);
                }

                if (position + blockLength > fileLength)
                {
                    return null;
                }

                if (type == EndBlockType)
                {
                    return new ArchiveExtent(start, position + blockLength);
                }

                position += blockLength;
            }

            return null;
        }

        // ---------- RAR5 ----------

        /// <summary>
        /// RAR5 通用头：<c>CRC32(4) + headerSize(vint) + type(vint) + flags(vint) + [extraSize][dataSize]</c>，
        /// <c>headerSize</c> 从 type 字段起算（不含自身），因此头尾位置 = type 起点 + headerSize + dataSize。
        /// 走到类型 5（结束头）即末尾；遇到类型 4（归档加密头）后所有头部都是密文，无法继续遍历。
        /// </summary>
        private static ArchiveExtent? DetectRar5(FileStream stream, long start, long fileLength)
        {
            const ulong HeaderFlagExtraArea = 0x0001;
            const ulong HeaderFlagDataArea = 0x0002;
            const ulong HeaderTypeMain = 1;
            const ulong HeaderTypeFile = 2;
            const ulong HeaderTypeService = 3;
            const ulong HeaderTypeArchiveEncryption = 4;
            const ulong HeaderTypeEndOfArchive = 5;

            long position = start + 8; // 跳过签名

            for (int i = 0; i < MaxBlockIterations; i++)
            {
                if (position + 4 > fileLength)
                {
                    return null;
                }

                stream.Position = position + 4; // 跳过 CRC32

                if (ReadVint(stream) is not ulong headerSize)
                {
                    return null;
                }

                long bodyStart = stream.Position;

                if (ReadVint(stream) is not ulong type)
                {
                    return null;
                }

                if (ReadVint(stream) is not ulong flags)
                {
                    return null;
                }

                ulong dataSize = 0;
                if ((flags & HeaderFlagExtraArea) != 0 && ReadVint(stream) is null)
                {
                    return null;
                }

                if ((flags & HeaderFlagDataArea) != 0)
                {
                    if (ReadVint(stream) is not ulong size)
                    {
                        return null;
                    }

                    dataSize = size;
                }

                long bodyEnd = bodyStart + (long)headerSize;
                long dataEnd = bodyEnd + (long)dataSize;

                if (bodyEnd < bodyStart || dataEnd < bodyEnd || dataEnd > fileLength)
                {
                    return null;
                }

                if (type == HeaderTypeArchiveEncryption)
                {
                    // 其后每个头都是「16 字节 IV + 密文块」，无法判断哪里是真正的末尾
                    return new ArchiveExtent(start, null);
                }

                if (type == HeaderTypeEndOfArchive)
                {
                    return new ArchiveExtent(start, dataEnd);
                }

                if (type is not HeaderTypeMain and not HeaderTypeFile and not HeaderTypeService)
                {
                    return null;
                }

                position = dataEnd;
            }

            return null;
        }

        /// <summary>读一个 RAR5 变长整数；读到文件末尾或超过 10 字节即返回 null</summary>
        private static ulong? ReadVint(FileStream stream)
        {
            ulong value = 0;
            int shift = 0;

            for (int i = 0; i < 10; i++)
            {
                int current = stream.ReadByte();
                if (current < 0)
                {
                    return null;
                }

                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
            }

            return null;
        }

        // ---------- Zip ----------

        /// <summary>
        /// Zip 的末尾是中央目录结束记录（EOCD）。它不是固定位置，只能从文件尾向前找，而且尾部可能有垃圾，
        /// 所以不能靠「EOCD 之后没有多余字节」来判断——这里改用一条结构性事实校验候选：
        /// <b>中央目录紧贴在 EOCD 之前</b>，因此 <c>EOCD 位置 - 中央目录大小</c> 处必须是中央目录文件头签名。
        /// 这条校验与是否存在前缀无关（前缀会同等平移两者），能可靠排除垃圾里偶然出现的 EOCD 签名。
        /// </summary>
        private static ArchiveExtent? DetectZip(FileStream stream, long start, long fileLength)
        {
            long scanLength = Math.Min(fileLength - start, MaxTrailingScanBytes);
            if (scanLength < ZipEocdFixedSize)
            {
                return null;
            }

            long scanStart = fileLength - scanLength;
            byte[] tail = new byte[scanLength];

            stream.Position = scanStart;
            if (stream.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false) != tail.Length)
            {
                return null;
            }

            // 从后往前找：最靠后的有效 EOCD 才是真正的末尾
            for (long index = scanLength - ZipEocdFixedSize; index >= 0; index--)
            {
                if (!tail.AsSpan((int)index).StartsWith(SignatureZipEnd))
                {
                    continue;
                }

                long eocdPosition = scanStart + index;
                if (eocdPosition < start)
                {
                    continue;
                }

                Span<byte> eocd = tail.AsSpan((int)index, ZipEocdFixedSize);

                int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(eocd[20..]);
                long end = eocdPosition + ZipEocdFixedSize + commentLength;
                if (end > fileLength)
                {
                    continue;
                }

                int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
                uint centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);

                if (entryCount > 0)
                {
                    long centralDirectoryStart = eocdPosition - centralDirectorySize;
                    if (centralDirectoryStart < start || centralDirectoryStart + 4 > eocdPosition)
                    {
                        continue;
                    }

                    stream.Position = centralDirectoryStart;
                    Span<byte> probe = stackalloc byte[4];
                    if (stream.ReadAtLeast(probe, 4, throwOnEndOfStream: false) != 4
                        || !probe.SequenceEqual(SignatureZipCentral))
                    {
                        continue;
                    }
                }

                return new ArchiveExtent(start, end);
            }

            return null;
        }
    }
}
