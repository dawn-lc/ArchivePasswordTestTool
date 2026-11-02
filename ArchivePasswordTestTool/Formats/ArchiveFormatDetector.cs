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

        // MZ（PE）封装 SFX 的 stub 扫描窗口上限，覆盖常见 7z/WinRAR SFX 模块大小
        private const int SfxScanWindowBytes = 8 * 1024 * 1024;

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

        // SFX 扫描表：追加在 exe 尾部的是压缩数据本体，只可能是本地文件头，空包/分卷标记不会出现
        private static readonly FormatSignature[] SfxSignatures =
        [
            new(Signature7z, SevenZipGuids.CLSID_CFormat7z),
            new(SignatureRar5, SevenZipGuids.CLSID_CFormatRar5),
            new(SignatureRar4, SevenZipGuids.CLSID_CFormatRar),
            new(SignatureZipLocal, SevenZipGuids.CLSID_CFormatZip),
        ];

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

                    // 最长签名 8 字节（RAR5），头部只需读一次，SFX 分支继续复用
                    Span<byte> head = stackalloc byte[8];
                    int headLength = (int)Math.Min(fileLength, head.Length);
                    Span<byte> headBytes = head[..headLength];
                    fileStream.ReadExactly(headBytes);

                    // 非 PE(MZ)：普通压缩包，签名只可能在文件头
                    if (head[0] != 0x4D || head[1] != 0x5A)
                    {
                        return DetectFromHead(headBytes);
                    }

                    // PE(MZ) 封装：读入 stub 扫描窗口，按优先级定位追加的压缩数据签名
                    int window = (int)Math.Min(fileLength, SfxScanWindowBytes);
                    byte[] buffer = new byte[window];
                    headBytes.CopyTo(buffer);
                    fileStream.ReadExactly(buffer.AsSpan(headLength));

                    return DetectAfterStub(buffer);
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

        /// <summary>在 PE stub 之后的扫描窗口内按优先级定位首个命中的签名，返回其绝对偏移。</summary>
        private static (Guid Format, long Offset) DetectAfterStub(ReadOnlySpan<byte> window)
        {
            foreach (var signature in SfxSignatures)
            {
                int offset = window.IndexOf(signature.Bytes);
                if (offset >= 0)
                {
                    return (signature.Format, offset);
                }
            }

            return (Guid.Empty, 0);
        }
    }
}
