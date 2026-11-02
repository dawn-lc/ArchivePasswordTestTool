using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 7zAES 的密钥派生，等价 7-Zip <c>CPP/7zip/Crypto/7zAes.cpp</c> 的 <c>CKeyInfo::CalcKey</c>：
    /// <list type="number">
    /// <item>把「盐 + 密码 + 8 字节小端计数器」作为一条记录，重复 <c>2^NumCyclesPower</c> 次喂给同一路 SHA-256</item>
    /// <item>计数器只写低 4 字节（源码里是 <c>SetUi32</c>，缓冲区末尾 8 字节事先清零），高 4 字节恒为 0</item>
    /// <item>最终摘要 32 字节直接作为 AES-256 密钥</item>
    /// </list>
    /// <para>
    /// 密码字节是 <b>UTF-16LE</b>（7-Zip 内部拿到的就是宽字符串）——与 RAR4 相同，与 ZipCrypto 不同。
    /// IV 不参与派生：它以明文存放在 coder properties 里（<c>[b0][b1][salt][iv]</c>），
    /// 由 <see cref="TryParseProperties"/> 读出后直接使用。
    /// </para>
    /// <para>
    /// 用 Windows CNG（bcrypt）而非托管 SHA256：CNG 的 SHA-256 走 SHA-NI 硬件指令
    /// （本机实测约 2 GB/s，比 SHA-1 的 0.8 GB/s 还快），且与 <see cref="Rar4KeyDerivation"/>
    /// 共用同一套「分批喂数据」的写法，避免为每轮做一次 P/Invoke。
    /// </para>
    /// </summary>
    internal static unsafe class SevenZipAesKeyDerivation
    {
        /// <summary>派生出的密钥长度（SHA-256 摘要 → AES-256）</summary>
        public const int KeySize = 32;

        /// <summary>AES 块长度（IV 长度同此值）</summary>
        public const int BlockSize = 16;

        /// <summary>
        /// 不做迭代派生的特殊标记（7-Zip 的 <c>NumCyclesPower == 0x3F</c>）：
        /// 密钥 = 盐 + 密码字节，其余补零。
        /// </summary>
        public const int NoCyclesMarker = 0x3F;

        /// <summary>7-Zip 支持的迭代次方上限（<c>k_NumCyclesPower_Supported_MAX</c>），超出即 E_NOTIMPL</summary>
        public const int MaxNumCyclesPower = 24;

        /// <summary>每次 BCryptHashData 处理的记录数（减少 P/Invoke 次数）</summary>
        private const int RecordsPerBatch = 1024;

        private const string AlgorithmId = "SHA256";

        private static readonly Lock ProviderLock = new();
        private static IntPtr _provider;
        private static bool _providerUnavailable;

        [ThreadStatic]
        private static byte[]? _batchBuffer;

        /// <summary>
        /// CNG 的 SHA-256 提供程序是否可用。不可用时调用方应整体关闭快速判错，
        /// 而不是逐条退化（逐条退化会静默漏掉正确答案）。
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureProvider();
                return _provider != IntPtr.Zero;
            }
        }

        /// <summary>
        /// 解析 7zAES 的 coder properties：<c>[b0][b1][salt...][iv...]</c>。
        /// <list type="bullet">
        /// <item><c>b0</c> 低 6 位 = NumCyclesPower；bit7 = 有盐；bit6 = 有 IV</item>
        /// <item><c>b1</c> 高 4 位 = 盐长度 - 1；低 4 位 = IV 长度 - 1</item>
        /// </list>
        /// IV 短于 16 字节时按 7-Zip 的做法补零（它的 <c>_iv</c> 缓冲区本就清零）。
        /// </summary>
        /// <returns>属性合法且参数在支持范围内时为 <see langword="true" /></returns>
        public static bool TryParseProperties(
            ReadOnlySpan<byte> properties,
            out int numCyclesPower,
            out byte[] salt,
            out byte[] iv)
        {
            numCyclesPower = 0;
            salt = [];
            iv = new byte[BlockSize];

            if (properties.Length == 0)
            {
                // size == 0：无属性。7-Zip 接受，但 AES 没盐没 IV 的情形不会出现，保守拒绝
                return false;
            }

            byte b0 = properties[0];
            numCyclesPower = b0 & 0x3F;

            if (properties.Length == 1)
            {
                // 只有 b0 且高位全零：等价于「无盐无 IV」
                return (b0 & 0xC0) == 0 && IsSupportedCycles(numCyclesPower);
            }

            byte b1 = properties[1];
            int saltSize = ((b0 >> 7) & 1) + (b1 >> 4);
            int ivSize = ((b0 >> 6) & 1) + (b1 & 0x0F);

            // 7-Zip 要求属性长度精确等于 2 + saltSize + ivSize
            if (properties.Length != 2 + saltSize + ivSize)
            {
                return false;
            }

            salt = properties.Slice(2, saltSize).ToArray();

            ReadOnlySpan<byte> providedIv = properties.Slice(2 + saltSize, ivSize);
            providedIv[..Math.Min(providedIv.Length, BlockSize)].CopyTo(iv);

            return IsSupportedCycles(numCyclesPower);
        }

        private static bool IsSupportedCycles(int numCyclesPower) =>
            numCyclesPower == NoCyclesMarker || numCyclesPower <= MaxNumCyclesPower;

        /// <summary>
        /// 派生 AES-256 密钥。
        /// </summary>
        /// <param name="salt">盐（长度可为 0）</param>
        /// <param name="passwordBytes">密码字节（UTF-16LE）</param>
        /// <param name="numCyclesPower">
        /// 迭代次方；<see cref="NoCyclesMarker"/> 表示不迭代。必须已通过
        /// <see cref="TryParseProperties"/> 的取值范围校验。
        /// </param>
        /// <param name="key">输出：<see cref="KeySize"/> 字节密钥</param>
        public static void DeriveKey(
            ReadOnlySpan<byte> salt,
            ReadOnlySpan<byte> passwordBytes,
            int numCyclesPower,
            Span<byte> key)
        {
            if (key.Length < KeySize)
            {
                throw new ArgumentException($"密钥缓冲区至少需要 {KeySize} 字节", nameof(key));
            }

            Span<byte> target = key[..KeySize];

            if (numCyclesPower == NoCyclesMarker)
            {
                // 7-Zip：Key = 盐 + 密码（截断到 32 字节），其余补零
                target.Clear();
                int written = 0;
                foreach (byte b in salt)
                {
                    if (written >= KeySize)
                    {
                        break;
                    }

                    target[written++] = b;
                }

                foreach (byte b in passwordBytes)
                {
                    if (written >= KeySize)
                    {
                        break;
                    }

                    target[written++] = b;
                }

                return;
            }

            if (numCyclesPower is < 0 or > MaxNumCyclesPower)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numCyclesPower), numCyclesPower, $"NumCyclesPower 必须在 0 到 {MaxNumCyclesPower} 之间。");
            }

            EnsureProvider();
            if (_provider == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows CNG SHA256 提供程序不可用。");
            }

            int rounds = 1 << numCyclesPower;
            int step = salt.Length + passwordBytes.Length + 8;
            byte[] batch = RentBatch(Math.Max(RecordsPerBatch * step, step));
            byte[] digest = new byte[KeySize];

            int hr = BCryptCreateHash(_provider, out IntPtr hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0);
            if (hr != 0)
            {
                throw new InvalidOperationException($"BCryptCreateHash 失败（0x{hr:X8}）。");
            }

            int used = 0;
            try
            {
                for (int i = 0; i < rounds; i++)
                {
                    if (used + step > batch.Length)
                    {
                        HashData(hash, batch, used);
                        used = 0;
                    }

                    salt.CopyTo(batch.AsSpan(used));
                    passwordBytes.CopyTo(batch.AsSpan(used + salt.Length));

                    // 计数器：低 4 字节写轮次，高 4 字节保持 0（缓冲每轮都被完整覆盖）
                    int counter = used + salt.Length + passwordBytes.Length;
                    batch[counter] = (byte)i;
                    batch[counter + 1] = (byte)(i >> 8);
                    batch[counter + 2] = (byte)(i >> 16);
                    batch[counter + 3] = (byte)(i >> 24);
                    batch[counter + 4] = 0;
                    batch[counter + 5] = 0;
                    batch[counter + 6] = 0;
                    batch[counter + 7] = 0;

                    used += step;
                }

                if (used > 0)
                {
                    HashData(hash, batch, used);
                }

                FinishHash(hash, digest);
            }
            finally
            {
                _ = BCryptDestroyHash(hash);
            }

            digest.CopyTo(target);
        }

        private static void EnsureProvider()
        {
            if (_provider != IntPtr.Zero || _providerUnavailable)
            {
                return;
            }

            lock (ProviderLock)
            {
                if (_provider != IntPtr.Zero || _providerUnavailable)
                {
                    return;
                }

                int hr = BCryptOpenAlgorithmProvider(out IntPtr provider, AlgorithmId, null, 0);
                if (hr != 0 || provider == IntPtr.Zero)
                {
                    _providerUnavailable = true;
                    return;
                }

                _provider = provider;
            }
        }

        private static byte[] RentBatch(int size)
        {
            byte[]? buffer = _batchBuffer;
            if (buffer is null || buffer.Length < size)
            {
                buffer = new byte[size];
                _batchBuffer = buffer;
            }

            return buffer;
        }

        private static void HashData(IntPtr hash, byte[] buffer, int length)
        {
            fixed (byte* data = buffer)
            {
                int hr = BCryptHashData(hash, data, length, 0);
                if (hr != 0)
                {
                    throw new InvalidOperationException($"BCryptHashData 失败（0x{hr:X8}）。");
                }
            }
        }

        private static void FinishHash(IntPtr hash, byte[] digest)
        {
            fixed (byte* output = digest)
            {
                int hr = BCryptFinishHash(hash, output, digest.Length, 0);
                if (hr != 0)
                {
                    throw new InvalidOperationException($"BCryptFinishHash 失败（0x{hr:X8}）。");
                }
            }
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(
            out IntPtr phAlgorithm,
            string pszAlgId,
            string? pszImplementation,
            uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptCreateHash(
            IntPtr hAlgorithm,
            out IntPtr phHash,
            IntPtr pbHashObject,
            int cbHashObject,
            IntPtr pbSecret,
            int cbSecret,
            uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptHashData(IntPtr hHash, byte* pbInput, int cbInput, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptFinishHash(IntPtr hHash, byte* pbOutput, int cbOutput, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyHash(IntPtr hHash);
    }
}
