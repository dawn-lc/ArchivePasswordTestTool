using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 从已打开的 7-Zip 归档里读出 <see cref="ArchiveInfo"/> 的全部判定字段。
    /// <para>
    /// 与 <see cref="ArchiveContext"/> 分开：那边只管 COM 对象的生命周期（打开/关闭/复用），
    /// 这边只管「读哪些属性、怎么从属性推结论」。读属性是唯一会碰到 COM 的地方，
    /// 结论本身（是否 header 加密、错密码要不要解全量）是纯逻辑，两者混在一起时
    /// 改动判定规则要去翻一个文件名里写着 Context 的类。
    /// </para>
    /// </summary>
    internal static class ArchiveInspector
    {
        /// <summary>
        /// 枚举全部条目并填充 <paramref name="info"/>。
        /// 只在归档刚打开、且 <paramref name="info"/> 尚未与任何线程共享时调用一次。
        /// </summary>
        public static void Populate(IInArchive archive, ArchiveInfo info)
        {
            _ = archive.GetNumberOfItems(out uint fileCount);
            info.FileCount = fileCount;

            if (fileCount == 0)
            {
                info.IsEncrypted = true;
                info.IsHeaderEncryption = true;
                info.SupportsQuickTest = false;
                return;
            }

            bool foundEncrypted = false;
            bool allEncrypted = true;
            bool allEncryptedStored = true;
            ulong totalEncryptedPacked = 0;
            ulong unpackedTotal = 0;
            ulong packedTotal = 0;
            var methods = new SortedSet<string>(StringComparer.Ordinal);

            // 最小可测加密文件（压缩后 > 32 字节）。
            // foundQuickTarget 既是「已找到」哨兵也是有效性判据：不能单靠 size 初值，
            // 否则没有合格目标时 quickTestIndex 会停在 0，一旦 0 号文件未加密，
            // 任何错误密码都会被误判为正确（假阳性）——已实测复现并修复。
            bool foundQuickTarget = false;
            uint quickTestIndex = 0;
            ulong quickTestSize = 0;
            ulong quickTestUnpacked = 0;
            bool quickTargetStored = false;

            for (uint i = 0; i < fileCount; i++)
            {
                // 每个文件只读一次编码属性：下面三处判断都要用
                string? method = ReadMethod(archive, i);

                // 7-Zip 的 Method 是「编码链」，形如 `LZMA2:16 7zAES:19`：
                // 加密层不算「压缩方式」，混进来只会让这一行难以阅读（界面另有「加密状态」）
                foreach (string coder in SplitCoders(method))
                {
                    if (!IsEncryptionCoder(coder))
                    {
                        _ = methods.Add(coder);
                    }
                }

                PropVariant enc = new();
                try
                {
                    _ = archive.GetProperty(i, ItemPropId.Encrypted, ref enc);
                    bool encrypted = enc.VarType == VarEnum.VT_BOOL && enc.Bool;

                    // 大小与时间与是否加密无关，一律统计：信息面板要能告诉用户
                    // 「这个包多大、解开多大」，而不是只有一个文件数量
                    ulong itemPacked = ReadUInt64(archive, i, ItemPropId.PackedSize);
                    ulong itemSize = ReadUInt64(archive, i, ItemPropId.Size);
                    packedTotal += itemPacked;
                    unpackedTotal += itemSize;

                    info.IsSolid |= ReadBool(archive, i, ItemPropId.Solid);

                    if (encrypted)
                    {
                        foundEncrypted = true;
                        totalEncryptedPacked += itemPacked;

                        bool stored = IsStored(method);
                        if (itemPacked > 32 && (!foundQuickTarget || itemPacked < quickTestSize))
                        {
                            foundQuickTarget = true;
                            quickTestIndex = i;
                            quickTestSize = itemPacked;
                            quickTestUnpacked = itemSize;
                            // kpidName 是归档内的相对路径全名，正是用户能对照的名字
                            info.QuickTestEntryName = ReadString(archive, i, ItemPropId.Name);

                            // 只对"最终会被测的那个文件"关心编码：
                            // Copy(不压缩) 编码在密文上没有任何可校验结构
                            quickTargetStored = stored;
                        }

                        if (!stored)
                        {
                            allEncryptedStored = false;
                        }
                    }
                    else
                    {
                        allEncrypted = false;
                    }
                }
                finally
                {
                    enc.Clear();
                }
            }

            info.IsEncrypted = foundEncrypted;
            info.UnpackedSize = unpackedTotal;
            info.PackedSize = packedTotal;
            info.Method = methods.Count > 0 ? string.Join(", ", methods) : null;
            info.QuickTestUnpackedSize = quickTestUnpacked;

            // 判定 header 是否加密：7-Zip 在 header 加密时会通过
            // GetArchiveProperty(kpidEncrypted) 返回 VT_BOOL true；
            // zip 等其余格式无此 archive 级属性（VT_EMPTY）。
            // （原实现 fileCount < 1 && allEncrypted 是错的：正常包 fileCount>=1 恒为 false）
            bool headerEncrypted = false;
            PropVariant hdr = new();
            try
            {
                _ = archive.GetArchiveProperty((uint)ItemPropId.Encrypted, ref hdr);
                headerEncrypted = hdr.VarType == VarEnum.VT_BOOL && hdr.Bool;
            }
            finally
            {
                hdr.Clear();
            }
            info.IsHeaderEncryption = headerEncrypted;

            // 仅当存在合格（压缩后 >32 字节）的加密文件时才建立快速测试目标：
            // 条件不成立时若还无条件赋值，minIndex 会停留在初值 0，
            // 一旦 0 号文件恰好未加密，密码测试将打到未加密文件上，
            // 任何错误密码都会被误判为正确（假阳性）——已实测复现并修复。
            // 索引数组一次算好挂在 Info 上，供每个密码测试复用（原先每个密码都新建数组）
            if (foundEncrypted && foundQuickTarget)
            {
                info.QuickTestFileIndices = [quickTestIndex];
            }
            info.SupportsQuickTest = !allEncrypted && foundQuickTarget;

            // 判错代价诊断（仅用于预警，不参与判定逻辑）：
            // 实际会解码的数据量 = 快速测试目标的大小，或（无合格目标时）全部加密数据之和
            info.FailurePathBytes = foundQuickTarget ? quickTestSize : totalEncryptedPacked;

            // "每个错误密码都必须解完整个数据"只发生在编码器无法自校验时：
            // 7z 的 Copy(store) 编码不产生任何结构，错误密钥解出的垃圾数据与正常数据
            // 无法区分，只能解到文件尾靠 CRC 判错。反之 LZMA/LZMA2/BZip2/PPMd 的压缩流
            // 有内部结构（range coder 首字节、块头校验），首块即失败（实测：200MB 随机
            // 数据用 LZMA2 只读 1MB 就判错，Copy 则读满 126MB）。
            info.FailureRequiresFullDecode = foundQuickTarget ? quickTargetStored : allEncryptedStored;
        }

        /// <summary>
        /// 把 7-Zip 的 Method 属性拆成单个编码名。该属性是空格分隔的编码链
        /// （<c>LZMA2:16 7zAES:19</c>、<c>Copy 7zAES:19</c>），展示与判据都应按单段处理。
        /// </summary>
        private static string[] SplitCoders(string? method) =>
            string.IsNullOrWhiteSpace(method)
                ? []
                : method.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// 该编码名是否只是加密层（7zAES / RarAes / Rar5AES / ZipCrypto / AES-256-CBC…）。
        /// 7-Zip 的 Method 属性是「编码链」，加密与压缩混在同一个字符串里，
        /// 展示压缩方式时需要把加密段滤掉。
        /// </summary>
        private static bool IsEncryptionCoder(string method) =>
            method.Contains("AES", StringComparison.OrdinalIgnoreCase)
            || method.Contains("Crypto", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 该条目是否使用"不压缩"编码（7z 的 Copy / zip 的 Store）。
        /// 编码名读不到时返回 false —— 拿不到编码就不要声称它慢，宁可漏报也不误报。
        /// </summary>
        private static bool IsStored(string? method) =>
            method is not null
            && (method.StartsWith("Copy", StringComparison.Ordinal)
                || method.StartsWith("Store", StringComparison.Ordinal));

        /// <summary>读字符串属性（如 <see cref="ItemPropId.Method"/> / <see cref="ItemPropId.Name"/>）</summary>
        private static string? ReadString(IInArchive archive, uint index, ItemPropId property)
        {
            PropVariant value = new();
            try
            {
                return archive.GetProperty(index, property, ref value) == 0 ? value.Bstr : null;
            }
            finally
            {
                // BSTR 已在 Bstr getter 里复制成托管字符串，此处释放原生内存
                value.Clear();
            }
        }

        /// <summary>读数值属性；非数值类型或读取失败时返回 0</summary>
        private static ulong ReadUInt64(IInArchive archive, uint index, ItemPropId property)
        {
            PropVariant value = new();
            try
            {
                return archive.GetProperty(index, property, ref value) == 0 ? value.UInt64 : 0;
            }
            finally
            {
                value.Clear();
            }
        }

        /// <summary>读布尔属性；缺失时返回 false</summary>
        private static bool ReadBool(IInArchive archive, uint index, ItemPropId property)
        {
            PropVariant value = new();
            try
            {
                return archive.GetProperty(index, property, ref value) == 0 && value.Bool;
            }
            finally
            {
                value.Clear();
            }
        }

        /// <summary>读编码名（<see cref="ItemPropId.Method"/>），供 <see cref="IsStored"/> 与展示共用</summary>
        private static string? ReadMethod(IInArchive archive, uint index) =>
            ReadString(archive, index, ItemPropId.Method);
    }
}
