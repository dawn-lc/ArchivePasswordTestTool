namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 传统 Zip 加密（ZipCrypto / PKZIP 2.0）的密钥流，等价 Info-ZIP
    /// <c>zipcloak</c>、7-Zip <c>Crypto/ZipCrypto.cpp</c> 的实现。
    /// <para>
    /// 算法本身不提供任何强度（已知明文可恢复密钥），快速判错只需它的**确定性**：
    /// 密码正确才能解出合法的文件首块。
    /// </para>
    /// <para>
    /// 状态是 3 个 32 位 key，<see cref="Reset"/> 后逐字节解密会自动更新 key。
    /// 用 struct 让调用方把它放栈上，避免每次尝试都分配对象。
    /// </para>
    /// </summary>
    internal struct ZipCryptoCipher
    {
        private uint _key0;
        private uint _key1;
        private uint _key2;

        /// <summary>12 字节加密头长度（其中最后一字节是校验值）</summary>
        public const int HeaderLength = 12;

        /// <summary>
        /// CRC32 表（多项式 0xEDB88320）。标准 zip 表，
        /// key 更新用的是"不取反"的 CRC 变体，因此单独保留一份表。
        /// </summary>
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }

        private static uint Crc32(uint crc, byte value) =>
            CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

        /// <summary>用密码字节初始化密钥流（重复调用是安全的：会重置状态）</summary>
        public void Reset(ReadOnlySpan<byte> password)
        {
            _key0 = 0x12345678;
            _key1 = 0x23456789;
            _key2 = 0x34567890;

            foreach (byte b in password)
            {
                UpdateKeys(b);
            }
        }

        /// <summary>解一个字节（同时推进 key）</summary>
        public byte Decrypt(byte cipher)
        {
            byte plain = (byte)(cipher ^ NextKeyByte());
            UpdateKeys(plain);
            return plain;
        }

        /// <summary>
        /// 就地解密。密钥流与位置相关，必须从加密头开始连续调用。
        /// </summary>
        public void Decrypt(Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Decrypt(data[i]);
            }
        }

        private void UpdateKeys(byte value)
        {
            _key0 = Crc32(_key0, value);
            _key1 = (_key1 + (_key0 & 0xFF)) * 134775813u + 1u;
            _key2 = Crc32(_key2, (byte)(_key1 >> 24));
        }

        private byte NextKeyByte()
        {
            uint temp = (_key2 & 0xFFFF) | 2;
            return (byte)((temp * (temp ^ 1)) >> 8);
        }
    }
}
