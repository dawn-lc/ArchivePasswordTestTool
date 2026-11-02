namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 压缩包信息（探测结果的纯数据载体）。
    /// <para>
    /// 由 <see cref="ArchiveInspector"/> 打开归档时填充一次，之后在多线程间**只读**共享
    /// （<c>Planner</c> 的上下文池、<c>Runner</c> 的并行循环、各快筛探测器都读同一份）。
    /// 因此任何代码都不该在探测之后改写它——那会让后续迭代读到与归档不符的判定结果。
    /// </para>
    /// </summary>
    public class ArchiveInfo
    {
        /// <summary>压缩包格式对应的 GUID</summary>
        public Guid Type { get; set; } = Guid.Empty;

        /// <summary>GUID 对应的压缩包格式名</summary>
        public string Format =>
            SevenZipFormats.GuidToName.TryGetValue(Type, out string? name) ? name : Type.ToString();

        /// <summary>文件数量</summary>
        public uint FileCount { get; set; }

        /// <summary>
        /// 是否走快速测试路径：非全量加密 且 存在压缩后大于 32 字节的加密文件
        /// （密码校验仅针对最小加密文件，避开逐密码全量提取）
        /// </summary>
        public bool SupportsQuickTest { get; set; }

        /// <summary>是否加密头部</summary>
        public bool IsHeaderEncryption { get; set; }

        /// <summary>
        /// 测试一个错误密码时，实际会被解码的数据量（字节）。
        /// <para>
        /// 有快速测试目标时等于该文件的大小，否则等于全部加密数据之和。
        /// 只在 <see cref="FailureRequiresFullDecode"/> 为 true 时代表真实代价。
        /// </para>
        /// </summary>
        public ulong FailurePathBytes { get; set; }

        /// <summary>
        /// 错误密码是否必须解完整个数据才能判错。
        /// <para>
        /// 为 true 表示参与测试的数据使用了「不压缩」编码（7z Copy / zip Store），
        /// 密文没有任何可作为判据的内部结构，只能解到文件尾靠 CRC 判错——
        /// 代价与文件大小成正比，增加并行度也不会改善（瓶颈是内存带宽/IO）。
        /// </para>
        /// <para>
        /// 压缩编码（LZMA/LZMA2/BZip2/PPMd）不受影响：错误密钥解出的首块就是非法
        /// 压缩流，几 KB 内即可判错。
        /// </para>
        /// </summary>
        public bool FailureRequiresFullDecode { get; set; }

        /// <summary>
        /// 快速测试目标文件索引（最小的、压缩后大于 32 字节的加密文件）。
        /// 打开归档时算好一次，每个密码测试直接复用同一个数组，避免逐密码重复分配。
        /// 为空表示没有合格的快速测试目标（此时走全量测试）。
        /// </summary>
        public uint[]? QuickTestFileIndices { get; set; }

        /// <summary>压缩包是否加密</summary>
        public bool IsEncrypted { get; set; }

        /// <summary>全部条目的原始（解压后）大小之和。header 加密时无法读取，为 0</summary>
        public ulong UnpackedSize { get; set; }

        /// <summary>全部条目的压缩后大小之和。header 加密时无法读取，为 0</summary>
        public ulong PackedSize { get; set; }

        /// <summary>
        /// 归档用到的编码名（如 <c>Copy</c> / <c>LZMA2:24</c> / <c>Store</c>），去重后按字典序拼接。
        /// <para>
        /// 这是判断「会不会慢」最直接的依据：<c>Copy</c>/<c>Store</c> 的密文没有可自校验结构，
        /// 错误密码必须解完整个数据才能判错；LZMA/PPMd 等首块即失败。
        /// </para>
        /// 为空表示读不到（header 加密或无条目）。
        /// </summary>
        public string? Method { get; set; }

        /// <summary>快速测试目标的原始（解压后）大小；<see cref="QuickTestFileIndices"/> 为空时无意义</summary>
        public ulong QuickTestUnpackedSize { get; set; }

        /// <summary>快速测试目标的条目名（用于在信息面板里指明「实际测的是哪个文件」）</summary>
        public string? QuickTestEntryName { get; set; }

        /// <summary>是否固实压缩（solid）：所有文件共享一个压缩流，单个文件无法独立解出</summary>
        public bool IsSolid { get; set; }

        /// <summary>压缩数据在文件中的起始偏移。普通包为 0，自解压（SFX）为 PE stub 之后的偏移</summary>
        public long SignatureOffset { get; set; }

        /// <summary>分卷数量。单文件包为 1</summary>
        public int VolumeCount { get; set; } = 1;

        /// <summary>全部分卷在磁盘上的字节数之和（SFX 时含 stub）</summary>
        public long DiskSize { get; set; }
    }
}
