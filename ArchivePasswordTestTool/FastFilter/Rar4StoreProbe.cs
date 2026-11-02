using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// RAR4（WinRAR -ma4）「存储模式 + 单文件 + 仅数据加密」压缩包的快速判错器。
    /// <para>
    /// 背景：RAR4 没有密码校验字段（RAR5 才有 PswCheck），7-Zip 遇到错误密码时必须
    /// 把整个文件解完、靠文件尾 CRC 才能判错。本例 1.2GB 单文件 → 每个候选密码
    /// 要解 1.2GB，250 万字典约 100 小时。
    /// </para>
    /// <para>
    /// 存储模式（method 0x30）下密文就是明文（CBC 补零），因此只要派生 RAR4 密钥、
    /// 解密首块并校验文件格式签名就能排除错误密码：错误密码解出的首块是随机字节，
    /// 命中 MP4 等容器魔数的概率约 2^-32。命中者再交给 7-Zip 完整确认。
    /// </para>
    /// <para>
    /// 适用条件严格限定，任一不满足即返回 null（退回完整测密）：
    /// RAR4、仅 1 个文件、加密但未加密文件名、存储模式、UNP_VER ≥ 29、
    /// 未分卷、未使用 LHD_LARGE、packSize 恰好等于 unpSize 补零到 16 的倍数、
    /// 且文件扩展名对应已知的容器签名。
    /// </para>
    /// </summary>
    internal sealed class Rar4StoreProbe : IPasswordProbe
    {
        // RAR4 头部类型（单字节字段，不能按 2 字节读取——紧随其后就是 HEAD_FLAGS）
        private const byte MainHeadType = 0x73;
        private const byte FileHeadType = 0x74;

        // MHD_*
        private const ushort MhdMultVolume = 0x0001;
        private const ushort MhdPassword = 0x0080;

        // LHD_*
        private const ushort LhdSplitBefore = 0x0001;
        private const ushort LhdSplitAfter = 0x0002;
        private const ushort LhdPassword = 0x0004;
        private const ushort LhdLarge = 0x0100;
        private const ushort LhdSalt = 0x0400;

        private const int SignatureSize = 7;
        private const int MainHeaderBaseSize = 13;
        private const int FileHeaderBaseSize = 32;
        private const int BlockSize = 16;
        private const byte MethodStored = 0x30;

        /// <summary>RAR4 头部最大读取窗口：文件名过长时放弃快速判错</summary>
        private const int HeaderReadWindow = 64 * 1024;

        [ThreadStatic]
        private static Aes? _aes;

        [ThreadStatic]
        private static byte[]? _keyBuffer;

        [ThreadStatic]
        private static byte[]? _ivBuffer;

        [ThreadStatic]
        private static byte[]? _plainBuffer;

        [ThreadStatic]
        private static byte[]? _passwordBuffer;

        private readonly byte[] _salt;
        private readonly byte[] _cipherFirstBlock;
        private readonly ContentSignature _signature;

        private Rar4StoreProbe(string fileName, byte[] salt, byte[] cipherFirstBlock, ContentSignature signature)
        {
            FileName = fileName;
            _salt = salt;
            _cipherFirstBlock = cipherFirstBlock;
            _signature = signature;
        }

        /// <summary>归档内的文件名（未加密的文件名头，可用于展示）</summary>
        public string FileName { get; }

        /// <summary>预期的明文格式名（如 "MP4"）</summary>
        public string ContentFormat => _signature.Format;

        /// <summary>
        /// 尝试为当前归档建立快速判错器。不适用时返回 null，调用方应退回完整测密。
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="info">已探测的归档信息（必须来自真实打开成功的归档）</param>
        public static Rar4StoreProbe? TryCreate(string archivePath, ArchiveInfo info)
        {
            // 只处理 RAR4：RAR5 的 PswCheck 由 7-Zip 自己快速判错，不需要本机制
            if (info.Type != SevenZipGuids.CLSID_CFormatRar)
            {
                return null;
            }

            if (!info.IsEncrypted || info.IsHeaderEncryption)
            {
                return null;
            }

            // 多文件时首个文件的格式不代表后续文件，保守关闭
            if (info.FileCount != 1 || info.SupportsQuickTest)
            {
                return null;
            }

            var (_, signatureOffset) = ArchiveFormatDetector.DetectArchiveFormat(archivePath);

            try
            {
                using var stream = File.OpenRead(archivePath);
                long fileLength = stream.Length;
                int window = (int)Math.Min(fileLength - signatureOffset, HeaderReadWindow);
                if (window < SignatureSize + MainHeaderBaseSize)
                {
                    return null;
                }

                byte[] head = new byte[window];
                _ = stream.Seek(signatureOffset, SeekOrigin.Begin);
                stream.ReadExactly(head);

                int p = SignatureSize;

                // ---- 主头 ----
                if (head[p + 2] != MainHeadType)
                {
                    return null;
                }

                ushort mainFlags = ReadUInt16(head, p + 3);
                ushort mainSize = ReadUInt16(head, p + 5);
                if (mainSize < MainHeaderBaseSize)
                {
                    return null;
                }

                // 分卷（后续数据在其他文件）与 header 加密都不适用
                if ((mainFlags & (MhdMultVolume | MhdPassword)) != 0)
                {
                    return null;
                }

                p += mainSize;

                // ---- 文件头 ----
                if (p + FileHeaderBaseSize > head.Length || head[p + 2] != FileHeadType)
                {
                    return null;
                }

                ushort fileFlags = ReadUInt16(head, p + 3);
                ushort fileSize = ReadUInt16(head, p + 5);
                if (fileSize < FileHeaderBaseSize || p + fileSize > head.Length)
                {
                    return null;
                }

                if ((fileFlags & (LhdSplitBefore | LhdSplitAfter | LhdLarge)) != 0
                    || (fileFlags & LhdPassword) == 0
                    || (fileFlags & LhdSalt) == 0)
                {
                    return null;
                }

                uint packSize = ReadUInt32(head, p + 7);
                uint unpackSize = ReadUInt32(head, p + 11);
                byte unpackVersion = head[p + 24];
                byte method = head[p + 25];
                ushort nameSize = ReadUInt16(head, p + 26);

                if (method != MethodStored || unpackVersion < 29 || unpackSize == 0)
                {
                    return null;
                }

                // 存储模式 + CBC 补零：密文长度必须正好是明文补到 16 的倍数
                ulong expectedPackedSize = ((ulong)unpackSize + BlockSize - 1) / BlockSize * BlockSize;
                if (packSize != expectedPackedSize)
                {
                    return null;
                }

                int nameOffset = p + FileHeaderBaseSize;
                if (nameOffset + nameSize > head.Length)
                {
                    return null;
                }

                // 文件名可能是 LHD_UNICODE 编码，但扩展名部分总是 ASCII，按 Latin1 读取即可
                string fileName = Encoding.Latin1.GetString(head, nameOffset, nameSize);
                ContentSignature? signature = ContentSignature.ForFileName(fileName);
                if (signature is null)
                {
                    return null;
                }

                // 盐紧跟在文件名之后，必须落在头部范围内
                int saltOffset = nameOffset + nameSize;
                if (saltOffset + Rar4KeyDerivation.SaltSize > p + fileSize)
                {
                    return null;
                }

                long dataOffset = p + fileSize;
                if (dataOffset + BlockSize > fileLength)
                {
                    return null;
                }

                byte[] salt = head.AsSpan(saltOffset, Rar4KeyDerivation.SaltSize).ToArray();
                byte[] cipherFirstBlock = new byte[BlockSize];
                _ = stream.Seek(dataOffset, SeekOrigin.Begin);
                stream.ReadExactly(cipherFirstBlock);

                return new Rar4StoreProbe(fileName, salt, cipherFirstBlock, signature);
            }
            catch (Exception)
            {
                // 读文件失败/越界：只是放弃优化，不影响正常测密
                return null;
            }
        }

        /// <summary>
        /// 快速判定：派生 RAR4 密钥 → 解密首块 → 比对文件格式签名。
        /// 不修改任何共享状态，可被多线程并发调用。
        /// </summary>
        public PasswordProbeResult Probe(string password)
        {
            // 密码超过 28 个字符时记录超过 64 字节，会触发 7-Zip 的 RAR 3.5 兼容分支，本实现不支持
            if (password.Length > Rar4KeyDerivation.MaxPasswordChars)
            {
                return PasswordProbeResult.Unsupported;
            }

            // 密码字节是 UTF-16LE（UNP_VER ≥ 29，7-Zip 逐 wchar 取低/高字节）。
            // 缓冲按线程复用：这是每候选都要执行的热路径，避免逐次分配
            byte[] passwordBuffer = _passwordBuffer ??= new byte[Rar4KeyDerivation.MaxPasswordChars * 2];
            int passwordByteCount = Encoding.Unicode.GetBytes(password, passwordBuffer);

            byte[] key = _keyBuffer ??= new byte[BlockSize];
            byte[] iv = _ivBuffer ??= new byte[BlockSize];
            byte[] plain = _plainBuffer ??= new byte[BlockSize];

            Rar4KeyDerivation.DeriveKey(passwordBuffer.AsSpan(0, passwordByteCount), _salt, key, iv);

            Aes aes = _aes ??= CreateAes();
            aes.Key = key;
            _ = aes.DecryptCbc(_cipherFirstBlock, iv, plain, PaddingMode.None);

            // 扩展名推断的签名优先，但不以其为限：归档内文件名可能被改名或伪装
            return _signature.MatchesFileOrAnyKnownContainer(plain)
                ? PasswordProbeResult.Match
                : PasswordProbeResult.NoMatch;
        }

        private static Aes CreateAes()
        {
            Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            return aes;
        }

        private static ushort ReadUInt16(byte[] buffer, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));

        private static uint ReadUInt32(byte[] buffer, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
    }
}
