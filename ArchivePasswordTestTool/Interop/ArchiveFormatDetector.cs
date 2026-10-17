using System.Collections.Concurrent;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 压缩包格式检测：按文件头签名（或 PE 封装 SFX 的 stub 后签名扫描）识别格式与数据偏移。
    /// 消费方：SevenZipNative（ArchiveContext 构造）、Runner（哈希前格式确认）。
    /// </summary>
    internal static class ArchiveFormatDetector
    {
        // MZ（PE）封装 SFX 的 stub 扫描窗口上限，覆盖常见 7z/WinRAR SFX 模块大小
        private const int SfxScanWindowBytes = 8 * 1024 * 1024;

        // 格式检测按路径缓存：并行场景下多个 context 会重复检测同一文件
        private static readonly ConcurrentDictionary<string, (Guid Format, long Offset)> FormatDetectionCache = new();

        private static readonly byte[] Signature7z = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        private static readonly byte[] SignatureZip = [0x50, 0x4B, 0x03, 0x04];
        private static readonly byte[] SignatureRar4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static readonly byte[] SignatureRar5 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

        /// <summary>
        /// 识别压缩包格式及签名偏移。
        /// 普通压缩包：签名位于文件头（Offset=0）。
        /// SFX 自解压包：exe stub（MZ 头）+ 追加的压缩数据，签名位于 stub 之后，
        /// 需在扫描窗口内定位签名偏移，供 IInArchive.Open 的 maxCheckStartPosition 使用。
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

                    byte[] head = new byte[(int)Math.Min(fileLength, 8)];
                    fileStream.ReadExactly(head, 0, head.Length);

                    if (head[0] != 0x4D || head[1] != 0x5A) // 非 PE(MZ)：普通压缩包
                    {
                        return DetectFromHead(head);
                    }

                    // PE(MZ) 封装：读入 stub 扫描窗口，按优先级定位真实压缩数据签名
                    int window = (int)Math.Min(fileLength, SfxScanWindowBytes);
                    byte[] buffer = new byte[window];
                    fileStream.Seek(0, SeekOrigin.Begin);
                    fileStream.ReadExactly(buffer, 0, window);
                    ReadOnlySpan<byte> span = buffer;

                    int idx = IndexOfPattern(span, Signature7z);
                    if (idx >= 0)
                    {
                        return (SevenZipGuids.CLSID_CFormat7z, idx);
                    }

                    idx = IndexOfPattern(span, SignatureRar5);
                    if (idx >= 0)
                    {
                        return (SevenZipGuids.CLSID_CFormatRar5, idx);
                    }

                    idx = IndexOfPattern(span, SignatureRar4);
                    if (idx >= 0)
                    {
                        return (SevenZipGuids.CLSID_CFormatRar, idx);
                    }

                    idx = IndexOfPattern(span, SignatureZip);
                    if (idx >= 0)
                    {
                        return (SevenZipGuids.CLSID_CFormatZip, idx);
                    }

                    return (Guid.Empty, 0);
                }
                catch (Exception)
                {
                    return (Guid.Empty, 0);
                }
            });
        }

        /// <summary>
        /// 按文件头字节检测格式（兼容旧调用方式）
        /// </summary>
        private static (Guid Format, long Offset) DetectFromHead(byte[] header)
        {
            int available = header.Length;

            // 检查7z格式签名：37 7A BC AF 27 1C
            if (available >= 6 &&
                header[0] == 0x37 && header[1] == 0x7A &&
                header[2] == 0xBC && header[3] == 0xAF &&
                header[4] == 0x27 && header[5] == 0x1C)
            {
                return (SevenZipGuids.CLSID_CFormat7z, 0);
            }

            // 检查Zip格式签名：50 4B 03 04 或 50 4B 05 06 或 50 4B 07 08
            if (available >= 4 &&
                header[0] == 0x50 && header[1] == 0x4B)
            {
                if (header[2] == 0x03 && header[3] == 0x04) // 标准Zip
                    return (SevenZipGuids.CLSID_CFormatZip, 0);
                if (header[2] == 0x05 && header[3] == 0x06) // 空Zip
                    return (SevenZipGuids.CLSID_CFormatZip, 0);
                if (header[2] == 0x07 && header[3] == 0x08) // 分卷Zip
                    return (SevenZipGuids.CLSID_CFormatZip, 0);
            }

            // 检查Rar4格式签名：52 61 72 21 1A 07 00
            if (available >= 7 &&
                header[0] == 0x52 && header[1] == 0x61 &&
                header[2] == 0x72 && header[3] == 0x21 &&
                header[4] == 0x1A && header[5] == 0x07 &&
                header[6] == 0x00)
            {
                return (SevenZipGuids.CLSID_CFormatRar, 0);
            }

            // 检查Rar5格式签名：52 61 72 21 1A 07 01 00
            if (available >= 8 &&
                header[0] == 0x52 && header[1] == 0x61 &&
                header[2] == 0x72 && header[3] == 0x21 &&
                header[4] == 0x1A && header[5] == 0x07 &&
                header[6] == 0x01 && header[7] == 0x00)
            {
                return (SevenZipGuids.CLSID_CFormatRar5, 0);
            }

            return (Guid.Empty, 0);
        }

        /// <summary>
        /// 在缓冲区内查找字节模式，返回首次出现的绝对偏移（未找到返回 -1）
        /// </summary>
        private static int IndexOfPattern(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        {
            if (haystack.Length < needle.Length)
            {
                return -1;
            }

            int searchFrom = 0;
            while (searchFrom <= haystack.Length - needle.Length)
            {
                int relative = haystack[searchFrom..].IndexOf(needle[0]);
                if (relative < 0)
                {
                    return -1;
                }

                int absolute = searchFrom + relative;
                if (haystack.Slice(absolute, needle.Length).SequenceEqual(needle))
                {
                    return absolute;
                }

                searchFrom = absolute + 1;
            }

            return -1;
        }
    }
}
