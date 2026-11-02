using System.Buffers.Binary;
using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 传统 Zip 加密（ZipCrypto）+ Store（不压缩）条目的快速判错器。
    /// <para>
    /// 背景：ZipCrypto 的 12 字节加密头只校验 **1 个字节**（CRC 高字节，或 flag bit 3
    /// 设置时的时间高字节），误判率 1/256。实测 200MB 单文件下 20000 条字典有
    /// 显著比例的错误密码会通过校验并触发完整解压，吞吐被拖到 1100 个/秒
    /// （对比 WinZip AES 的 3754 个/秒，后者有 2 字节校验值）。
    /// </para>
    /// <para>
    /// 加密头之后就是 Store 模式的明文（ZipCrypto 是流密码，逐字节异或），
    /// 因此再解 16 字节并比对文件格式签名即可把误判率压到约 2^-32，
    /// 同时彻底消除那 1/256 的假阳性解压。
    /// </para>
    /// <para>
    /// 适用条件：zip、加密、非 AES（method != 99）、某条目的 method == 0(Store)、
    /// 该条目未使用 data descriptor（本地头里的压缩后大小可靠）、扩展名有签名。
    /// </para>
    /// </summary>
    internal sealed class ZipStoreProbe : IPasswordProbe
    {
        private const uint LocalFileHeaderSignature = 0x04034B50;

        // 通用标志位
        private const ushort FlagEncrypted = 0x0001;
        private const ushort FlagDataDescriptor = 0x0008;

        private const ushort MethodStored = 0;
        private const ushort MethodAes = 99;

        private const int LocalHeaderSize = 30;
        private const int ProbeBlockSize = 16;

        // 12 字节加密头 + 16 字节明文首块
        private const int ProbeBufferSize = ZipCryptoCipher.HeaderLength + ProbeBlockSize;

        private readonly byte[] _cipherProbe = new byte[ProbeBufferSize];
        private readonly byte _checkByte;
        private readonly ContentSignature _signature;

        private ZipStoreProbe(string fileName, byte checkByte, ContentSignature signature)
        {
            FileName = fileName;
            _checkByte = checkByte;
            _signature = signature;
        }

        /// <summary>归档内的文件名（zip 的文件名默认不加密）</summary>
        public string FileName { get; }

        public string ContentFormat => _signature.Format;

        /// <summary>
        /// 扫描本地文件头，找第一个可用于判错的条目。不适用时返回 null（退回完整测密）。
        /// </summary>
        public static ZipStoreProbe? TryCreate(string archivePath, ArchiveInfo info)
        {
            if (info.Type != SevenZipGuids.CLSID_CFormatZip || !info.IsEncrypted || info.IsHeaderEncryption)
            {
                return null;
            }

            var (_, signatureOffset) = ArchiveFormatDetector.DetectArchiveFormat(archivePath);

            try
            {
                using var stream = new FileStream(
                    archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);

                long position = signatureOffset;
                byte[] header = new byte[LocalHeaderSize];

                // 顺序遍历本地文件头。数据区按压缩后大小跳过——这要求 bit 3 未设置，
                // 否则本地头里的大小为 0，无法可靠跳转，直接放弃。
                while (position + LocalHeaderSize <= stream.Length)
                {
                    stream.Position = position;
                    if (!ReadExactly(stream, header))
                    {
                        return null;
                    }

                    if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalFileHeaderSignature)
                    {
                        return null; // 到中央目录/SFX 尾部，未找到可用条目
                    }

                    ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                    ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
                    uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
                    uint packedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18));
                    ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
                    ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));

                    bool hasDescriptor = (flags & FlagDataDescriptor) != 0;
                    if (hasDescriptor)
                    {
                        // 大小字段不可信，无法跳过数据继续扫描；第一个条目也不可靠（可能是 0）
                        return null;
                    }

                    long dataOffset = position + LocalHeaderSize + nameLength + extraLength;
                    if (dataOffset > stream.Length)
                    {
                        return null;
                    }

                    bool encrypted = (flags & FlagEncrypted) != 0;
                    if (encrypted && method == MethodStored)
                    {
                        long nameOffset = position + LocalHeaderSize;
                        var probe = TryBuild(stream, nameOffset, dataOffset, packedSize, nameLength, crc32);
                        if (probe is not null)
                        {
                            return probe;
                        }
                    }

                    // ← 下一张本地头
                    position = dataOffset + packedSize;
                }

                return null;
            }
            catch (Exception)
            {
                // 读文件失败/结构异常：只是放弃优化，不影响正常测密
                return null;
            }
        }

        private static ZipStoreProbe? TryBuild(
            FileStream stream,
            long nameOffset,
            long dataOffset,
            uint packedSize,
            ushort nameLength,
            uint crc32)
        {
            // 加密头 12 字节 + 至少 1 字节数据才算有意义
            if (packedSize <= ZipCryptoCipher.HeaderLength)
            {
                return null;
            }

            // 校验值：bit 3（data descriptor）在上面已被排除，因此本地头里的 CRC 可靠，
            // 校验值就是 CRC 的高字节
            byte checkByte = (byte)(crc32 >> 24);

            // 文件名（zip 的文件名在本地头里是明文；UTF-8 标志只影响编码，扩展名总是 ASCII）
            byte[] nameBytes = new byte[nameLength];
            stream.Position = nameOffset;
            if (!ReadExactly(stream, nameBytes))
            {
                return null;
            }

            string fileName = Encoding.Latin1.GetString(nameBytes);
            ContentSignature? signature = ContentSignature.ForFileName(fileName);
            if (signature is null)
            {
                return null;
            }

            var probe = new ZipStoreProbe(fileName, checkByte, signature);
            stream.Position = dataOffset;
            if (!ReadExactly(stream, probe._cipherProbe))
            {
                return null;
            }

            return probe;
        }

        /// <summary>
        /// 快速判定：初始化 ZipCrypto 密钥流 → 解 12 字节加密头 → 校验字节 → 解首块比对签名。
        /// 不修改共享状态，可多线程并发调用。
        /// </summary>
        public PasswordProbeResult Probe(string password)
        {
            // 传统 zip 加密的密码字节按 OEM/ANSI 解释，非 ASCII 密码的编码取决于
            // 7-Zip 内部转换（UTF-8 / 系统代码页），这里不做猜测——交回完整测密，
            // 绝不因为编码假设错误而漏掉正确答案
            foreach (char c in password)
            {
                if (c > 0x7F)
                {
                    return PasswordProbeResult.Unsupported;
                }
            }

            var cipher = new ZipCryptoCipher();
            cipher.Reset(Encoding.ASCII.GetBytes(password));

            Span<byte> buffer = stackalloc byte[ProbeBufferSize];
            _cipherProbe.CopyTo(buffer);
            cipher.Decrypt(buffer);

            // buffer[11] = 加密头最后一字节 = 校验值
            if (buffer[ZipCryptoCipher.HeaderLength - 1] != _checkByte)
            {
                return PasswordProbeResult.NoMatch;
            }

            return _signature.Matches(buffer[ZipCryptoCipher.HeaderLength..])
                ? PasswordProbeResult.Match
                : PasswordProbeResult.NoMatch;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }
    }
}
