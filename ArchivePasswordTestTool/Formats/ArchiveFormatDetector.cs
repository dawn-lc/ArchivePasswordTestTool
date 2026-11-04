using System.Collections.Concurrent;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 压缩包格式检测：按文件头签名（或 PE 封装 SFX 的 stub 后签名扫描）识别格式与数据偏移。
    /// 消费方：<see cref="ArchiveContext"/>（构造时确定格式与 maxCheckStartPosition）、
    /// 各快筛探测器（定位签名偏移）。纯托管实现，不依赖 7-Zip。
    /// </summary>
    internal static class ArchiveFormatDetector
    {
        /// <summary>签名及其对应的 7-Zip 格式 GUID。</summary>
        private readonly record struct FormatSignature(byte[] Bytes, Guid Format);

        /// <summary>
        /// 前置数据的扫描上限。归档签名可能出现在文件中间任何位置（自解压 stub、被拼接的其它文件、
        /// 下载工具写入的包装头），但为它读整个文件不值得：正常归档在文件头就命中，
        /// 走扫描路径的都是「开头不像归档」的文件，而非归档文件（视频、文档）也走这条路。
        /// 64MB 覆盖了实际会遇到的前置数据，同时把误传非压缩包的代价限制在几十毫秒。
        /// </summary>
        private const int MaxLeadingDataBytes = 64 * 1024 * 1024;

        /// <summary>扫描分块大小：逐块读入并按需保留跨块的签名重叠部分，不整体读入大文件</summary>
        private const int ScanChunkBytes = 1024 * 1024;

        // 格式检测按路径缓存：并行场景下多个 context 会重复检测同一文件
        private static readonly ConcurrentDictionary<string, (Guid Format, long Offset)> FormatDetectionCache = new();

        private static readonly byte[] Signature7z = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        private static readonly byte[] SignatureRar4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static readonly byte[] SignatureRar5 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
        private static readonly byte[] SignatureZipLocal = [0x50, 0x4B, 0x03, 0x04];   // 普通 Zip 本地文件头
        private static readonly byte[] SignatureZipEmpty = [0x50, 0x4B, 0x05, 0x06];   // 空 Zip（仅中央目录结束记录）
        private static readonly byte[] SignatureZipSpanned = [0x50, 0x4B, 0x07, 0x08]; // 分卷 Zip 标记

        // 文件头检测表，顺序即优先级；RAR5 / RAR4 在第 7 字节区分（01 vs 00），互不为前缀
        private static readonly FormatSignature[] HeadSignatures =
        [
            new(Signature7z, SevenZipGuids.CLSID_CFormat7z),
            new(SignatureRar5, SevenZipGuids.CLSID_CFormatRar5),
            new(SignatureRar4, SevenZipGuids.CLSID_CFormatRar),
            new(SignatureZipLocal, SevenZipGuids.CLSID_CFormatZip),
            new(SignatureZipEmpty, SevenZipGuids.CLSID_CFormatZip),
            new(SignatureZipSpanned, SevenZipGuids.CLSID_CFormatZip),
        ];

        /// <summary>扫描用的签名表（与头部表相同：扫描时任何合法签名都可能出现，包括空包的结束记录）</summary>
        private static readonly FormatSignature[] ScanSignatures = HeadSignatures;

        /// <summary>参与扫描的最长签名长度，决定跨块保留的重叠字节数</summary>
        private static readonly int MaxSignatureLength = HeadSignatures.Max(signature => signature.Bytes.Length);

        /// <summary>
        /// 识别压缩包格式及签名偏移。
        /// <para>
        /// 快路径：签名就在文件开头（绝大多数归档、以及 7-Zip 自己产出的分卷），只读 8 字节。
        /// 慢路径：签名不在开头——自解压包（PE stub）、前面被拼接了其它文件的数据、下载工具的包装头等，
        /// 都表现为「一份合法归档整体后移」。此时在 <see cref="MaxLeadingDataBytes"/> 内扫描签名，
        /// 取<b>最早出现</b>的那个（位置优先于表格顺序：最先像归档的地方才是归档起点）。
        /// </para>
        /// <para>
        /// 偏移会原样交给 <c>IInArchive.Open</c> 的 <c>maxCheckStartPosition</c>，7-Zip 据此在
        /// 该范围内容自行定位签名，因此前置数据不会被当成归档的一部分。
        /// </para>
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>格式 GUID 与签名偏移；无法识别时 Format 为 Guid.Empty</returns>
        public static (Guid Format, long Offset) DetectArchiveFormat(string filePath)
        {
            return FormatDetectionCache.GetOrAdd(Path.GetFullPath(filePath), static path =>
            {
                try
                {
                    using var fileStream = File.OpenRead(path);
                    long fileLength = fileStream.Length;
                    if (fileLength < 4)
                    {
                        return (Guid.Empty, 0);
                    }

                    // 最长签名 8 字节（RAR5），头部只需读一次
                    Span<byte> head = stackalloc byte[8];
                    int headLength = (int)Math.Min(fileLength, head.Length);
                    Span<byte> headBytes = head[..headLength];
                    fileStream.ReadExactly(headBytes);

                    // 快路径：签名就在开头，绝大多数文件在此返回，不产生额外 IO
                    var fromHead = DetectFromHead(headBytes);
                    if (fromHead.Format != Guid.Empty)
                    {
                        return fromHead;
                    }

                    // 慢路径：签名可能在任意位置
                    return DetectByScanning(fileStream, fileLength);
                }
                catch (Exception)
                {
                    return (Guid.Empty, 0);
                }
            });
        }

        /// <summary>按文件头签名识别格式（普通压缩包，签名偏移恒为 0）。</summary>
        private static (Guid Format, long Offset) DetectFromHead(ReadOnlySpan<byte> head)
        {
            foreach (var signature in HeadSignatures)
            {
                if (head.StartsWith(signature.Bytes))
                {
                    return (signature.Format, 0);
                }
            }

            return (Guid.Empty, 0);
        }

        /// <summary>
        /// 在文件前 <see cref="MaxLeadingDataBytes"/> 内分块扫描签名，返回最早命中的位置。
        /// 分块之间保留「最长签名 - 1」字节重叠，避免签名被块边界切断。
        /// </summary>
        private static (Guid Format, long Offset) DetectByScanning(FileStream stream, long fileLength)
        {
            long limit = Math.Min(fileLength, MaxLeadingDataBytes);
            byte[] buffer = new byte[ScanChunkBytes + MaxSignatureLength - 1];

            long windowStart = 0;
            int carry = 0;
            stream.Position = 0;

            while (windowStart + carry < limit)
            {
                int capacity = buffer.Length - carry;
                int want = (int)Math.Min(capacity, limit - windowStart - carry);
                if (want <= 0)
                {
                    break;
                }

                int read = stream.Read(buffer, carry, want);
                if (read <= 0)
                {
                    break;
                }

                int length = carry + read;
                ReadOnlySpan<byte> window = buffer.AsSpan(0, length);

                if (FindEarliestSignature(window, out Guid format, out int offset))
                {
                    return (format, windowStart + offset);
                }

                // 保留尾部若干字节与下一块拼接；Span.CopyTo 支持源与目标重叠
                int keep = Math.Min(MaxSignatureLength - 1, length);
                buffer.AsSpan(length - keep, keep).CopyTo(buffer);
                windowStart += length - keep;
                carry = keep;
            }

            return (Guid.Empty, 0);
        }

        /// <summary>
        /// 找出窗口内最早出现的签名（同一位置时按表序取优先级更高的）。
        /// 与「按表序逐个 IndexOf 取第一个命中」不同：后者会让表里靠前的格式压过实际更早出现的归档。
        /// </summary>
        private static bool FindEarliestSignature(ReadOnlySpan<byte> window, out Guid format, out int offset)
        {
            format = Guid.Empty;
            offset = -1;

            foreach (var signature in ScanSignatures)
            {
                int index = window.IndexOf(signature.Bytes);
                if (index < 0)
                {
                    continue;
                }

                if (offset < 0 || index < offset)
                {
                    offset = index;
                    format = signature.Format;
                }
            }

            return offset >= 0;
        }
    }
}
