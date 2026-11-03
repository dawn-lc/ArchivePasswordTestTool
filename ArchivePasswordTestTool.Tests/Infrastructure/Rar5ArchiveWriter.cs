using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 合成 RAR5（WinRAR -ma5）存档的参数。
/// </summary>
/// <remarks>
/// <see cref="DataSalt"/> / <see cref="DataIv"/> / <see cref="HeaderSalt"/> 由调用方给定固定值，保证同一测试每次生成的字节完全一致。
/// </remarks>
internal sealed record Rar5WriterOptions
{
    /// <summary>归档内文件名（UTF-8 编码后最多 2047 字节）</summary>
    public required string EntryName { get; init; }

    /// <summary>明文内容</summary>
    public required byte[] Plaintext { get; init; }

    /// <summary>密码（RAR5 用 UTF-8 编码参与 KDF，与 RAR4 的 UTF-16LE 不同）</summary>
    public required string Password { get; init; }

    /// <summary>文件数据加密的盐（16 字节）</summary>
    public required byte[] DataSalt { get; init; }

    /// <summary>文件数据加密的初始向量（16 字节）</summary>
    public required byte[] DataIv { get; init; }

    /// <summary>PBKDF2 迭代次数的 log2；真实归档取 15（32768 次）</summary>
    public int KdfCount { get; init; } = Rar5ArchiveWriter.DefaultKdfCount;

    /// <summary>是否加密头部（WinRAR 的 -hp）</summary>
    public bool EncryptHeaders { get; init; }

    /// <summary>头部加密的盐（16 字节）；<see cref="EncryptHeaders"/> 为 true 时必需</summary>
    public byte[]? HeaderSalt { get; init; }

    /// <summary>SFX 桩字节（MZ 等），置于签名之前</summary>
    public byte[]? SfxStub { get; init; }

    /// <summary>分卷数量；1 表示单卷</summary>
    public int VolumeCount { get; init; } = 1;
}

/// <summary>
/// 纯托管合成 RAR5（WinRAR -ma5）存档，用于摆脱测试对 WinRAR 的依赖。
/// <para>
/// 结构完全按 rarlab.com 公开的 RAR5 技术说明实现，并在编写时用真实 WinRAR 7.21 生成的样本逐字段标定：
/// 签名 8 字节、通用头 [CRC32][headerSize 变长整数][type][flags][extraSize?][dataSize?][类型字段][extra][data]。
/// </para>
/// <para>
/// 两个容易被误解的细节（都是标定出来的，不是读文档读出来的）：
/// <list type="number">
/// <item>headerSize <b>不含</b> headerSize 字段自身，只从 type 字段数到 extra area 末尾；</item>
/// <item>Header CRC32 覆盖范围是 [headerSize 字段 .. extra area 末尾]，也就是「headerSize 的首字节」到「extra 的末字节」，不含 4 字节 CRC 自身也不含 data area。</item>
/// </list>
/// </para>
/// <para>
/// 两处刻意省略，都是为了避开技术说明未公开的算法：
/// <list type="bullet">
/// <item>文件加密记录 flags 置 0：不写 PswCheck（12 字节校验值，算法只在 UnRAR 源码里），
/// 也不用 tweaked checksums（Data CRC32 随密钥变换）。技术说明里这两项都是可选的。</item>
/// <item>归档加密头 EncryptionFlags 同样置 0。代价是 7-Zip 必须解密后才能判错——
/// 落错密码时头部 CRC32 不匹配，效果等同于「密码错误」，正是本项目关心的情况。</item>
/// </list>
/// 作为对照：真实 WinRAR 会置位，若将来需要逐字节复刻，得先解决这两个未公开算法。
/// </para>
/// <para>
/// 与 <see cref="Rar4ArchiveWriter"/> 的一个关键差别：这里不使用生产代码派生的密钥
/// （生产侧根本没有 RAR5 KDF，密码由 7-Zip 自己校验），所以本写入器不具备「反向验证生产 KDF」的作用；
/// 它反向验证的是 7-Zip 能解开，也即本文件对 RAR5 结构的理解没有偏差。
/// </para>
/// </summary>
internal static class Rar5ArchiveWriter
{
    /// <summary>RAR5 签名（RAR4 是 7 字节且末字节为 0x00，这里末字节为 0x00、倒数第二字节为 0x01）</summary>
    private static readonly byte[] Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

    public const int DefaultKdfCount = 15;
    public const int SaltSize = 16;
    public const int IvSize = 16;
    public const int BlockSize = 16;

    /// <summary>密钥长度：RAR5 固定 AES-256</summary>
    public const int KeySize = 32;

    private const ulong HeaderTypeMain = 1;
    private const ulong HeaderTypeFile = 2;
    private const ulong HeaderTypeEncryption = 4;
    private const ulong HeaderTypeEnd = 5;

    /// <summary>通用头标志：存在 extra area</summary>
    private const ulong HeaderFlagExtraArea = 0x0001;

    /// <summary>通用头标志：存在 data area</summary>
    private const ulong HeaderFlagDataArea = 0x0002;

    /// <summary>通用头标志：未知头可跳过</summary>
    private const ulong HeaderFlagSkipUnknown = 0x0004;

    /// <summary>通用头标志：数据从上一分卷延续</summary>
    private const ulong HeaderFlagDataFromPreviousVolume = 0x0008;

    /// <summary>通用头标志：数据在下一分卷延续</summary>
    private const ulong HeaderFlagDataContinuesInNextVolume = 0x0010;

    /// <summary>主头 ArchiveFlags：多卷归档</summary>
    private const ulong ArchiveFlagVolume = 0x0001;

    /// <summary>主头 ArchiveFlags：带卷号字段</summary>
    private const ulong ArchiveFlagVolumeNumberPresent = 0x0002;

    /// <summary>文件头 FileFlags：带 Data CRC32</summary>
    private const ulong FileFlagCrc32 = 0x0004;

    /// <summary>结束头 EndFlags：非最后一卷</summary>
    private const ulong EndFlagMoreVolumes = 0x0001;

    /// <summary>文件属性：FILE_ATTRIBUTE_ARCHIVE（与 WinRAR 一致，便于对照）</summary>
    private const ulong FileAttributeArchive = 0x20;

    /// <summary>
    /// 压缩信息：低 6 位是压缩算法版本，7-9 位是压缩方法（0 表示不压缩），10 位起是字典大小指数。
    /// 存储模式即整体取 0。
    /// </summary>
    private const ulong CompressionInfoStore = 0;

    /// <summary>宿主 OS：0 = Windows</summary>
    private const ulong HostOsWindows = 0;

    /// <summary>
    /// 生成归档，返回写出的文件路径（按分卷顺序）。
    /// </summary>
    /// <param name="basePath">基路径：单卷时即输出文件名；多卷时按 base.part1.rar、base.part2.rar… 命名（传入的序号后缀会被忽略）</param>
    public static IReadOnlyList<string> Create(string basePath, Rar5WriterOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.VolumeCount, 1);

        if (options.DataSalt.Length != SaltSize)
        {
            throw new ArgumentException($"DataSalt 必须为 {SaltSize} 字节", nameof(options));
        }

        if (options.DataIv.Length != IvSize)
        {
            throw new ArgumentException($"DataIv 必须为 {IvSize} 字节", nameof(options));
        }

        if (options.EncryptHeaders && options.HeaderSalt is not { Length: SaltSize })
        {
            throw new ArgumentException($"EncryptHeaders 为 true 时 HeaderSalt 必须为 {SaltSize} 字节", nameof(options));
        }

        byte[] cipher = EncryptData(options);

        var volumes = SplitEvenly(cipher.Length, options.VolumeCount);
        string[] paths = BuildVolumePaths(basePath, volumes.Length);

        for (int i = 0; i < volumes.Length; i++)
        {
            (int offset, int length) = volumes[i];
            bool isFirst = i == 0;
            bool isLast = i == volumes.Length - 1;

            byte[] volume = BuildVolume(options, cipher, offset, length, i, volumes.Length, isFirst, isLast);

            if (isFirst && options.SfxStub is { Length: > 0 } stub)
            {
                byte[] withStub = new byte[stub.Length + volume.Length];
                stub.CopyTo(withStub, 0);
                volume.CopyTo(withStub, stub.Length);
                volume = withStub;
            }

            File.WriteAllBytes(paths[i], volume);
        }

        return paths;
    }

    /// <summary>
    /// 单卷、仅数据加密、存储模式：最接近「弱校验 + 明文首块」的目标场景。
    /// </summary>
    public static string CreateStoredEncrypted(string path, string entryName, byte[] plaintext, string password, byte[] salt, byte[] iv)
    {
        var options = new Rar5WriterOptions
        {
            EntryName = entryName,
            Plaintext = plaintext,
            Password = password,
            DataSalt = salt,
            DataIv = iv,
        };

        return Create(path, options)[0];
    }

    /// <summary>
    /// 单卷、头部 + 数据加密：对应 WinRAR 的 -hp，密码在打开归档时即被校验。
    /// </summary>
    public static string CreateHeaderEncrypted(string path, string entryName, byte[] plaintext, string password, byte[] dataSalt, byte[] dataIv, byte[] headerSalt)
    {
        var options = new Rar5WriterOptions
        {
            EntryName = entryName,
            Plaintext = plaintext,
            Password = password,
            DataSalt = dataSalt,
            DataIv = dataIv,
            EncryptHeaders = true,
            HeaderSalt = headerSalt,
        };

        return Create(path, options)[0];
    }

    // ---------- 加密 ----------

    /// <summary>
    /// RAR5 密钥派生：PBKDF2-HMAC-SHA256(UTF-8 密码, 盐, 2^kdfCount 次, 32 字节)。
    /// 已用真实 WinRAR 7.21 样本标定：kdfCount=15、UTF-8 编码、AES-256-CBC、无填充（明文补零到 16 的倍数）。
    /// </summary>
    private static byte[] DeriveKey(string password, byte[] salt, int kdfCount)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 1 << kdfCount, HashAlgorithmName.SHA256, KeySize);
    }

    private static byte[] EncryptData(Rar5WriterOptions options)
    {
        int paddedLength = (options.Plaintext.Length + BlockSize - 1) / BlockSize * BlockSize;
        byte[] padded = new byte[paddedLength];
        options.Plaintext.CopyTo(padded, 0);

        byte[] key = DeriveKey(options.Password, options.DataSalt, options.KdfCount);
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = options.DataIv;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(padded, 0, padded.Length);
    }

    // ---------- 分卷与命名 ----------

    /// <summary>把密文均分为若干段，段边界对齐到 16 字节块（最后一段吸收余数）</summary>
    private static (int Offset, int Length)[] SplitEvenly(int total, int volumeCount)
    {
        var result = new (int Offset, int Length)[volumeCount];
        if (volumeCount == 1)
        {
            result[0] = (0, total);
            return result;
        }

        int perVolume = total / volumeCount;
        perVolume -= perVolume % BlockSize;
        if (perVolume <= 0)
        {
            throw new ArgumentException("明文过短，无法按请求的卷数切分", nameof(volumeCount));
        }

        int offset = 0;
        for (int i = 0; i < volumeCount; i++)
        {
            int length = i == volumeCount - 1 ? total - offset : perVolume;
            result[i] = (offset, length);
            offset += length;
        }

        return result;
    }

    private static string[] BuildVolumePaths(string basePath, int volumeCount)
    {
        if (volumeCount == 1)
        {
            return [basePath];
        }

        string directory = Path.GetDirectoryName(basePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(basePath);
        if (stem.EndsWith(".part1", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^".part1".Length];
        }

        var paths = new string[volumeCount];
        for (int i = 0; i < volumeCount; i++)
        {
            paths[i] = Path.Combine(directory, $"{stem}.part{i + 1}.rar");
        }

        return paths;
    }

    // ---------- 单卷装配 ----------

    private static byte[] BuildVolume(
        Rar5WriterOptions options,
        byte[] cipher,
        int dataOffset,
        int dataLength,
        int volumeIndex,
        int volumeCount,
        bool isFirst,
        bool isLast)
    {
        var volume = new List<byte>(cipher.Length / volumeCount + 512);
        volume.AddRange(Signature);

        byte[]? headerKey = null;
        if (options.EncryptHeaders)
        {
            headerKey = DeriveKey(options.Password, options.HeaderSalt!, options.KdfCount);

            // 归档加密头本身是明文，从它之后的每一个头才套 IV + 密文
            AppendHeader(volume, BuildArchiveEncryptionHeader(options), headerKey: null, headerSalt: null, volumeIndex, 0);
        }

        ulong mainFlags = HeaderFlagSkipUnknown;
        ulong archiveFlags = 0;
        ulong? volumeNumber = null;
        if (volumeCount > 1)
        {
            archiveFlags |= ArchiveFlagVolume;
            if (!isFirst)
            {
                // 与 WinRAR 一致：首卷不写卷号（缺省即 0），后续卷用 0 基序号
                archiveFlags |= ArchiveFlagVolumeNumberPresent;
                volumeNumber = (ulong)(volumeIndex);
            }
        }

        AppendHeader(volume, BuildMainHeader(mainFlags, archiveFlags, volumeNumber), headerKey, options.HeaderSalt, volumeIndex, 1);

        // Data CRC32 在分卷归档里是两套不同语义，这是读 7-Zip 的 Rar5Handler 才弄清的：
        // ① 非末卷（头部带「数据在下一卷延续」标志）：7-Zip 在 CVolsInStream 里对**本卷的打包数据（密文）**算 CRC32，再与该卷头部的 CRC 比对；
        // ② 末卷（不带延续标志）：7-Zip 在 CUnpacker 里对**整个文件的解包数据（明文）**算 CRC32，与末卷头部的 CRC 比对。
        // 写错任何一条都是 "CRC Failed in encrypted file. Wrong password?"——报错信息完全误导，像是密码错了。
        // 单卷时只有「末卷」这一种情形，所以 Crc32(明文) 就是对的；WinRAR 各卷 CRC 互不相同，正是因为它写的是各卷密文 CRC。
        uint dataCrc = isLast
            ? Crc32.Compute(options.Plaintext)
            : Crc32.Compute(cipher.AsSpan(dataOffset, dataLength));

        ulong fileFlags = HeaderFlagExtraArea | HeaderFlagDataArea;
        if (!isFirst)
        {
            fileFlags |= HeaderFlagDataFromPreviousVolume;
        }

        if (!isLast)
        {
            fileFlags |= HeaderFlagDataContinuesInNextVolume;
        }

        AppendHeader(volume, BuildFileHeader(options, fileFlags, dataLength, dataCrc), headerKey, options.HeaderSalt, volumeIndex, 2);

        volume.AddRange(cipher.AsSpan(dataOffset, dataLength));

        AppendHeader(volume, BuildEndHeader(isLast), headerKey, options.HeaderSalt, volumeIndex, 3);

        return [.. volume];
    }

    /// <summary>明文写入，或按头部加密规则封装（16 字节 IV + 16 字节对齐的密文块）</summary>
    private static void AppendHeader(List<byte> volume, byte[] header, byte[]? headerKey, byte[]? headerSalt, int volumeIndex, int headerIndex)
    {
        if (headerKey is null)
        {
            volume.AddRange(header);
            return;
        }

        byte[] iv = DeterministicIv(headerSalt!, volumeIndex, headerIndex);
        int paddedLength = (header.Length + BlockSize - 1) / BlockSize * BlockSize;
        byte[] padded = new byte[paddedLength];
        header.CopyTo(padded, 0);

        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = headerKey;
        aes.IV = iv;
        using ICryptoTransform encryptor = aes.CreateEncryptor();

        volume.AddRange(iv);
        volume.AddRange(encryptor.TransformFinalBlock(padded, 0, padded.Length));
    }

    /// <summary>
    /// 头部 IV 的确定性生成：同一 (盐, 卷号, 头序号) 永远得到同一 IV，
    /// 这样生成的归档字节稳定，测试可以断言体积与偏移。
    /// </summary>
    private static byte[] DeterministicIv(byte[] salt, int volumeIndex, int headerIndex)
    {
        Span<byte> input = stackalloc byte[salt.Length + 8];
        salt.CopyTo(input);
        BinaryPrimitives.WriteInt32LittleEndian(input[salt.Length..], volumeIndex);
        BinaryPrimitives.WriteInt32LittleEndian(input[(salt.Length + 4)..], headerIndex);

        return SHA256.HashData(input)[..IvSize];
    }

    // ---------- 头部构造 ----------

    private static byte[] BuildMainHeader(ulong flags, ulong archiveFlags, ulong? volumeNumber)
    {
        var body = new List<byte>();
        WriteVint(body, HeaderTypeMain);
        WriteVint(body, flags);
        WriteVint(body, archiveFlags);
        if (volumeNumber.HasValue)
        {
            WriteVint(body, volumeNumber.Value);
        }

        return FinishHeader(body);
    }

    private static byte[] BuildFileHeader(Rar5WriterOptions options, ulong flags, int volumeDataLength, uint dataCrc)
    {
        byte[] name = Encoding.UTF8.GetBytes(options.EntryName);
        byte[] extra = BuildFileEncryptionExtra(options);

        var body = new List<byte>();
        WriteVint(body, HeaderTypeFile);
        WriteVint(body, flags);
        WriteVint(body, (ulong)extra.Length);
        WriteVint(body, (ulong)volumeDataLength);
        WriteVint(body, FileFlagCrc32);

        // 每卷都写文件总长：7-Zip 只取末卷的 Size 作为解包长度，其余卷的 Size 不参与解包，
        // 但保持与 WinRAR 一致的「每卷都写全量」更贴近真实归档
        WriteVint(body, (ulong)options.Plaintext.Length);
        WriteVint(body, FileAttributeArchive);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, dataCrc);
        body.AddRange(crcBytes.ToArray());

        WriteVint(body, CompressionInfoStore);
        WriteVint(body, HostOsWindows);
        WriteVint(body, (ulong)name.Length);
        body.AddRange(name);
        body.AddRange(extra);

        return FinishHeader(body);
    }

    /// <summary>
    /// 文件加密记录（extra area 类型 0x01）：type + version + flags + kdfCount + salt(16) + iv(16) + [check(12)]。
    /// flags 置 0 表示既没有 PswCheck 也不用 tweaked checksums，因此不写 check 字段。
    /// </summary>
    private static byte[] BuildFileEncryptionExtra(Rar5WriterOptions options)
    {
        var record = new List<byte>();
        WriteVint(record, 1); // extra 记录类型：FileEncryption
        WriteVint(record, 0); // 加密版本：0 = AES-256
        WriteVint(record, 0); // 加密标志：无 PswCheck、无 tweaked checksums
        record.Add((byte)options.KdfCount);
        record.AddRange(options.DataSalt);
        record.AddRange(options.DataIv);

        var extra = new List<byte>(record.Count + 2);
        WriteVint(extra, (ulong)record.Count); // 记录长度覆盖 type 字段到记录末尾
        extra.AddRange(record);
        return [.. extra];
    }

    private static byte[] BuildArchiveEncryptionHeader(Rar5WriterOptions options)
    {
        var body = new List<byte>();
        WriteVint(body, HeaderTypeEncryption);
        WriteVint(body, 0); // 通用标志：既无 extra area 也无 data area
        WriteVint(body, 0); // 加密版本：0 = AES-256
        WriteVint(body, 0); // 加密标志：不写 PswCheck
        body.Add((byte)options.KdfCount);
        body.AddRange(options.HeaderSalt!);

        return FinishHeader(body);
    }

    private static byte[] BuildEndHeader(bool isLast)
    {
        var body = new List<byte>();
        WriteVint(body, HeaderTypeEnd);
        WriteVint(body, HeaderFlagSkipUnknown);
        WriteVint(body, isLast ? 0 : EndFlagMoreVolumes);

        return FinishHeader(body);
    }

    /// <summary>
    /// 收尾：补 headerSize（从 type 字段开始的长度）与 Header CRC32（覆盖 headerSize 字段起、到 extra area 末尾）。
    /// </summary>
    private static byte[] FinishHeader(List<byte> body)
    {
        var data = new List<byte>(body.Count + 10);
        WriteVint(data, (ulong)body.Count);
        data.AddRange(body);

        byte[] result = new byte[data.Count + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, Crc32.Compute(data.ToArray()));
        data.CopyTo(result, 4);
        return result;
    }

    /// <summary>RAR5 变长整数：每字节低 7 位是数据，最高位表示后续还有字节</summary>
    private static void WriteVint(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }
}
