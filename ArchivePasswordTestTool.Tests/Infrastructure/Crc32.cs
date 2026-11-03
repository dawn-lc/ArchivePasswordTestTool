namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 标准 CRC-32（多项式 0xEDB88320，反射算法），供合成归档时计算头部与数据校验。
/// <para>
/// RAR4 的 HEAD_CRC 取本算法的低 16 位，RAR5 的 Header CRC32 / Data CRC32 直接用完整 32 位。
/// </para>
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
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

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}
