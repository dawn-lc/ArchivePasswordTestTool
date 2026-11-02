using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 合成 RAR4（WinRAR -ma4）存档，仅供测试使用。
/// <para>
/// 本机通常没有 WinRAR（真实 RAR 样本靠 <see cref="TestDataFixture"/> 生成，无 WinRAR 时跳过），
/// 而「快速判错」的正确性必须有一个**已知密码**的 RAR4 存档才能验证。
/// </para>
/// <para>
/// 关键点：这里用生产代码的 <see cref="Rar4KeyDerivation"/> 派生密钥并加密。
/// 若该 KDF 与真实 RAR4 算法有任何偏差，7-Zip 将无法解开本函数生成的存档——
/// 因此「用主程序/7-Zip 解开合成存档」这一断言同时反向验证了 KDF 实现。
/// 存档未压缩（method 0x30），与真实快速判错的目标场景一致。
/// </para>
/// </summary>
internal static class Rar4ArchiveWriter
{
    private const byte MainHeadType = 0x73;
    private const byte FileHeadType = 0x74;
    private const byte EndHeadType = 0x7B;

    private const ushort LhdPassword = 0x0004;
    private const ushort LhdSalt = 0x0400;

    private const byte UnpackVersion = 29;
    private const byte MethodStored = 0x30;

    /// <summary>
    /// 生成一个「单文件 + 仅数据加密 + 存储模式」的 RAR4 存档。
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="entryName">归档内文件名（扩展名决定快速判错用的签名）</param>
    /// <param name="plaintext">明文内容</param>
    /// <param name="password">密码</param>
    /// <param name="salt">8 字节盐（测试传固定值以保证可重复）</param>
    public static void CreateStoredEncrypted(string path, string entryName, byte[] plaintext, string password, byte[] salt)
    {
        if (salt.Length != Rar4KeyDerivation.SaltSize)
        {
            throw new ArgumentException($"盐必须为 {Rar4KeyDerivation.SaltSize} 字节", nameof(salt));
        }

        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] key = new byte[16];
        byte[] iv = new byte[16];
        Rar4KeyDerivation.DeriveKey(passwordBytes, salt, key, iv);

        // CBC 无填充：明文补零到 16 的倍数（RAR4 存储模式就是这么做的）
        int paddedLength = (plaintext.Length + 15) / 16 * 16;
        byte[] padded = new byte[paddedLength];
        plaintext.CopyTo(padded, 0);

        byte[] cipher;
        using (Aes aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = iv;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(padded, 0, padded.Length);
        }

        byte[] name = Encoding.ASCII.GetBytes(entryName);
        int fileHeaderSize = 32 + name.Length + salt.Length;
        byte[] fileHeader = new byte[fileHeaderSize];

        fileHeader[2] = FileHeadType;
        BinaryPrimitives.WriteUInt16LittleEndian(fileHeader.AsSpan(3), LhdPassword | LhdSalt);
        BinaryPrimitives.WriteUInt16LittleEndian(fileHeader.AsSpan(5), (ushort)fileHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(7), (uint)cipher.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(11), (uint)plaintext.Length);
        fileHeader[15] = 2;                                                  // HOST_OS = Win32
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(16), Crc32(plaintext));
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(20), DosTimestamp(new DateTime(2024, 1, 2, 3, 4, 6)));
        fileHeader[24] = UnpackVersion;
        fileHeader[25] = MethodStored;
        BinaryPrimitives.WriteUInt16LittleEndian(fileHeader.AsSpan(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(28), 0x20); // FILE_ATTRIBUTE_ARCHIVE
        name.CopyTo(fileHeader, 32);
        salt.CopyTo(fileHeader, 32 + name.Length);
        WriteHeadCrc(fileHeader);

        byte[] mainHeader = new byte[13];
        mainHeader[2] = MainHeadType;
        BinaryPrimitives.WriteUInt16LittleEndian(mainHeader.AsSpan(5), (ushort)mainHeader.Length);
        WriteHeadCrc(mainHeader);

        byte[] endHeader = new byte[7];
        endHeader[2] = EndHeadType;
        BinaryPrimitives.WriteUInt16LittleEndian(endHeader.AsSpan(5), (ushort)endHeader.Length);
        WriteHeadCrc(endHeader);

        using var output = File.Create(path);
        output.Write(Rar4Signature);
        output.Write(mainHeader);
        output.Write(fileHeader);
        output.Write(cipher);
        output.Write(endHeader);
    }

    /// <summary>
    /// 构造一个以 MP4 首盒开头的明文（长度 40 → 密文 48），
    /// 使其命中快速判错的 MP4 签名路径。
    /// </summary>
    public static byte[] CreateMp4LikePlaintext()
    {
        byte[] plaintext = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(plaintext, 0x18);            // box size = 24
        Encoding.ASCII.GetBytes("ftyp").CopyTo(plaintext, 4);
        Encoding.ASCII.GetBytes("isom").CopyTo(plaintext, 8);
        BinaryPrimitives.WriteUInt32BigEndian(plaintext.AsSpan(12), 0x200);
        Encoding.ASCII.GetBytes("isomiso2").CopyTo(plaintext, 16);
        for (int i = 24; i < plaintext.Length; i++)
        {
            plaintext[i] = (byte)(i * 7 + 3);
        }

        return plaintext;
    }

    private static readonly byte[] Rar4Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>RAR4 头部校验：HEAD_CRC = CRC32(header[2..]) 的低 16 位</summary>
    private static void WriteHeadCrc(byte[] header)
    {
        ushort crc = (ushort)Crc32(header.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(header, crc);
    }

    private static uint DosTimestamp(DateTime time) =>
        (uint)(((time.Year - 1980) << 25) | (time.Month << 21) | (time.Day << 16)
               | (time.Hour << 11) | (time.Minute << 5) | (time.Second / 2));

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

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
