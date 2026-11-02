using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    internal static class SevenZipFormats
    {
        public static readonly Dictionary<Guid, string> GuidToName = new()
        {
            { SevenZipGuids.CLSID_CFormat7z, "7z" },
            { SevenZipGuids.CLSID_CFormatZip, "zip" },
            { SevenZipGuids.CLSID_CFormatRar, "rar" },
            { SevenZipGuids.CLSID_CFormatRar5, "rar5" }
        };
    }

    /// <summary>
    /// 7-Zip SDK 的 GUID 定义
    /// </summary>
    internal static class SevenZipGuids
    {
        // 格式 CLSID（7z 官方规则：{23170F69-40C1-278A-1000-000110xx0000}）
        public static readonly Guid CLSID_CFormat7z = new("23170F69-40C1-278A-1000-000110070000");
        public static readonly Guid CLSID_CFormatZip = new("23170F69-40C1-278A-1000-000110010000");
        public static readonly Guid CLSID_CFormatRar = new("23170F69-40C1-278A-1000-000110030000");
        public static readonly Guid CLSID_CFormatRar5 = new("23170F69-40C1-278A-1000-000110CC0000");

        // 接口 GUID（7z 官方规则：{23170F69-40C1-278A-0000-00yy00xx0000}）
        public static readonly Guid IID_IInArchive = new("23170F69-40C1-278A-0000-000600600000");
    }
}
