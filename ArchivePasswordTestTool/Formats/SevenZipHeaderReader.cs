using System.Buffers.Binary;
using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 7z 归档头里「存储模式快速判错」需要的字段。
    /// 只保留单文件夹 / 单打包流 / 单文件的情形，其余结构在解析阶段就被拒绝。
    /// </summary>
    internal sealed class SevenZipStoreLayout
    {
        /// <summary>AES coder 的 properties（<c>[b0][b1][salt][iv]</c>），交给 <see cref="SevenZipAesKeyDerivation.TryParseProperties"/> 解析</summary>
        public required byte[] AesProperties { get; init; }

        /// <summary>打包流（即密文）的文件绝对偏移</summary>
        public required long PackedOffset { get; init; }

        /// <summary>打包流长度（含 CBC 补齐，因此可能大于原文件大小）</summary>
        public required long PackedSize { get; init; }

        /// <summary>归档内文件名（取扩展名定位格式签名）</summary>
        public required string EntryName { get; init; }
    }

    /// <summary>
    /// 只读 7z 归档头中与「存储模式快速判错」有关的部分。
    /// <para>
    /// 为什么不复用已打开的 <c>IInArchive</c>：7-Zip 不对外暴露 AES coder 的盐与 IV，
    /// 而这两者正是自己派生密钥的唯一必需输入，因此必须自己解析头部。
    /// </para>
    /// <para>
    /// 只处理<b>明文头</b>（第一个字节是 <c>kHeader</c>）。头被编码（<c>kEncodedHeader</c>）时
    /// 需要先解压甚至解密才能读到 coder 与文件名，那等于把 7z 解码器重写一遍，不做——
    /// 这时返回 <see langword="null"/> 退回完整测密。实测单文件 <c>-mx=0</c> 的归档头都是明文；
    /// 多文件 <c>-mx=0</c> 的头会被 LZMA 编码，而多文件本来也不在支持范围内。
    /// </para>
    /// </summary>
    internal static class SevenZipHeaderReader
    {
        /// <summary>7z 签名头长度</summary>
        private const int SignatureSize = 32;

        /// <summary>头部读取上限：正常 7z 头只有几 KB，超过即视为异常（防御畸形文件）</summary>
        private const int MaxHeaderSize = 4 * 1024 * 1024;

        /// <summary>单个 folder 支持的 coder 数上限（目标是 [AES, Copy] 两个）</summary>
        private const int MaxCoders = 4;

        /// <summary>单个归档支持的 folder / 打包流数上限</summary>
        private const int MaxFolders = 64;

        private static readonly byte[] Signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

        /// <summary>7zAES 的方法 ID（<c>06 F1 07 01</c>）</summary>
        private static readonly byte[] AesMethodId = [0x06, 0xF1, 0x07, 0x01];

        /// <summary>7z 的「不压缩 / 存储」方法 ID</summary>
        private const byte CopyMethodId = 0x00;

        // 段 ID（数字即 7z 格式规范里的取值）
        private const byte KEnd = 0x00;
        private const byte KHeader = 0x01;
        private const byte KArchiveProperties = 0x02;
        private const byte KAdditionalStreamsInfo = 0x03;
        private const byte KMainStreamsInfo = 0x04;
        private const byte KFilesInfo = 0x05;
        private const byte KPackInfo = 0x06;
        private const byte KUnPackInfo = 0x07;
        private const byte KSubStreamsInfo = 0x08;
        private const byte KSize = 0x09;
        private const byte KCRC = 0x0A;
        private const byte KFolder = 0x0B;
        private const byte KCodersUnPackSize = 0x0C;
        private const byte KNumUnPackStream = 0x0D;
        private const byte KEmptyStream = 0x0E;
        private const byte KName = 0x11;

        /// <summary>
        /// 读出快速判错所需的布局；结构不受支持或读取失败时返回 <see langword="null"/>。
        /// </summary>
        /// <param name="archivePath">归档路径</param>
        /// <param name="signatureOffset">7z 签名在文件中的偏移（SFX 时非 0）</param>
        public static SevenZipStoreLayout? TryRead(string archivePath, long signatureOffset)
        {
            try
            {
                using var stream = new FileStream(
                    archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);

                return TryRead(stream, signatureOffset);
            }
            catch (Exception)
            {
                // 读文件失败/越界：只是放弃优化，不影响正常测密
                return null;
            }
        }

        private static SevenZipStoreLayout? TryRead(FileStream stream, long signatureOffset)
        {
            if (signatureOffset < 0 || stream.Length < signatureOffset + SignatureSize)
            {
                return null;
            }

            byte[] signature = new byte[SignatureSize];
            stream.Position = signatureOffset;
            if (!TryReadExactly(stream, signature))
            {
                return null;
            }

            if (!signature.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            {
                return null;
            }

            ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(signature.AsSpan(12));
            ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(signature.AsSpan(20));

            if (nextHeaderSize == 0 || nextHeaderSize > MaxHeaderSize)
            {
                return null;
            }

            // NextHeaderOffset 相对「签名头之后」，即文件偏移 signatureOffset + 32
            long headerStart = signatureOffset + SignatureSize + (long)nextHeaderOffset;
            if (headerStart < 0 || headerStart + (long)nextHeaderSize > stream.Length)
            {
                return null;
            }

            byte[] header = new byte[(int)nextHeaderSize];
            stream.Position = headerStart;
            if (!TryReadExactly(stream, header))
            {
                return null;
            }

            return Parse(header, signatureOffset);
        }

        private static SevenZipStoreLayout? Parse(byte[] header, long signatureOffset)
        {
            var reader = new SpanReader(header);

            if (!reader.TryReadByte(out byte section) || section != KHeader)
            {
                // kEncodedHeader（0x17）等：需要解压/解密头部才能继续，不支持
                return null;
            }

            long packPos = 0;
            int packStreamCount = 0;
            ulong packSize = 0;

            int folderCount = 0;
            CoderInfo[] coders = [];
            ulong[] unpackSizes = [];
            bool chainIsAesThenCopy = false;
            int numUnpackStreams = 1;

            int fileCount = -1;
            bool firstEntryIsEmpty = false;
            string? entryName = null;

            bool reachedEnd = false;
            while (!reachedEnd && reader.TryReadByte(out section))
            {
                switch (section)
                {
                    case KEnd:
                        reachedEnd = true;
                        break;

                    case KMainStreamsInfo:
                        if (!TryReadStreamsInfo(ref reader, out StreamsInfo info))
                        {
                            return null;
                        }

                        packPos = (long)info.PackPos;
                        packStreamCount = info.PackSizes.Count;
                        packSize = info.PackSizes.Count > 0 ? info.PackSizes[0] : 0;
                        folderCount = info.FolderCount;
                        coders = info.FirstFolderCoders;
                        unpackSizes = info.FirstFolderUnpackSizes;
                        chainIsAesThenCopy = info.FirstFolderIsAesThenCopy;

                        // kSubStreamsInfo 未出现时，每个 folder 默认 1 个子流
                        numUnpackStreams = info.NumUnpackStreams.Count > 0 ? info.NumUnpackStreams[0] : 1;
                        break;

                    case KAdditionalStreamsInfo:
                        // 与主数据无关，丢弃即可；仍要解析，否则定位不到后续段
                        if (!TryReadStreamsInfo(ref reader, out _))
                        {
                            return null;
                        }

                        break;

                    case KArchiveProperties:
                        if (!TrySkipProperties(ref reader))
                        {
                            return null;
                        }

                        break;

                    case KFilesInfo:
                        if (!TryReadFilesInfo(ref reader, out fileCount, out firstEntryIsEmpty, out entryName))
                        {
                            return null;
                        }

                        break;

                    default:
                        return null;
                }
            }

            if (!reachedEnd)
            {
                return null;
            }

            // 目标：单文件夹、单打包流、单文件，且编码链恰好是 [AES, Copy]
            if (folderCount != 1 || packStreamCount != 1 || fileCount != 1)
            {
                return null;
            }

            if (firstEntryIsEmpty || entryName is null || !chainIsAesThenCopy || coders.Length != 2)
            {
                return null;
            }

            if (coders[0].Properties is not { } aesProperties || unpackSizes.Length != 2 || numUnpackStreams != 1)
            {
                return null;
            }

            // AES-CBC 把数据补齐到 16 的倍数；最后一个 coder（Copy）的输出才是原文件大小
            const int blockSize = SevenZipAesKeyDerivation.BlockSize;
            ulong unpackedSize = unpackSizes[1];
            ulong expectedPackedSize = (unpackedSize + blockSize - 1) / blockSize * blockSize;

            if (packSize != expectedPackedSize || packSize < blockSize)
            {
                return null;
            }

            long packedOffset = signatureOffset + SignatureSize + packPos;
            if (packedOffset < 0)
            {
                return null;
            }

            return new SevenZipStoreLayout
            {
                AesProperties = aesProperties,
                PackedOffset = packedOffset,
                PackedSize = (long)packSize,
                EntryName = entryName,
            };
        }

        /// <summary>StreamsInfo 里与快速判错相关的部分（只保留第一个 folder 的细节）</summary>
        private struct StreamsInfo
        {
            public ulong PackPos;
            public List<ulong> PackSizes;
            public int FolderCount;
            public CoderInfo[] FirstFolderCoders;
            public ulong[] FirstFolderUnpackSizes;
            public bool FirstFolderIsAesThenCopy;
            public bool FirstFolderCrcDefined;
            public List<int> NumUnpackStreams;
        }

        private readonly struct CoderInfo(byte[] method, byte[]? properties)
        {
            public byte[] Method { get; } = method;

            public byte[]? Properties { get; } = properties;
        }

        private static bool TryReadStreamsInfo(ref SpanReader reader, out StreamsInfo info)
        {
            info = new StreamsInfo
            {
                PackSizes = [],
                FirstFolderCoders = [],
                FirstFolderUnpackSizes = [],
                NumUnpackStreams = [],
            };

            while (reader.TryReadByte(out byte section))
            {
                switch (section)
                {
                    case KEnd:
                        return true;

                    case KPackInfo:
                        if (!TryReadPackInfo(ref reader, ref info))
                        {
                            return false;
                        }

                        break;

                    case KUnPackInfo:
                        if (!TryReadUnPackInfo(ref reader, ref info))
                        {
                            return false;
                        }

                        break;

                    case KSubStreamsInfo:
                        if (!TryReadSubStreamsInfo(ref reader, ref info))
                        {
                            return false;
                        }

                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        private static bool TryReadPackInfo(ref SpanReader reader, ref StreamsInfo info)
        {
            if (!reader.TryReadNumber(out ulong packPos) || !reader.TryReadNumber(out ulong count) || count > MaxFolders)
            {
                return false;
            }

            info.PackPos = packPos;
            for (ulong i = 0; i < count; i++)
            {
                info.PackSizes.Add(0);
            }

            while (reader.TryReadByte(out byte section))
            {
                switch (section)
                {
                    case KEnd:
                        return true;

                    case KSize:
                        for (int i = 0; i < info.PackSizes.Count; i++)
                        {
                            if (!reader.TryReadNumber(out ulong size))
                            {
                                return false;
                            }

                            info.PackSizes[i] = size;
                        }

                        break;

                    case KCRC:
                        if (!TrySkipDigests(ref reader, info.PackSizes.Count, out _))
                        {
                            return false;
                        }

                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        private static bool TryReadUnPackInfo(ref SpanReader reader, ref StreamsInfo info)
        {
            var folderCoders = new List<CoderInfo[]>();
            var folderChains = new List<bool>();
            var folderUnpackSizes = new List<ulong[]>();

            while (reader.TryReadByte(out byte section))
            {
                switch (section)
                {
                    case KEnd:
                        info.FolderCount = folderCoders.Count;
                        info.FirstFolderCoders = folderCoders.Count > 0 ? folderCoders[0] : [];
                        info.FirstFolderIsAesThenCopy = folderChains.Count > 0 && folderChains[0];
                        info.FirstFolderUnpackSizes = folderUnpackSizes.Count > 0 ? folderUnpackSizes[0] : [];
                        return true;

                    case KFolder:
                        {
                            if (!reader.TryReadNumber(out ulong folderCount) || folderCount > MaxFolders)
                            {
                                return false;
                            }

                            if (!reader.TryReadByte(out byte external) || external != 0)
                            {
                                // external == 1：folder 数据存在别处，不支持
                                return false;
                            }

                            for (ulong i = 0; i < folderCount; i++)
                            {
                                if (!TryReadFolder(ref reader, out CoderInfo[] folderCoderList, out bool isAesThenCopy))
                                {
                                    return false;
                                }

                                folderCoders.Add(folderCoderList);
                                folderChains.Add(isAesThenCopy);
                                folderUnpackSizes.Add([]);
                            }

                            break;
                        }

                    case KCodersUnPackSize:
                        {
                            for (int f = 0; f < folderCoders.Count; f++)
                            {
                                int outStreams = folderCoders[f].Length;
                                var sizes = new ulong[outStreams];
                                for (int i = 0; i < outStreams; i++)
                                {
                                    if (!reader.TryReadNumber(out sizes[i]))
                                    {
                                        return false;
                                    }
                                }

                                folderUnpackSizes[f] = sizes;
                            }

                            break;
                        }

                    case KCRC:
                        if (!TrySkipDigests(ref reader, folderCoders.Count, out bool firstFolderCrcDefined))
                        {
                            return false;
                        }

                        if (folderCoders.Count > 0)
                        {
                            info.FirstFolderCrcDefined = firstFolderCrcDefined;
                        }

                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 解析单个 folder 的 coder 列表、绑定对与打包流索引，并判断它是否恰为 [AES, Copy] 的直连链。
        /// 复杂 coder（多入多出）与 alternative methods 一律拒绝。
        /// </summary>
        private static bool TryReadFolder(ref SpanReader reader, out CoderInfo[] coders, out bool isAesThenCopy)
        {
            coders = [];
            isAesThenCopy = false;

            if (!reader.TryReadNumber(out ulong numCoders) || numCoders == 0 || numCoders > MaxCoders)
            {
                return false;
            }

            coders = new CoderInfo[numCoders];
            int totalInStreams = 0;
            int totalOutStreams = 0;

            for (int i = 0; i < coders.Length; i++)
            {
                if (!reader.TryReadByte(out byte flags))
                {
                    return false;
                }

                int methodSize = flags & 0x0F;
                if (methodSize == 0 || (flags & 0x10) != 0 || (flags & 0x80) != 0)
                {
                    // methodSize == 0 → 复杂 coder 的位掩码形式；
                    // bit4 → 多入多出；bit7 → alternative methods
                    return false;
                }

                if (!reader.TrySlice(methodSize, out ReadOnlySpan<byte> method))
                {
                    return false;
                }

                byte[]? properties = null;
                if ((flags & 0x20) != 0)
                {
                    if (!reader.TryReadNumber(out ulong propertiesSize) || propertiesSize > 1024)
                    {
                        return false;
                    }

                    if (!reader.TrySlice((int)propertiesSize, out ReadOnlySpan<byte> rawProperties))
                    {
                        return false;
                    }

                    properties = rawProperties.ToArray();
                }

                coders[i] = new CoderInfo(method.ToArray(), properties);
                totalInStreams++;
                totalOutStreams++;
            }

            // 绑定对：把某个 coder 的输出接到另一个 coder 的输入
            int bindPairCount = totalOutStreams - 1;
            int firstBindIn = -1;
            int firstBindOut = -1;
            for (int i = 0; i < bindPairCount; i++)
            {
                if (!reader.TryReadNumber(out ulong inIndex) || !reader.TryReadNumber(out ulong outIndex))
                {
                    return false;
                }

                if (i == 0)
                {
                    firstBindIn = (int)inIndex;
                    firstBindOut = (int)outIndex;
                }
            }

            // 打包流数为 1 时索引由约束推导，不占字节；多个时显式列出
            int packedStreamCount = totalInStreams - bindPairCount;
            if (packedStreamCount <= 0)
            {
                return false;
            }

            if (packedStreamCount > 1 && !reader.TrySkip(packedStreamCount))
            {
                return false;
            }

            // 只认「两个简单 coder，且第 2 个的输入来自第 1 个的输出」：
            // 这样唯一的未绑定输入是 coder[0]，即打包流先喂 AES、AES 的输出再喂 Copy
            isAesThenCopy = coders.Length == 2
                && firstBindIn == 1
                && firstBindOut == 0
                && coders[0].Method.AsSpan().SequenceEqual(AesMethodId)
                && coders[1].Method.Length == 1
                && coders[1].Method[0] == CopyMethodId
                && coders[0].Properties is not null;

            return true;
        }

        private static bool TryReadSubStreamsInfo(ref SpanReader reader, ref StreamsInfo info)
        {
            while (reader.TryReadByte(out byte section))
            {
                switch (section)
                {
                    case KEnd:
                        return true;

                    case KNumUnPackStream:
                        {
                            for (int i = 0; i < info.FolderCount; i++)
                            {
                                if (!reader.TryReadNumber(out ulong value) || value > int.MaxValue)
                                {
                                    return false;
                                }

                                info.NumUnpackStreams.Add((int)value);
                            }

                            break;
                        }

                    case KSize:
                        {
                            // 只有子流数 > 1 的 folder 才有条目，且每个只写 n-1 个（最后一个由 folder 大小推出）
                            int count = 0;
                            for (int i = 0; i < info.FolderCount; i++)
                            {
                                count += Math.Max(0, StreamsInFolder(info, i) - 1);
                            }

                            if (!reader.TrySkip(count))
                            {
                                return false;
                            }

                            break;
                        }

                    case KCRC:
                        {
                            // 摘要数 = 子流数 > 1 或 folder 级 CRC 未定义的 folder 的子流数之和
                            int digests = 0;
                            for (int i = 0; i < info.FolderCount; i++)
                            {
                                int streams = StreamsInFolder(info, i);
                                bool crcDefined = i == 0 && info.FirstFolderCrcDefined;
                                if (streams != 1 || !crcDefined)
                                {
                                    digests += streams;
                                }
                            }

                            if (!TrySkipDigests(ref reader, digests, out _))
                            {
                                return false;
                            }

                            break;
                        }

                    default:
                        return false;
                }
            }

            return false;
        }

        private static int StreamsInFolder(StreamsInfo info, int folderIndex) =>
            folderIndex < info.NumUnpackStreams.Count ? info.NumUnpackStreams[folderIndex] : 1;

        private static bool TryReadFilesInfo(
            ref SpanReader reader,
            out int fileCount,
            out bool firstEntryIsEmpty,
            out string? entryName)
        {
            fileCount = -1;
            firstEntryIsEmpty = false;
            entryName = null;

            if (!reader.TryReadNumber(out ulong count) || count > 1_000_000)
            {
                return false;
            }

            fileCount = (int)count;

            while (reader.TryReadByte(out byte property))
            {
                if (property == KEnd)
                {
                    return true;
                }

                if (!reader.TryReadNumber(out ulong size) || size > MaxHeaderSize)
                {
                    return false;
                }

                if (!reader.TrySlice((int)size, out ReadOnlySpan<byte> payload))
                {
                    return false;
                }

                switch (property)
                {
                    case KEmptyStream:
                        // 位图 MSB 优先：最高位对应第 0 个条目
                        firstEntryIsEmpty = payload.Length > 0 && (payload[0] & 0x80) != 0;
                        break;

                    case KName:
                        if (!TryReadFirstEntryName(payload, out entryName))
                        {
                            return false;
                        }

                        break;
                }
            }

            return false;
        }

        /// <summary>从 kName 属性里取第一个名字；UTF-16LE、以 0x0000 结尾</summary>
        private static bool TryReadFirstEntryName(ReadOnlySpan<byte> payload, out string? name)
        {
            name = null;

            if (payload.Length < 1 || payload[0] != 0)
            {
                // external == 1：名字存在别处，不支持
                return false;
            }

            ReadOnlySpan<byte> names = payload[1..];
            int end = 0;
            while (end + 1 < names.Length && !(names[end] == 0 && names[end + 1] == 0))
            {
                end += 2;
            }

            if (end == 0)
            {
                return false;
            }

            name = Encoding.Unicode.GetString(names[..end]);
            return true;
        }

        /// <summary>
        /// 跳过摘要表：先一个「是否全部定义」标记，未全定义时再跟一个位图，
        /// 然后每个已定义的条目 4 字节 CRC。<paramref name="firstDefined"/> 报告第 0 个条目是否已定义。
        /// </summary>
        private static bool TrySkipDigests(ref SpanReader reader, int count, out bool firstDefined)
        {
            firstDefined = false;

            if (count <= 0)
            {
                return true;
            }

            if (!reader.TryReadByte(out byte allAreDefined))
            {
                return false;
            }

            int defined = count;
            if (allAreDefined == 0)
            {
                int bitmapSize = (count + 7) / 8;
                if (!reader.TrySlice(bitmapSize, out ReadOnlySpan<byte> bitmap))
                {
                    return false;
                }

                defined = 0;
                for (int i = 0; i < count; i++)
                {
                    if ((bitmap[i >> 3] & (0x80 >> (i & 7))) != 0)
                    {
                        defined++;
                    }
                }

                firstDefined = (bitmap[0] & 0x80) != 0;
            }
            else
            {
                firstDefined = true;
            }

            return reader.TrySkip(defined * sizeof(uint));
        }

        private static bool TrySkipProperties(ref SpanReader reader)
        {
            while (reader.TryReadByte(out byte property))
            {
                if (property == KEnd)
                {
                    return true;
                }

                if (!reader.TryReadNumber(out ulong size) || size > MaxHeaderSize)
                {
                    return false;
                }

                if (!reader.TrySkip((int)size))
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer) =>
            stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;

        /// <summary>7z 头部字节流上的顺序读取器</summary>
        private ref struct SpanReader
        {
            private readonly byte[] _data;
            private int _position;

            public SpanReader(byte[] data)
            {
                _data = data;
                _position = 0;
            }

            public bool TryReadByte(out byte value)
            {
                if (_position >= _data.Length)
                {
                    value = 0;
                    return false;
                }

                value = _data[_position++];
                return true;
            }

            /// <summary>
            /// 7z 的变长整数：首字节从高位起每遇到 1 就多读一个字节，
            /// 遇到第一个 0 后用该位以下的剩余位作为最高一段。
            /// </summary>
            public bool TryReadNumber(out ulong value)
            {
                value = 0;

                if (!TryReadByte(out byte first))
                {
                    return false;
                }

                byte mask = 0x80;
                for (int i = 0; i < 8; i++)
                {
                    if ((first & mask) == 0)
                    {
                        value |= (ulong)(first & (mask - 1)) << (8 * i);
                        return true;
                    }

                    if (!TryReadByte(out byte next))
                    {
                        return false;
                    }

                    value |= (ulong)next << (8 * i);
                    mask >>= 1;
                }

                return true;
            }

            public bool TrySlice(int count, out ReadOnlySpan<byte> value)
            {
                if (count < 0 || count > _data.Length - _position)
                {
                    value = default;
                    return false;
                }

                value = _data.AsSpan(_position, count);
                _position += count;
                return true;
            }

            public bool TrySkip(int count) => TrySlice(count, out _);
        }
    }
}
