using System.Buffers.Binary;
using System.Text;

namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// RAR5 加密记录（extra area 类型 0x01）的字段。
/// </summary>
internal sealed record Rar5EncryptionRecord(ulong Version, ulong Flags, int KdfCount, byte[] Salt, byte[] Iv, byte[]? CheckValue)
{
    /// <summary>加密标志：带 PswCheck（12 字节密码校验值）</summary>
    public const ulong FlagPasswordCheck = 0x0001;

    /// <summary>加密标志：用 tweaked checksums（CRC/哈希随密钥变换）</summary>
    public const ulong FlagTweakedChecksums = 0x0002;

    /// <summary>是否带 PswCheck。真实 WinRAR 会置位；合成归档刻意不置位（算法未公开）</summary>
    public bool HasPasswordCheck => CheckValue is not null;

    /// <summary>
    /// 是否用 tweaked checksums。置位时 Data CRC32 / 哈希是按密钥变换后的值，不等于明文的普通 CRC32。
    /// 真实 WinRAR 默认置位；合成归档不置位（算法未公开），因此它写的 CRC32 就是明文 CRC32。
    /// </summary>
    public bool HasTweakedChecksums => (Flags & FlagTweakedChecksums) != 0;
}

/// <summary>一个已解析的 RAR5 头部。</summary>
internal sealed record Rar5Header
{
    /// <summary>头部类型，见 <see cref="Rar5HeaderReader"/> 的 Type 常量</summary>
    public required ulong Type { get; init; }

    /// <summary>通用头 flags（<see cref="Rar5HeaderReader.FlagExtraArea"/> 等），注意与 <see cref="FileFlags"/> 区分</summary>
    public required ulong Flags { get; init; }

    /// <summary>头部长度：从 type 字段起算到 extra area 末尾，<b>不含</b> headerSize 字段自身</summary>
    public required ulong HeaderSize { get; init; }

    public required uint StoredCrc { get; init; }

    /// <summary>4 字节 CRC 在归档中的偏移</summary>
    public required int Offset { get; init; }

    /// <summary>headerSize 字段的首字节偏移（Header CRC32 覆盖范围的起点）</summary>
    public required int SizeFieldStart { get; init; }

    /// <summary>type 字段的首字节偏移</summary>
    public required int BodyStart { get; init; }

    /// <summary>头部正文（含 extra area）的结束偏移，等于 Header CRC32 覆盖范围的终点</summary>
    public required int BodyEnd { get; init; }

    /// <summary>data area 起始偏移</summary>
    public required int DataStart { get; init; }

    /// <summary>data area 长度</summary>
    public required int DataLength { get; init; }

    public required ulong ExtraSize { get; init; }

    /// <summary>extra area 里出现的记录类型（按出现顺序）</summary>
    public required IReadOnlyList<ulong> ExtraTypes { get; init; }

    // ---------- 主头 ----------
    public ulong ArchiveFlags { get; init; }

    public ulong? VolumeNumber { get; init; }

    // ---------- 文件 / 服务头 ----------
    public ulong FileFlags { get; init; }

    public ulong UnpackedSize { get; init; }

    public ulong Attributes { get; init; }

    public uint? DataCrc { get; init; }

    public ulong CompressionInfo { get; init; }

    public ulong HostOs { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>文件加密记录；未加密时为 null</summary>
    public Rar5EncryptionRecord? FileEncryption { get; init; }

    // ---------- 归档加密头 ----------
    public ulong EncryptionVersion { get; init; }

    public ulong EncryptionFlags { get; init; }

    // ---------- 结束头 ----------
    public ulong EndFlags { get; init; }

    public bool HasFlag(ulong flag) => (Flags & flag) != 0;

    public bool HasFileFlag(ulong flag) => (FileFlags & flag) != 0;
}

/// <summary>
/// RAR5 头部解析器：把归档拆成结构化的头部列表。
/// <para>
/// 这个类同时是「对 RAR5 格式的理解」的可执行表达：同一套代码必须既能解析合成归档，也能解析真实 WinRAR 产出的归档，
/// 并且 Header CRC32 全部自洽。CRC 覆盖范围与 headerSize 语义只要有一条理解错，CRC 就永远对不上——
/// 这是比「7-Zip 能打开」更敏感的判据，因为 7-Zip 只会说「无法打开」，不指出错在哪。
/// </para>
/// <para>
/// 只处理明文头部（未启用 -hp 的归档）。头部加密时每个头前面还有 16 字节 IV 与 16 字节对齐的密文，
/// 需要先解密才能解析，不在本类的职责范围内。
/// </para>
/// </summary>
internal static class Rar5HeaderReader
{
    public const int SignatureSize = 8;
    public const int CrcSize = 4;
    public const int VintMaxBytes = 10;

    public const ulong TypeMain = 1;
    public const ulong TypeFile = 2;
    public const ulong TypeService = 3;
    public const ulong TypeArchiveEncryption = 4;
    public const ulong TypeEndOfArchive = 5;

    /// <summary>通用头标志：存在 extra area</summary>
    public const ulong FlagExtraArea = 0x0001;

    /// <summary>通用头标志：存在 data area</summary>
    public const ulong FlagDataArea = 0x0002;

    /// <summary>通用头标志：数据在下一分卷延续</summary>
    public const ulong FlagDataContinuesInNextVolume = 0x0010;

    /// <summary>文件头标志：带 Data CRC32</summary>
    public const ulong FileFlagCrc32 = 0x0004;

    /// <summary>extra 记录类型：文件加密</summary>
    public const ulong ExtraTypeFileEncryption = 0x01;

    /// <summary>extra 记录类型：文件时间</summary>
    public const ulong ExtraTypeFileTime = 0x03;

    /// <summary>解析整个归档的明文头部序列。</summary>
    /// <param name="archive">归档字节</param>
    /// <param name="signatureOffset">签名偏移（SFX 时大于 0）</param>
    public static IReadOnlyList<Rar5Header> Read(ReadOnlySpan<byte> archive, int signatureOffset = 0)
    {
        int position = signatureOffset + SignatureSize;
        var headers = new List<Rar5Header>();

        while (position + CrcSize < archive.Length)
        {
            var header = ReadOne(archive, position);
            headers.Add(header);

            position = header.DataStart + header.DataLength;

            if (header.Type == TypeEndOfArchive)
            {
                break;
            }
        }

        return headers;
    }

    /// <summary>Header CRC32 是否与 <see cref="Read"/> 认定的覆盖范围一致。</summary>
    public static bool CrcMatches(ReadOnlySpan<byte> archive, Rar5Header header) =>
        header.StoredCrc == Crc32.Compute(archive[header.SizeFieldStart..header.BodyEnd]);

    private static Rar5Header ReadOne(ReadOnlySpan<byte> archive, int offset)
    {
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(archive[offset..]);
        int sizeFieldStart = offset + CrcSize;
        int cursor = sizeFieldStart;

        ulong headerSize = ReadVint(archive, ref cursor);
        int bodyStart = cursor;

        ulong type = ReadVint(archive, ref cursor);
        ulong flags = ReadVint(archive, ref cursor);

        ulong extraSize = 0;
        if ((flags & FlagExtraArea) != 0)
        {
            extraSize = ReadVint(archive, ref cursor);
        }

        ulong dataSize = 0;
        if ((flags & FlagDataArea) != 0)
        {
            dataSize = ReadVint(archive, ref cursor);
        }

        int bodyEnd = bodyStart + checked((int)headerSize);
        if (bodyEnd > archive.Length)
        {
            throw new InvalidDataException($"头部 @0x{offset:X4} 的 headerSize={headerSize} 越界（文件长度 {archive.Length}）");
        }

        int dataStart = bodyEnd;
        if (dataStart + (long)dataSize > archive.Length)
        {
            throw new InvalidDataException($"头部 @0x{offset:X4} 的 dataSize={dataSize} 越界（文件长度 {archive.Length}）");
        }

        // 类型专有字段从 flags（以及可选的 extraSize/dataSize）之后开始
        int fieldCursor = cursor;
        ulong archiveFlags = 0;
        ulong? volumeNumber = null;
        ulong fileFlags = 0;
        ulong unpackedSize = 0;
        ulong attributes = 0;
        uint? dataCrc = null;
        ulong compressionInfo = 0;
        ulong hostOs = 0;
        string name = string.Empty;
        ulong encryptionVersion = 0;
        ulong encryptionFlags = 0;
        ulong endFlags = 0;

        switch (type)
        {
            case TypeMain:
                archiveFlags = ReadVint(archive, ref fieldCursor);
                if ((archiveFlags & 0x0002) != 0)
                {
                    volumeNumber = ReadVint(archive, ref fieldCursor);
                }

                break;

            case TypeFile:
            case TypeService:
                fileFlags = ReadVint(archive, ref fieldCursor);
                unpackedSize = ReadVint(archive, ref fieldCursor);
                attributes = ReadVint(archive, ref fieldCursor);

                if ((fileFlags & 0x0002) != 0)
                {
                    fieldCursor += 4; // Unix mtime
                }

                if ((fileFlags & FileFlagCrc32) != 0)
                {
                    dataCrc = BinaryPrimitives.ReadUInt32LittleEndian(archive[fieldCursor..]);
                    fieldCursor += 4;
                }

                compressionInfo = ReadVint(archive, ref fieldCursor);
                hostOs = ReadVint(archive, ref fieldCursor);
                int nameLength = checked((int)ReadVint(archive, ref fieldCursor));
                name = Encoding.UTF8.GetString(archive[fieldCursor..(fieldCursor + nameLength)]);
                break;

            case TypeArchiveEncryption:
                encryptionVersion = ReadVint(archive, ref fieldCursor);
                encryptionFlags = ReadVint(archive, ref fieldCursor);
                break;

            case TypeEndOfArchive:
                endFlags = ReadVint(archive, ref fieldCursor);
                break;

            default:
                // 未知类型：按通用头处理，extra area 仍然可解析
                break;
        }

        int extraStart = bodyEnd - checked((int)extraSize);
        if (extraStart < bodyStart)
        {
            throw new InvalidDataException($"头部 @0x{offset:X4} 的 extraSize={extraSize} 超出 headerSize={headerSize}");
        }

        var (extraTypes, fileEncryption) = ParseExtra(archive, extraStart, bodyEnd);

        return new Rar5Header
        {
            Type = type,
            Flags = flags,
            HeaderSize = headerSize,
            StoredCrc = storedCrc,
            Offset = offset,
            SizeFieldStart = sizeFieldStart,
            BodyStart = bodyStart,
            BodyEnd = bodyEnd,
            DataStart = dataStart,
            DataLength = checked((int)dataSize),
            ExtraSize = extraSize,
            ExtraTypes = extraTypes,
            ArchiveFlags = archiveFlags,
            VolumeNumber = volumeNumber,
            FileFlags = fileFlags,
            UnpackedSize = unpackedSize,
            Attributes = attributes,
            DataCrc = dataCrc,
            CompressionInfo = compressionInfo,
            HostOs = hostOs,
            Name = name,
            FileEncryption = fileEncryption,
            EncryptionVersion = encryptionVersion,
            EncryptionFlags = encryptionFlags,
            EndFlags = endFlags,
        };
    }

    /// <summary>
    /// 解析 extra area：每条记录 = size(vint) + type(vint) + 数据，其中 <c>size</c> 从 <b>type 字段</b>起算、
    /// 覆盖到记录末尾（<b>不含</b> size 字段自身——与头部 headerSize 的语义一致）。
    /// 只解开文件加密记录的字段，其余记录仅记录类型（用于对照「真实归档里有而合成归档里没有」的记录）。
    /// </summary>
    private static (IReadOnlyList<ulong> Types, Rar5EncryptionRecord? Encryption) ParseExtra(ReadOnlySpan<byte> archive, int start, int end)
    {
        var types = new List<ulong>();
        Rar5EncryptionRecord? encryption = null;

        int cursor = start;
        while (cursor < end)
        {
            ulong recordSize = ReadVint(archive, ref cursor);
            int recordContentStart = cursor;
            int recordEnd = recordContentStart + checked((int)recordSize);
            if (recordEnd > end)
            {
                throw new InvalidDataException($"extra 记录 @0x{recordContentStart:X4} 的 size={recordSize} 越界（extra 末偏移 0x{end:X4}）");
            }

            ulong recordType = ReadVint(archive, ref cursor);
            types.Add(recordType);

            if (recordType == ExtraTypeFileEncryption && encryption is null)
            {
                ulong version = ReadVint(archive, ref cursor);
                ulong recordFlags = ReadVint(archive, ref cursor);
                int kdfCount = archive[cursor];
                cursor++;

                byte[] salt = archive.Slice(cursor, 16).ToArray();
                cursor += 16;
                byte[] iv = archive.Slice(cursor, 16).ToArray();
                cursor += 16;

                byte[]? checkValue = null;
                if ((recordFlags & Rar5EncryptionRecord.FlagPasswordCheck) != 0)
                {
                    checkValue = archive.Slice(cursor, 12).ToArray();
                }

                encryption = new Rar5EncryptionRecord(version, recordFlags, kdfCount, salt, iv, checkValue);
            }

            cursor = recordEnd;
        }

        return (types, encryption);
    }

    /// <summary>RAR5 变长整数：每字节低 7 位是数据，最高位表示后续还有字节</summary>
    public static ulong ReadVint(ReadOnlySpan<byte> buffer, ref int position)
    {
        ulong value = 0;
        int shift = 0;

        for (int i = 0; i < VintMaxBytes; i++)
        {
            byte current = buffer[position++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException($"变长整数过长（起始偏移 {position - VintMaxBytes}）");
    }
}
