using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// RAR3 / RAR4（WinRAR -ma4，UNP_VER ≥ 29）密码派生，等价 7-Zip
    /// <c>Crypto/RarAes.cpp</c> 的 <c>CDecoder::CalcKey</c>：
    /// <list type="number">
    /// <item>一条连续的 SHA1 流，共 2^18 轮；每轮追加「密码字节 + 8 字节盐 + 3 字节小端计数器」</item>
    /// <item>第 i 轮（i 为 16384 的整数倍）取当前摘要的最后一个字节（digest[19]）作为 IV 的第 i/16384 个字节</item>
    /// <item>结束时把摘要按 4 字节分组逆序，得到 128 位 AES 密钥</item>
    /// </list>
    /// <para>
    /// 密码字节在 UNP_VER ≥ 29 时是 <b>UTF-16LE</b>（7-Zip RarHandler.cpp 逐 wchar 取低/高字节），
    /// 不是 UTF-8——写错这个细节会导致派生的密钥全错。
    /// </para>
    /// <para>
    /// 用 Windows CNG（bcrypt）而非托管 SHA1：<c>BCryptDuplicateHash</c> 可以复制中间状态
    /// （.NET 的 <c>IncrementalHash</c> 不暴露状态，无法在 16 个检查点取摘要），
    /// 且 CNG 的 SHA1 走硬件指令，实测比手写托管实现快约 2 倍（10.9ms vs 22.4ms/候选）。
    /// </para>
    /// </summary>
    internal static unsafe class Rar3KeyDerivation
    {
        public const int SaltSize = 8;

        /// <summary>SHA1 总轮数（与 7-Zip 的 kNumRounds 一致）</summary>
        public const int Rounds = 1 << 18;

        /// <summary>IV 检查点间隔：每 16384 轮取一次摘要末字节</summary>
        private const int CheckpointStep = Rounds / 16;

        /// <summary>每次 BCryptHashData 处理的记录数（减少 P/Invoke 次数）</summary>
        private const int RecordsPerBatch = 1024;

        /// <summary>
        /// 单条记录（密码 + 盐）的长度上限。超过 64 字节时 7-Zip 的 CalcKey 会进入
        /// RAR 3.5 兼容分支（UpdatePswDataSha1 就地改写缓冲），本实现不支持，
        /// 调用方必须对该候选退回完整测密。
        /// </summary>
        public const int MaxRawRecordSize = 64;

        /// <summary>单条记录最大值对应的 UTF-16 密码字符数：(64 - 8) / 2</summary>
        public const int MaxPasswordChars = (MaxRawRecordSize - SaltSize) / 2;

        private const string AlgorithmId = "SHA1";

        private static readonly object ProviderLock = new();
        private static IntPtr _provider;
        private static bool _providerUnavailable;

        [ThreadStatic]
        private static byte[]? _batchBuffer;

        /// <summary>
        /// CNG 的 SHA1 提供程序是否可用。不可用时调用方应整体关闭快速判错，
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
        /// 派生 AES-128 密钥与 IV。
        /// </summary>
        /// <param name="passwordBytes">密码字节（UTF-16LE，长度 ≤ 56）</param>
        /// <param name="salt">8 字节盐</param>
        /// <param name="key">输出：16 字节密钥</param>
        /// <param name="iv">输出：16 字节 IV</param>
        public static void DeriveKey(ReadOnlySpan<byte> passwordBytes, ReadOnlySpan<byte> salt, Span<byte> key, Span<byte> iv)
        {
            if (salt.Length != SaltSize)
            {
                throw new ArgumentException($"盐长度必须为 {SaltSize}", nameof(salt));
            }

            int rawSize = passwordBytes.Length + salt.Length;
            if (rawSize > MaxRawRecordSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(passwordBytes), rawSize, $"单条记录不得超过 {MaxRawRecordSize} 字节（RAR 3.5 兼容分支未实现）");
            }

            EnsureProvider();
            if (_provider == IntPtr.Zero)
            {
                throw new InvalidOperationException("Windows CNG SHA1 提供程序不可用。");
            }

            int step = rawSize + 3;
            byte[] batch = RentBatch(RecordsPerBatch * step);
            Span<byte> digest = stackalloc byte[20];

            int hr = BCryptCreateHash(_provider, out IntPtr hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0);
            if (hr != 0)
            {
                throw new InvalidOperationException($"BCryptCreateHash 失败（0x{hr:X8}）。");
            }

            int used = 0;
            try
            {
                for (int i = 0; i < Rounds; i++)
                {
                    if (used + step > batch.Length)
                    {
                        HashData(hash, batch, used);
                        used = 0;
                    }

                    passwordBytes.CopyTo(batch.AsSpan(used));
                    salt.CopyTo(batch.AsSpan(used + passwordBytes.Length));

                    int offset = used + rawSize;
                    batch[offset] = (byte)i;
                    batch[offset + 1] = (byte)(i >> 8);
                    batch[offset + 2] = (byte)(i >> 16);
                    used += step;

                    // 7-Zip：if (i % (kNumRounds / 16) == 0) → 取当前状态的摘要末字节。
                    // 必须先冲刷本批缓冲：CNG 的哈希对象只包含已提交的字节，
                    // 直接 Duplicate 会取到滞后最多 RecordsPerBatch 条记录的状态（IV 全错）。
                    if ((i & (CheckpointStep - 1)) == 0)
                    {
                        if (used > 0)
                        {
                            HashData(hash, batch, used);
                            used = 0;
                        }

                        SnapshotDigest(hash, digest);
                        iv[i >> 14] = digest[19];
                    }
                }

                if (used > 0)
                {
                    HashData(hash, batch, used);
                }

                FinishHash(hash, digest);
            }
            finally
            {
                BCryptDestroyHash(hash);
            }

            // 7-Zip：_key[i * 4 + j] = digest[i * 4 + 3 - j]
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    key[i * 4 + j] = digest[i * 4 + 3 - j];
                }
            }
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

        private static void SnapshotDigest(IntPtr hash, Span<byte> digest)
        {
            int hr = BCryptDuplicateHash(hash, out IntPtr duplicate, IntPtr.Zero, 0, 0);
            if (hr != 0)
            {
                throw new InvalidOperationException($"BCryptDuplicateHash 失败（0x{hr:X8}）。");
            }

            try
            {
                FinishHash(duplicate, digest);
            }
            finally
            {
                BCryptDestroyHash(duplicate);
            }
        }

        private static void FinishHash(IntPtr hash, Span<byte> digest)
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
        private static extern int BCryptDuplicateHash(
            IntPtr hHash,
            out IntPtr phNewHash,
            IntPtr pbHashObject,
            int cbHashObject,
            uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptHashData(IntPtr hHash, byte* pbInput, int cbInput, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptFinishHash(IntPtr hHash, byte* pbOutput, int cbOutput, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyHash(IntPtr hHash);
    }
}
