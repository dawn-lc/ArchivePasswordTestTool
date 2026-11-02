using System.Text;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 内容签名表的覆盖与正确性。
/// <para>
/// 这些断言解决两个问题：
/// <list type="number">
/// <item><b>表不齐全</b>：归档内文件扩展名不在表里时，快速判错直接关闭，退回完整解密
/// （大文件上意味着几十小时）。所以「承诺支持的格式」必须有测试逐项盯着。</item>
/// <item><b>魔数写错</b>：写错一个字节不会有任何编译错误，只会在真实归档上静默漏掉
/// 正确答案——与用户报告的「密码明明在字典里却找不到」完全同形。因此每个格式都构造
/// 一个真实首块并断言能被识别。</item>
/// </list>
/// </para>
/// <para>
/// 分组依据是「兜底强度」：兜底表只收录魔数 ≥ 3 字节的格式（见产品代码里的
/// <c>UniversalMinimumMagicLength</c>），2 字节魔数的格式只能靠扩展名精确匹配。
/// </para>
/// </summary>
public sealed class ContentSignatureTableTests
{
    /// <summary>填充字节。选 0x5A 是为了不与任何魔数片段重合。</summary>
    private const byte Filler = 0x5A;

    // ---------- 扩展名能解析出签名 ----------

    /// <summary>
    /// 常见归档内格式的扩展名必须都能解析出签名。
    /// 缺任何一个都意味着该格式的压缩包无法启用快速判错。
    /// </summary>
    [Theory]
    [InlineData(".7z")]
    [InlineData(".ape")]
    [InlineData(".aif")]
    [InlineData(".aiff")]
    [InlineData(".apk")]
    [InlineData(".asf")]
    [InlineData(".avi")]
    [InlineData(".bmp")]
    [InlineData(".bz2")]
    [InlineData(".cab")]
    [InlineData(".chm")]
    [InlineData(".class")]
    [InlineData(".db")]
    [InlineData(".dds")]
    [InlineData(".dex")]
    [InlineData(".djvu")]
    [InlineData(".doc")]
    [InlineData(".docx")]
    [InlineData(".dll")]
    [InlineData(".elf")]
    [InlineData(".epub")]
    [InlineData(".exe")]
    [InlineData(".fbx")]
    [InlineData(".flac")]
    [InlineData(".flv")]
    [InlineData(".gif")]
    [InlineData(".gz")]
    [InlineData(".jpeg")]
    [InlineData(".jpg")]
    [InlineData(".lz4")]
    [InlineData(".m4a")]
    [InlineData(".mid")]
    [InlineData(".mkv")]
    [InlineData(".mov")]
    [InlineData(".mp3")]
    [InlineData(".mp4")]
    [InlineData(".mpg")]
    [InlineData(".msi")]
    [InlineData(".ogg")]
    [InlineData(".opus")]
    [InlineData(".otf")]
    [InlineData(".pdf")]
    [InlineData(".png")]
    [InlineData(".psd")]
    [InlineData(".rar")]
    [InlineData(".rm")]
    [InlineData(".rmvb")]
    [InlineData(".rtf")]
    [InlineData(".so")]
    [InlineData(".sqlite")]
    [InlineData(".swf")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    [InlineData(".ttc")]
    [InlineData(".unity3d")]
    [InlineData(".vpk")]
    [InlineData(".wav")]
    [InlineData(".webm")]
    [InlineData(".webp")]
    [InlineData(".wim")]
    [InlineData(".wma")]
    [InlineData(".woff")]
    [InlineData(".woff2")]
    [InlineData(".wv")]
    [InlineData(".xlsx")]
    [InlineData(".xz")]
    [InlineData(".zip")]
    [InlineData(".zst")]
    public void CommonExtensionResolvesToSignature(string extension)
    {
        Assert.NotNull(ContentSignature.ForFileName("Sample" + extension));
    }

    /// <summary>
    /// 明确不支持的格式：无魔数，或魔数在首块之外。返回 null 让调用方退回完整测密。
    /// <para>
    /// 后三种是**结构性限制**，不是遗漏：<c>.ts</c> 靠 0x47 同步字节、需看 188 字节后的
    /// 第二个同步字节；<c>.tar</c> 的 <c>ustar</c> 在偏移 257；<c>.iso</c> 的 <c>CD001</c>
    /// 在偏移 0x8001。当前探测只解密首 16 字节，无法覆盖。
    /// </para>
    /// <para>
    /// <c>.ts</c> 即便读到 188 字节也只有 2 字节等效强度（2⁻¹⁶），2.5M 候选会有几十次误报，
    /// 不适合做筛选依据；<c>.tar</c> / <c>.iso</c> 则需要配套的头部校验逻辑（校验和 / 卷描述符）。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(".bin")]
    [InlineData(".dat")]
    [InlineData(".pak")]
    [InlineData(".txt")]
    [InlineData(".unknown")]
    [InlineData(".iso")]   // CD001 在偏移 0x8001
    [InlineData(".tar")]   // ustar 在偏移 257
    [InlineData(".ts")]    // 0x47 同步字节，强度不足
    [InlineData("")]
    public void UnsupportedExtensionReturnsNull(string extension)
    {
        Assert.Null(ContentSignature.ForFileName("Sample" + extension));
    }

    // ---------- 魔数真的能被识别（兜底表） ----------

    /// <summary>
    /// 偏移 0 的 ASCII 魔数。写错任何一个字节这里就会红。
    /// </summary>
    [Theory]
    [InlineData("fLaC")]         // FLAC
    [InlineData("OggS")]         // Ogg（Vorbis / Opus / Theora）
    [InlineData("wvpk")]         // WavPack
    [InlineData("MAC ")]         // Monkey's Audio
    [InlineData("MThd")]         // MIDI
    [InlineData(".RMF")]         // RealMedia
    [InlineData("8BPS")]         // Photoshop
    [InlineData("DDS ")]         // DirectDraw Surface
    [InlineData("AT&TFORM")]     // DjVu
    [InlineData("{\\rtf")]       // RTF
    [InlineData("ITSF")]         // CHM
    [InlineData("FWS")]          // SWF（未压缩）
    [InlineData("CWS")]          // SWF（zlib）
    [InlineData("ZWS")]          // SWF（LZMA）
    [InlineData("wOFF")]         // WOFF
    [InlineData("wOF2")]         // WOFF2
    [InlineData("OTTO")]         // OpenType
    [InlineData("ttcf")]         // TrueType Collection
    [InlineData("Kaydara FBX B")] // FBX 二进制
    [InlineData("dex\n")]        // Android dex
    [InlineData("MSWIM\0\0\0")]  // WIM / ESD / SWM
    [InlineData("UnityFS")]      // Unity 资源包
    [InlineData("Rar!")]         // RAR
    [InlineData("%PDF")]         // PDF
    [InlineData("GIF8")]         // GIF
    [InlineData("BZh")]          // bzip2
    [InlineData("SQLite format 3\0")] // SQLite
    public void AsciiMagicAtOffsetZeroIsRecognized(string magic)
    {
        // SQLite 的魔数有 16 字节，块要够长
        byte[] block = Block(Math.Max(16, magic.Length));
        Encoding.ASCII.GetBytes(magic).CopyTo(block, 0);

        Assert.True(
            ContentSignature.MatchesAnyKnownContainer(block),
            $"兜底表未识别魔数「{magic}」——要么表里缺这个格式，要么魔数写错了");
    }

    /// <summary>偏移 0 的二进制魔数</summary>
    [Theory]
    [InlineData("1A45DFA3")]         // Matroska / WebM
    [InlineData("FFD8FF")]           // JPEG
    [InlineData("89504E470D0A1A0A")]  // PNG（8 字节，少一个字节就不该命中）
    [InlineData("7F454C46")]         // ELF
    [InlineData("CAFEBABE")]         // Java class
    [InlineData("49492A00")]         // TIFF（Intel 字节序）
    [InlineData("4D4D002A")]         // TIFF（Motorola 字节序）
    [InlineData("3412AA55")]         // VPK（Source 引擎）
    [InlineData("04224D18")]         // LZ4
    [InlineData("377ABCAF271C")]     // 7z
    [InlineData("3026B2758E66CF11")] // ASF / WMV / WMA
    [InlineData("D0CF11E0A1B11AE1")] // OLE 复合文档
    [InlineData("504B0304")]         // ZIP
    [InlineData("000001BA")]         // MPEG-PS
    public void BinaryMagicAtOffsetZeroIsRecognized(string hex)
    {
        byte[] block = Block(16);
        Convert.FromHexString(hex).CopyTo(block, 0);

        Assert.True(
            ContentSignature.MatchesAnyKnownContainer(block),
            $"兜底表未识别魔数 {hex}");
    }

    /// <summary>类型名在偏移 8 的容器（RIFF / IFF 家族）</summary>
    [Theory]
    [InlineData("FORM", "AIFF")]
    [InlineData("FORM", "AIFC")]
    [InlineData("RIFF", "AVI ")]
    [InlineData("RIFF", "WAVE")]
    [InlineData("RIFF", "WEBP")]
    public void ContainerTypeAtOffsetEightIsRecognized(string container, string type)
    {
        byte[] block = Block(16);
        Encoding.ASCII.GetBytes(container).CopyTo(block, 0);
        Encoding.ASCII.GetBytes(type).CopyTo(block, 8);

        Assert.True(ContentSignature.MatchesAnyKnownContainer(block));
    }

    /// <summary>MP4 / ISO BMFF：偏移 4 的盒类型，且偏移 0 的盒长度必须合理</summary>
    [Theory]
    [InlineData("ftyp")]
    [InlineData("moov")]
    [InlineData("mdat")]
    [InlineData("free")]
    [InlineData("skip")]
    [InlineData("wide")]
    [InlineData("styp")]
    public void Mp4BoxTypeIsRecognized(string boxType)
    {
        byte[] block = Block(16);
        block[0] = 0;
        block[1] = 0;
        block[2] = 0;
        block[3] = 0x18; // 盒长度 24，满足「必须是 1 或 ≥ 8」
        Encoding.ASCII.GetBytes(boxType).CopyTo(block, 4);

        Assert.True(ContentSignature.MatchesAnyKnownContainer(block));
    }

    /// <summary>盒长度不合理时不该被当成 MP4（这是 MP4 签名唯一的额外强度来源）</summary>
    [Theory]
    [InlineData(0x00000002u)]
    [InlineData(0x00000007u)]
    public void Mp4ImplausibleBoxSizeIsRejected(uint boxSize)
    {
        byte[] block = Block(16);
        block[0] = (byte)(boxSize >> 24);
        block[1] = (byte)(boxSize >> 16);
        block[2] = (byte)(boxSize >> 8);
        block[3] = (byte)boxSize;
        Encoding.ASCII.GetBytes("ftyp").CopyTo(block, 4);

        Assert.False(ContentSignature.MatchesAnyKnownContainer(block));
    }

    // ---------- 弱魔数必须留在兜底表之外 ----------

    /// <summary>
    /// 2 字节魔数随机命中率 2⁻¹⁶：250 万候选会有上百次误报，足以把候选上限打满。
    /// 它们只能通过扩展名精确匹配，绝不能进兜底表。
    /// </summary>
    [Theory]
    [InlineData(".exe", "MZ")]
    [InlineData(".bmp", "BM")]
    [InlineData(".gz", new byte[] { 0x1F, 0x8B })]
    [InlineData(".mp3", new byte[] { 0xFF, 0xFB })]   // 最弱分支是 2 字节帧同步，整体按 2 字节计
    public void WeakMagicIsExcludedFromFallback(string extension, object magic)
    {
        byte[] bytes = magic switch
        {
            string ascii => Encoding.ASCII.GetBytes(ascii),
            byte[] raw => raw,
            _ => throw new InvalidOperationException("不支持的参数类型"),
        };

        byte[] block = Block(16);
        bytes.CopyTo(block, 0);

        var signature = ContentSignature.ForFileName("Sample" + extension);

        Assert.NotNull(signature);
        Assert.True(signature!.Matches(block), "扩展名精确匹配应当命中");
        Assert.False(ContentSignature.MatchesAnyKnownContainer(block), "弱魔数不得进入兜底表");
    }

    // ---------- 辅助 ----------

    private static byte[] Block(int length)
    {
        byte[] block = new byte[length];
        Array.Fill(block, Filler);
        return block;
    }
}
