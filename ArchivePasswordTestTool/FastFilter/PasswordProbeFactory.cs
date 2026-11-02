namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 快速判错器工厂：按归档格式选择可用的实现。
    /// <para>
    /// 三种格式的共同点是「没有可靠的密码校验值 + 存储模式让首块就是明文」：
    /// <list type="bullet">
    /// <item>7z：加口头没有校验值，Copy 编码的密文也没有结构，错密码必须解到文件尾靠 CRC 判错</item>
    /// <item>RAR4：没有任何校验字段，错误密码必须解完整个文件靠 CRC 判错</item>
    /// <item>ZipCrypto：只有 1 字节校验（误判率 1/256），每个假阳性都要完整解压</item>
    /// </list>
    /// 都能通过「解首块 + 比对文件格式签名」把误判率压到约 2^-32。
    /// </para>
    /// <para>
    /// 不适用时返回 null，调用方退回完整测密——快速路径永远是可选加速，不是必需依赖。
    /// </para>
    /// </summary>
    internal static class PasswordProbeFactory
    {
        public static IPasswordProbe? TryCreate(string archivePath, ArchiveInfo info)
        {
            // 探测都只读文件头部（最多几 KB）；顺序尝试，先匹配的返回。
            // 格式不匹配的实现会在第一次 Guid 比较时就返回，开销可忽略
            return (IPasswordProbe?)SevenZipStoreProbe.TryCreate(archivePath, info)
                ?? (IPasswordProbe?)Rar4StoreProbe.TryCreate(archivePath, info)
                ?? ZipStoreProbe.TryCreate(archivePath, info);
        }
    }
}
