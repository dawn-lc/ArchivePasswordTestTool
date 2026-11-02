using System.Security.Cryptography;
using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 7z「存储模式 + 单文件 + 仅数据加密」压缩包的快速判错器。
    /// <para>
    /// 背景：7z 的加密头里只有 <c>NumCyclesPower / Salt / IV</c>，<b>没有密码校验值</b>。
    /// 当内部文件用 Copy（不压缩）编码时，密文没有任何可自校验的结构——错误密钥解出的
    /// 垃圾数据与正常数据无法区分，7-Zip 只能解到文件尾靠文件 CRC 判错。实测 100MB 单文件
    /// 每个候选解码约 100MB，且<b>加线程与加进程都无效</b>（瓶颈是内存带宽，不是 dll 内的
    /// KDF 全局锁）：64 个/秒，外推 1.2GB + 250 万字典约 116 小时。
    /// </para>
    /// <para>
    /// 这里换一条路：自己派生 7zAES 密钥（<see cref="SevenZipAesKeyDerivation"/>），只解密
    /// 首块比对文件格式签名。错误密码解出的首块是随机字节，命中魔数的概率约 2^-32；
    /// 命中的候选再交给 7-Zip 完整解包确认，因此<b>绝不会漏掉正确答案</b>。
    /// KDF 是纯 CPU 且 SMT 有效，可占满全部逻辑核——这正是完整解密做不到的。
    /// </para>
    /// <para>
    /// 适用条件严格限定，任一不满足即返回 null（退回完整测密）：
    /// 7z 格式、加密但未加密文件名、单文件、头为明文、编码链恰为 [AES, Copy]、
    /// 单打包流、打包大小等于明文补齐到 16 的倍数、且扩展名在签名表内。
    /// </para>
    /// </summary>
    internal sealed class SevenZipStoreProbe : IPasswordProbe
    {
        private const int BlockSize = SevenZipAesKeyDerivation.BlockSize;

        /// <summary>密码超过这个字符数时长度开销已可忽略，直接拒绝以免 KDF 记录过大</summary>
        private const int MaxPasswordChars = 1024;

        [ThreadStatic]
        private static Aes? _aes;

        [ThreadStatic]
        private static byte[]? _keyBuffer;

        [ThreadStatic]
        private static byte[]? _plainBuffer;

        [ThreadStatic]
        private static byte[]? _passwordBuffer;

        private readonly int _numCyclesPower;
        private readonly byte[] _salt;
        private readonly byte[] _iv;
        private readonly byte[] _cipherFirstBlock;
        private readonly ContentSignature _signature;

        private SevenZipStoreProbe(
            string entryName,
            int numCyclesPower,
            byte[] salt,
            byte[] iv,
            byte[] cipherFirstBlock,
            ContentSignature signature)
        {
            EntryName = entryName;
            _numCyclesPower = numCyclesPower;
            _salt = salt;
            _iv = iv;
            _cipherFirstBlock = cipherFirstBlock;
            _signature = signature;
        }

        /// <summary>归档内的文件名（7z 未加密文件名时是明文）</summary>
        public string EntryName { get; }

        /// <summary>预期的明文格式名（如 "MP4"）</summary>
        public string ContentFormat => _signature.Format;

        /// <summary>
        /// 尝试为当前归档建立快速判错器。不适用时返回 null，调用方应退回完整测密。
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="info">已探测的归档信息（必须来自真实打开成功的归档）</param>
        public static SevenZipStoreProbe? TryCreate(string archivePath, ArchiveInfo info)
        {
            // 只处理 7z：RAR5 有 PswCheck、ZipCrypto 有独立探测器，都不需要本机制
            if (info.Type != SevenZipGuids.CLSID_CFormat7z)
            {
                return null;
            }

            // header 加密在 Open 阶段就验证了密码，快速判错没有意义
            if (!info.IsEncrypted || info.IsHeaderEncryption)
            {
                return null;
            }

            // 多文件时首个文件的格式不代表后续文件；部分加密（存在未加密条目）也不适用
            if (info.FileCount != 1 || info.SupportsQuickTest)
            {
                return null;
            }

            if (!SevenZipAesKeyDerivation.IsAvailable)
            {
                return null;
            }

            var (_, signatureOffset) = ArchiveFormatDetector.DetectArchiveFormat(archivePath);

            SevenZipStoreLayout? layout = SevenZipHeaderReader.TryRead(archivePath, signatureOffset);
            if (layout is null)
            {
                return null;
            }

            if (!SevenZipAesKeyDerivation.TryParseProperties(
                    layout.AesProperties, out int numCyclesPower, out byte[] salt, out byte[] iv))
            {
                return null;
            }

            ContentSignature? signature = ContentSignature.ForFileName(layout.EntryName);
            if (signature is null)
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(
                    archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);

                byte[] cipherFirstBlock = new byte[BlockSize];
                stream.Position = layout.PackedOffset;
                if (stream.ReadAtLeast(cipherFirstBlock, BlockSize, throwOnEndOfStream: false) != BlockSize)
                {
                    return null;
                }

                return new SevenZipStoreProbe(
                    layout.EntryName, numCyclesPower, salt, iv, cipherFirstBlock, signature);
            }
            catch (Exception)
            {
                // 读文件失败：只是放弃优化，不影响正常测密
                return null;
            }
        }

        /// <summary>
        /// 快速判定：派生 7zAES 密钥 → 解密首块 → 比对文件格式签名。
        /// 不修改任何共享状态，可被多线程并发调用。
        /// </summary>
        public PasswordProbeResult Probe(string password)
        {
            // 7z 的密码在 7-Zip 内部就是宽字符串，UTF-16LE 编码没有歧义；
            // 但超长密码会让每条 KDF 记录变大，超出实用范围就退回完整测密
            if (password.Length > MaxPasswordChars)
            {
                return PasswordProbeResult.Unsupported;
            }

            byte[] passwordBuffer = _passwordBuffer ??= new byte[MaxPasswordChars * 2];
            int passwordByteCount = Encoding.Unicode.GetBytes(password, passwordBuffer);

            byte[] key = _keyBuffer ??= new byte[SevenZipAesKeyDerivation.KeySize];
            byte[] plain = _plainBuffer ??= new byte[BlockSize];

            SevenZipAesKeyDerivation.DeriveKey(
                _salt, passwordBuffer.AsSpan(0, passwordByteCount), _numCyclesPower, key);

            Aes aes = _aes ??= CreateAes();
            aes.Key = key;
            _ = aes.DecryptCbc(_cipherFirstBlock, _iv, plain, PaddingMode.None);

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
    }
}
