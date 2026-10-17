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

        public static readonly Dictionary<string, Guid> NameToGuid = GuidToName.ToDictionary(k => k.Value, v => v.Key);
    }
    /// <summary>
    /// 7-Zip SDK 的 GUID 定义
    /// </summary>
    internal static class SevenZipGuids
    {
        // 7z 格式的 CLSID
        public static Guid CLSID_CFormat7z = new("23170F69-40C1-278A-1000-000110070000");
        public static Guid CLSID_CFormatZip = new("23170F69-40C1-278A-1000-000110010000");
        public static Guid CLSID_CFormatRar = new("23170F69-40C1-278A-1000-000110030000");
        public static Guid CLSID_CFormatRar5 = new("23170F69-40C1-278A-1000-000110CC0000");
        // 接口 GUID
        public static Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        public static Guid IID_IInArchive = new("23170F69-40C1-278A-0000-000600600000");
        public static Guid IID_IOutArchive = new("23170F69-40C1-278A-0000-000600A00000");
        public static Guid IID_ISetProperties = new("23170F69-40C1-278A-0000-000600030000");
    }
}
