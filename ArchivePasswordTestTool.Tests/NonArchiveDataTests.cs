using System.Buffers.Binary;
using System.Diagnostics;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 「非归档数据」的识别与分离：文件里除了归档本身还夹着别的字节时，
/// 程序必须照样测得动，并且能说清楚多余数据在哪、把归档单独拷出来。
/// <para>
/// 三条能力分别对应三类事实，因此断言也分三层：
/// <list type="number">
/// <item><b>定位</b>——签名不在文件头也要找到它（自解压 stub、被拼接的文件头、下载工具的包装头）</item>
/// <item><b>边界</b>——归档到哪结束按格式自身结构算，不靠猜</item>
/// <item><b>分离</b>——拷出来的字节必须与原始归档逐字节相同，且 7-Zip 能完整通过</item>
/// </list>
/// </para>
/// <para>
/// 实测背景：7-Zip 对尾随垃圾本来就宽容（三种格式各追加 16MB 仍完整通过测试），
/// 所以真正卡住的只有前置数据——旧实现只对 <c>MZ</c> 开头的文件扫描签名，其余一律要求签名在偏移 0。
/// </para>
/// </summary>
[Collection(TestDataCollection.Name)]
public sealed class NonArchiveDataTests(TestDataFixture fx, ITestOutputHelper output)
{
    private const string CorrectPassword = TestDataFixture.CorrectPassword;

    /// <summary>杂数据长度。选 64KB：足够跨越扫描分块，又不拖慢用例</summary>
    private const int JunkLength = 64 * 1024;

    /// <summary>
    /// 参与测试的格式：(样本名, 期望 GUID, 扩展名)。
    /// 三种格式的边界判定逻辑完全不同（起始头偏移 / 块链 / 中央目录），必须都覆盖。
    /// </summary>
    public static TheoryData<string, Guid> Formats => new()
    {
        { "Secret.7z", SevenZipGuids.CLSID_CFormat7z },
        { "SecretZip.zip", SevenZipGuids.CLSID_CFormatZip },
        { "Rar5Single.rar", SevenZipGuids.CLSID_CFormatRar5 },
    };

    // ---------- 第一层：定位 ----------

    /// <summary>
    /// 前置了非 MZ 的杂数据后，格式检测必须仍能识别，并报出签名偏移。
    /// 这是旧实现唯一真正挡住的情形（只有 MZ 才扫描）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void PrefixDataIsSkippedAndOffsetIsReported(string sampleName, Guid expectedFormat)
    {
        using var workspace = new Workspace();
        string path = workspace.Compose(fx.Archive(sampleName), prefix: JunkLength, suffix: 0);

        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);

        Assert.Equal(expectedFormat, format);
        Assert.Equal(JunkLength, offset);
    }

    /// <summary>
    /// 签名出现在杂数据之后很远的位置（模拟大体积 stub）也必须找到——
    /// 扫描是分块进行的，这条顺带覆盖「签名跨块边界」的拼接逻辑。
    /// </summary>
    [Fact]
    public void SignatureIsFoundAcrossScanChunkBoundaries()
    {
        using var workspace = new Workspace();

        // 前置长度刻意不是 1MB 的整数倍，让签名落在分块边界附近
        const int prefix = (1024 * 1024) - 3;
        string path = workspace.Compose(fx.Archive("Secret.7z"), prefix, suffix: 0);

        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);

        Assert.Equal(SevenZipGuids.CLSID_CFormat7z, format);
        Assert.Equal(prefix, offset);
    }

    /// <summary>前置数据不影响测密：主程序必须照常找到密码（端到端，语言无关判据）</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void PrefixedArchiveStillFindsPassword(string sampleName, Guid expectedFormat)
    {
        _ = expectedFormat;
        using var workspace = new Workspace();
        string path = workspace.Compose(fx.Archive(sampleName), prefix: JunkLength, suffix: JunkLength);

        var (exitCode, text) = RunProgram($"-D \"{fx.Dict("DictOK.txt")}\" -F \"{path}\"");

        Assert.Equal(0, exitCode);
        Assert.Contains(CorrectPassword, text);
    }

    /// <summary>
    /// 大文件里偶然出现 Zip 本地文件头签名（4 字节）时不能被当成压缩包。
    /// <para>
    /// 这不是假想的：64MB 窗口内随机出现 <c>PK\x03\x04</c> 的概率约 1.5%（64MB / 2^32），
    /// 误判的后果是把一个正常文件报成「压缩包损坏 / 密码错误」，比「格式不支持」更误导。
    /// 因此扫描出的 Zip 候选必须通过「中央目录结束记录自洽」这一结构校验。
    /// </para>
    /// <para>
    /// 只对扫描路径加校验，不校验偏移 0 的头部命中：那是「用户明确说这是压缩包，但文件坏了」的情形，
    /// 报「损坏」比报「格式不支持」更准确。
    /// </para>
    /// </summary>
    [Fact]
    public void IsolatedZipSignatureIsRejectedByStructuralCheck()
    {
        using var workspace = new Workspace();

        const int signatureOffset = 4096;
        byte[] bytes = new byte[JunkLength];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(0x41 + (i % 15));
        }

        // 孤立签名：后面没有本地文件头该有的字段，也没有中央目录
        bytes[signatureOffset] = 0x50;
        bytes[signatureOffset + 1] = 0x4B;
        bytes[signatureOffset + 2] = 0x03;
        bytes[signatureOffset + 3] = 0x04;

        string path = workspace.TargetPath("Fake.zip");
        File.WriteAllBytes(path, bytes);

        var (format, _) = ArchiveFormatDetector.DetectArchiveFormat(path);

        Assert.Equal(Guid.Empty, format);
    }

    // ---------- 第二层：边界 ----------

    /// <summary>
    /// 前后都夹数据时，范围必须精确等于归档本身：起点是签名偏移，终点是归档真实末尾。
    /// 差一个字节就说明对格式结构的理解有偏差（7z 的起始头、RAR 的块链、Zip 的中央目录各算一次）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void ExtentMatchesTheArchiveBytesExactly(string sampleName, Guid expectedFormat)
    {
        using var workspace = new Workspace();
        string archivePath = fx.Archive(sampleName);
        long archiveLength = new FileInfo(archivePath).Length;

        string path = workspace.Compose(archivePath, prefix: JunkLength, suffix: JunkLength);
        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);

        var extent = ArchiveExtentDetector.Detect(path, format, offset);

        Assert.NotNull(extent);
        Assert.Equal(expectedFormat, format);
        Assert.Equal(JunkLength, extent!.Start);
        Assert.Equal(JunkLength + archiveLength, extent.End);
        Assert.Equal(JunkLength, extent.TrailingBytes(JunkLength * 2 + archiveLength));
    }

    /// <summary>
    /// 只有尾随数据时，起点仍是 0，终点就是归档末尾。
    /// 7-Zip 本来就能读这种文件（实测 16MB 尾随也完整通过），这里锁的是「报得出尾随字节数」。
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void TrailingDataIsReportedWithoutShiftingStart(string sampleName, Guid expectedFormat)
    {
        using var workspace = new Workspace();
        string archivePath = fx.Archive(sampleName);
        long archiveLength = new FileInfo(archivePath).Length;

        string path = workspace.Compose(archivePath, prefix: 0, suffix: JunkLength);
        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);

        var extent = ArchiveExtentDetector.Detect(path, format, offset);

        Assert.NotNull(extent);
        Assert.Equal(expectedFormat, format);
        Assert.Equal(0, extent!.Start);
        Assert.Equal(archiveLength, extent.End);
        Assert.Equal(JunkLength, extent.TrailingBytes(archiveLength + JunkLength));
    }

    /// <summary>
    /// Zip 的归档末尾包含「注释」，注释长度写在结束记录里。
    /// 若只按固定的 22 字节结束记录算，带注释的包会少算一截——分离出来的副本就坏了。
    /// </summary>
    [Fact]
    public void ZipArchiveCommentIsIncludedInExtent()
    {
        using var workspace = new Workspace();

        const int commentLength = 37;
        string zipWithComment = workspace.CreateZipWithComment(fx.Archive("SecretZip.zip"), commentLength);
        long archiveLength = new FileInfo(zipWithComment).Length;

        string path = workspace.Compose(zipWithComment, prefix: JunkLength, suffix: JunkLength);
        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);

        var extent = ArchiveExtentDetector.Detect(path, format, offset);

        Assert.Equal(SevenZipGuids.CLSID_CFormatZip, format);
        Assert.NotNull(extent);
        Assert.Equal(JunkLength + archiveLength, extent!.End);
    }

    /// <summary>
    /// 头部加密的 RAR5 无法遍历头部（从第二个头开始全是密文），终点必须报「未知」而不是猜一个数。
    /// 猜错的后果是分离出一个坏文件，比拒绝更糟。
    /// </summary>
    [Fact]
    public void HeaderEncryptedRarExtentReportsUnknownEnd()
    {
        using var workspace = new Workspace();
        string path = workspace.Compose(fx.Archive("Rar5HeaderSplit.part1.rar"), prefix: JunkLength, suffix: 0);

        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);
        var extent = ArchiveExtentDetector.Detect(path, format, offset);

        Assert.Equal(SevenZipGuids.CLSID_CFormatRar5, format);
        Assert.Equal(JunkLength, offset);
        Assert.NotNull(extent);
        Assert.Null(extent!.End);
    }

    // ---------- 第三层：分离 ----------

    /// <summary>
    /// 分离出的文件必须与原始归档**逐字节相同**，并且 7-Zip 能完整通过测试。
    /// 逐字节相同是最强的判据：任何偏移/长度偏差都会立刻显形。
    /// </summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void SeparatedCopyIsByteIdenticalToOriginalArchive(string sampleName, Guid expectedFormat)
    {
        _ = expectedFormat;
        using var workspace = new Workspace();
        string archivePath = fx.Archive(sampleName);

        string path = workspace.Compose(archivePath, prefix: JunkLength, suffix: JunkLength);
        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);
        var extent = ArchiveExtentDetector.Detect(path, format, offset);

        Assert.NotNull(extent);
        Assert.NotNull(extent!.End);

        string target = workspace.TargetPath("Separated" + Path.GetExtension(sampleName));
        ArchiveSeparator.Write(path, extent.Start, extent.End!.Value, target);

        Assert.Equal(File.ReadAllBytes(archivePath), File.ReadAllBytes(target));
        Assert.Equal(0, RunSevenZip($"t -y -p{CorrectPassword} \"{target}\""));
    }

    /// <summary>目标文件名：后缀插在扩展名之前，且不覆盖原文件</summary>
    [Fact]
    public void SeparatedTargetPathKeepsExtension()
    {
        Assert.Equal(
            Path.Combine("C:\\dir", "Secret[clean].7z"),
            ArchiveSeparator.BuildTargetPath("C:\\dir\\Secret.7z"));

        Assert.Equal(
            Path.Combine("C:\\dir", "RarSfx[clean].exe"),
            ArchiveSeparator.BuildTargetPath("C:\\dir\\RarSfx.exe"));
    }

    /// <summary>
    /// 端到端：<c>-SeparateArchive</c> 必须在磁盘上留下干净副本，且内容与归档一致。
    /// 同时锁住「默认不分离」——不加开关时不该凭空多出文件。
    /// </summary>
    [Fact]
    public void SeparateSwitchWritesCleanCopyOnlyWhenRequested()
    {
        using var workspace = new Workspace();
        string archivePath = fx.Archive("Secret.7z");
        string path = workspace.Compose(archivePath, prefix: JunkLength, suffix: JunkLength);

        string expectedCleanPath = ArchiveSeparator.BuildTargetPath(path);

        // 默认：只读，不写文件
        var (defaultExit, _) = RunProgram($"-D \"{fx.Dict("DictOK.txt")}\" -F \"{path}\"");
        Assert.Equal(0, defaultExit);
        Assert.False(File.Exists(expectedCleanPath), "未指定 -SeparateArchive 时不应写出干净副本");

        // 指定开关：写出干净副本，且内容与原始归档一致
        var (separateExit, text) = RunProgram($"-D \"{fx.Dict("DictOK.txt")}\" -F \"{path}\" -SeparateArchive");
        Assert.Equal(0, separateExit);
        Assert.Contains(CorrectPassword, text);
        Assert.True(File.Exists(expectedCleanPath), $"未生成干净副本：{expectedCleanPath}");
        Assert.Equal(File.ReadAllBytes(archivePath), File.ReadAllBytes(expectedCleanPath));
    }

    /// <summary>
    /// 分卷不适用分离：多文件拼成的流切成单文件既不符合格式也不符合预期。
    /// 必须明确拒绝并且不产出文件，而不是写出一个坏包。
    /// <para>
    /// 注意必须保留完整分卷集：只把首卷改名/搬走会让分卷链断开，
    /// 那样归档会被当成单卷打开，测的就不再是「分卷」这条分支了。
    /// </para>
    /// </summary>
    [Fact]
    public void MultiVolumeIsRefusedWithoutWritingAnything()
    {
        using var workspace = new Workspace();
        (string firstVolume, string expectedCleanPath) =
            workspace.CopyVolumeSetWithPrefixedFirstVolume(fx.Archive("SecretSplit.7z.001"), JunkLength);

        var (exitCode, text) = RunProgram($"-D \"{fx.Dict("DictOK.txt")}\" -F \"{firstVolume}\" -SeparateArchive");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(expectedCleanPath), "分卷不应产出干净副本");

        // 拒绝分离不能顺带把测密也弄坏：首卷带前缀时分卷链仍应正确串起来
        Assert.Contains(CorrectPassword, text);
        output.WriteLine($"exit={exitCode}{Environment.NewLine}{text}");
    }

    // ---------- 快筛：偏移必须跟着走 ----------

    /// <summary>
    /// 快速判错自己解析归档头部，前置数据会让它把偏移算错——算错的表现是「正确密码被否决」，
    /// 也就是静默漏掉正确答案。这里用真实 7z 存储模式样本 + 前置数据验证偏移处理。
    /// </summary>
    [Fact]
    public void FastFilterHonoursPrefixOffset()
    {
        using var workspace = new Workspace();

        string payloadPath = workspace.TargetPath("Sample.mp4");
        File.WriteAllBytes(payloadPath, CreateMp4LikePlaintext(8192));

        string archivePath = workspace.TargetPath("Store.7z");
        var startInfo = new ProcessStartInfo(fx.SevenZipExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "a", "-t7z", "-mx=0", "-p" + CorrectPassword, "-y", archivePath, payloadPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(startInfo)!)
        {
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"7z 生成样本失败：{stdout}{stderr}");
        }

        string prefixedPath = workspace.Compose(archivePath, prefix: JunkLength, suffix: 0);

        var info = new ArchiveInfo
        {
            Type = SevenZipGuids.CLSID_CFormat7z,
            FileCount = 1,
            IsEncrypted = true,
            IsHeaderEncryption = false,
            SupportsQuickTest = false,
        };

        var probe = SevenZipStoreProbe.TryCreate(prefixedPath, info);

        Assert.NotNull(probe);
        Assert.Equal(PasswordProbeResult.Match, probe!.Probe(CorrectPassword));
        Assert.Equal(PasswordProbeResult.NoMatch, probe.Probe("WrongPassword123"));
    }

    // ---------- 工具 ----------

    /// <summary>
    /// 杂数据：只取 <c>0x41..0x4F</c>（A..O）这段字符。
    /// 刻意避开所有归档签名的首字节（<c>0x37</c> 7z、<c>0x50</c> PK、<c>0x52</c> Rar），
    /// 于是杂数据里不可能含有任何签名，测试判据不会因随机碰撞而偶发失败。
    /// </summary>
    private static byte[] CreateJunk(int length)
    {
        var junk = new byte[length];
        for (int i = 0; i < junk.Length; i++)
        {
            junk[i] = (byte)(0x41 + (i % 15));
        }

        return junk;
    }

    private static byte[] CreateMp4LikePlaintext(int length)
    {
        var plaintext = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(plaintext, 0x18);
        System.Text.Encoding.ASCII.GetBytes("ftyp").CopyTo(plaintext, 4);
        System.Text.Encoding.ASCII.GetBytes("isom").CopyTo(plaintext, 8);
        BinaryPrimitives.WriteUInt32BigEndian(plaintext.AsSpan(12), 0x200);
        System.Text.Encoding.ASCII.GetBytes("isomiso2").CopyTo(plaintext, 16);

        return plaintext;
    }

    private int RunSevenZip(string arguments)
    {
        var startInfo = new ProcessStartInfo(fx.SevenZipExe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        output.WriteLine($"7z {arguments} → exit={process.ExitCode}{Environment.NewLine}{stdout}{stderr}");
        return process.ExitCode;
    }

    private (int ExitCode, string Output) RunProgram(string arguments)
    {
        Assert.True(File.Exists(TestPaths.MainExePath), $"主程序不存在：{TestPaths.MainExePath}");

        var startInfo = new ProcessStartInfo(TestPaths.MainExePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        // 升级检查会发网络请求，测试里一律关掉
        startInfo.Environment["APTT_NO_UPGRADE_CHECK"] = "1";

        using var process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(120_000), "主程序执行超时");

        string text = stdout + stderr;
        output.WriteLine($"exit={process.ExitCode}{Environment.NewLine}{text}");
        return (process.ExitCode, text);
    }

    /// <summary>临时工作目录 + 拼接/改写文件的小工具，测试结束整体删除</summary>
    private sealed class Workspace : IDisposable
    {
        private readonly string _directory;

        public Workspace()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"ApttJunk{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(_directory);
        }

        public string TargetPath(string fileName) => Path.Combine(_directory, fileName);

        /// <summary>把 <c>[前缀][归档][尾随]</c> 拼成一个文件</summary>
        public string Compose(string archivePath, int prefix, int suffix)
        {
            byte[] body = File.ReadAllBytes(archivePath);
            byte[] head = CreateJunk(prefix);
            byte[] tail = CreateJunk(suffix);

            byte[] result = new byte[prefix + body.Length + suffix];
            head.CopyTo(result, 0);
            body.CopyTo(result, prefix);
            tail.CopyTo(result, prefix + body.Length);

            string path = TargetPath($"Composed{Guid.NewGuid():N}{Path.GetExtension(archivePath)}");
            File.WriteAllBytes(path, result);
            return path;
        }

        /// <summary>
        /// 把一整套分卷复制到工作目录（保持原基线名，分卷链才连得上），只给首卷加上前缀。
        /// 返回首卷路径与「假如允许分离会写到哪里」的目标路径。
        /// </summary>
        public (string FirstVolume, string ExpectedCleanPath) CopyVolumeSetWithPrefixedFirstVolume(
            string firstVolume, int prefix)
        {
            string directory = Path.GetDirectoryName(firstVolume)!;
            string firstName = Path.GetFileName(firstVolume);

            // SecretSplit.7z.001 → 基线 SecretSplit.7z，扩展名序号 .001
            int dot = firstName.LastIndexOf('.');
            string stem = firstName[..dot];

            string? target = null;
            foreach (string volume in Directory.EnumerateFiles(directory, $"{stem}.*"))
            {
                string suffix = Path.GetExtension(volume);
                if (suffix.Length < 2 || !suffix[1..].All(char.IsAsciiDigit))
                {
                    continue;
                }

                string copyPath = TargetPath(Path.GetFileName(volume));
                if (volume == firstVolume)
                {
                    byte[] body = File.ReadAllBytes(volume);
                    byte[] junk = CreateJunk(prefix);
                    byte[] composed = new byte[prefix + body.Length];
                    junk.CopyTo(composed, 0);
                    body.CopyTo(composed, prefix);
                    File.WriteAllBytes(copyPath, composed);
                    target = copyPath;
                }
                else
                {
                    File.Copy(volume, copyPath);
                }
            }

            Assert.NotNull(target);
            return (target!, ArchiveSeparator.BuildTargetPath(target!));
        }

        /// <summary>
        /// 给 Zip 追加上一段归档注释：在中央目录结束记录里写入注释长度，再把注释接在后面。
        /// 用来覆盖「EOCD 不是文件最后 22 字节」的边界情形。
        /// </summary>
        public string CreateZipWithComment(string zipPath, int commentLength)
        {
            byte[] bytes = File.ReadAllBytes(zipPath);
            int eocd = FindLastIndex(bytes, [0x50, 0x4B, 0x05, 0x06]);
            Assert.True(eocd >= 0, "样本里找不到 Zip 结束记录");

            byte[] comment = CreateJunk(commentLength);
            byte[] result = new byte[eocd + 22 + commentLength];

            bytes.AsSpan(0, eocd + 22).CopyTo(result);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(eocd + 20), (ushort)commentLength);
            comment.CopyTo(result, eocd + 22);

            string path = TargetPath($"Commented{Guid.NewGuid():N}.zip");
            File.WriteAllBytes(path, result);
            return path;
        }

        private static int FindLastIndex(byte[] haystack, byte[] needle)
        {
            for (int i = haystack.Length - needle.Length; i >= 0; i--)
            {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    return i;
                }
            }

            return -1;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论
            }
        }
    }
}
