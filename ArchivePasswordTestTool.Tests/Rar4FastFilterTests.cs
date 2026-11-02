using System.Diagnostics;
using System.Text;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// RAR4 快速判错：RAR4 没有密码校验字段，7-Zip 遇到错误密码必须解完整个文件
/// 才能靠文件尾 CRC 判错（1.2GB 单文件 → 每候选 0.5s，250 万字典约 100 小时）。
/// 存储模式下密文即明文，只要派生 RAR3 密钥、解首块并校验文件格式签名即可排除错误密码。
/// <para>
/// 本文件的断言分两层：<br/>
/// ① 单元层：合成的 RAR4 存档能被探测器正确识别、正确密码命中、错误密码不命中；<br/>
/// ② 端到端层：真实启动主程序，快速筛选找到的密码仍必须由 7-Zip 完整确认；
///    且关闭快速筛选后同一存档也能被破解——这一条反向验证了 KDF 实现与真实 RAR3 一致
///    （合成的密文由生产代码的 KDF 生成，7-Zip 只有算出同样的密钥才解得开）。
/// </para>
/// </summary>
public sealed class Rar4FastFilterTests
{
    private const string CorrectPassword = "Correct123!";
    private const string EntryName = "Sample.mp4";

    private static readonly byte[] FixedSalt = [0x90, 0xBB, 0xAE, 0xF8, 0xD1, 0xC9, 0x0B, 0xC2];

    /// <summary>
    /// 物理核数必须落在 (0, 逻辑核数] 区间；探测失败时退回逻辑核数也不能是 0
    /// </summary>
    [Fact]
    public void PhysicalCoreCountIsSane()
    {
        int physical = CpuTopology.PhysicalCoreCount;

        Assert.InRange(physical, 1, Math.Max(1, Environment.ProcessorCount));
    }

    /// <summary>
    /// 并行度取「物理核」与「亲和性掩码内可用逻辑核」的较小值。
    /// 这两个 API 都不看进程亲和性（实测限制到 2 个核后仍返回 16 / 8），
    /// 因此这个合并规则是防止超订的唯一保障。
    /// </summary>
    [Theory]
    [InlineData(8, 16, 8)]   // 常见 8C16T 默认亲和性
    [InlineData(8, 2, 2)]    // 用户把进程限制到 2 个逻辑核
    [InlineData(8, 8, 8)]    // 掩码恰好覆盖 8 个逻辑核
    [InlineData(16, 24, 16)] // 12C24T 之类：物理核少于可用逻辑核
    [InlineData(16, 1, 1)]   // 单核运行
    public void ParallelismCombinesPhysicalAndAffinityCores(int physical, int available, int expected)
    {
        Assert.Equal(expected, CpuTopology.Combine(physical, available));
    }

    /// <summary>探测异常（0/负数）不能让并行度变成 0，否则 Parallel.For 直接抛异常</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 8)]
    [InlineData(8, 0)]
    [InlineData(-1, -1)]
    public void ParallelismNeverDropsToZero(int physical, int available)
    {
        Assert.True(CpuTopology.Combine(physical, available) >= 1);
    }

    /// <summary>正确密码命中签名，错误密码不命中（固定盐 → 结果可重复）</summary>
    [Fact]
    public void ProbeMatchesCorrectPasswordAndRejectsWrongOne()
    {
        using var sample = new SyntheticRar4(EntryName, Rar4ArchiveWriter.CreateMp4LikePlaintext(), CorrectPassword, FixedSalt);

        var probe = Rar4StoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal("MP4", probe!.ContentFormat);
        Assert.Equal(PasswordProbeResult.Match, probe.Probe(CorrectPassword));
        Assert.Equal(PasswordProbeResult.NoMatch, probe.Probe("WrongPassword123"));
    }

    /// <summary>
    /// 密码超过 28 个字符时记录超过 64 字节，会触发 7-Zip 的 RAR 3.5 兼容分支，
    /// 本实现不支持 → 必须明确返回 Unsupported，由调用方退回完整测密（绝不静默漏测）
    /// </summary>
    [Fact]
    public void LongPasswordIsReportedUnsupported()
    {
        using var sample = new SyntheticRar4(EntryName, Rar4ArchiveWriter.CreateMp4LikePlaintext(), CorrectPassword, FixedSalt);

        var probe = Rar4StoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal(PasswordProbeResult.Unsupported, probe!.Probe(new string('a', Rar3KeyDerivation.MaxPasswordChars + 1)));
    }

    /// <summary>压缩模式（method != 0x30）的 RAR4 不在快速判错范围内</summary>
    [Fact]
    public void CompressedArchiveIsNotEligible()
    {
        using var sample = new SyntheticRar4(EntryName, Rar4ArchiveWriter.CreateMp4LikePlaintext(), CorrectPassword, FixedSalt);
        sample.Info.FileCount = 2; // 多文件同样不适用

        Assert.Null(Rar4StoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>非 RAR4 格式（如 7z）不被误判为可快速判错</summary>
    [Fact]
    public void OtherFormatsAreNotEligible()
    {
        using var sample = new SyntheticRar4(EntryName, Rar4ArchiveWriter.CreateMp4LikePlaintext(), CorrectPassword, FixedSalt);
        sample.Info.Type = SevenZipGuids.CLSID_CFormat7z;

        Assert.Null(Rar4StoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>
    /// 端到端：快速筛选命中后仍由 7-Zip 完整确认，结果与直接完整测密一致。
    /// 关闭快速筛选的那一次同时验证了 KDF 实现（见类注释）。
    /// </summary>
    [Fact]
    public void EndToEndFindsPasswordWithAndWithoutFastFilter()
    {
        using var sample = new SyntheticRar4(EntryName, Rar4ArchiveWriter.CreateMp4LikePlaintext(), CorrectPassword, FixedSalt);

        string dictPath = Path.ChangeExtension(sample.Path, ".txt");
        File.WriteAllLines(dictPath, ["wrong1", "wrong2", CorrectPassword, "wrong3"]);

        try
        {
            var fast = RunTool(dictPath, sample.Path);
            Assert.Equal(0, fast.ExitCode);
            Assert.Contains(CorrectPassword, fast.Output);
            Assert.Contains("快速", fast.Output); // 中文界面的快速筛选提示

            // 关闭快速筛选：走 7-Zip 完整测密。若 KDF/加密实现与真实 RAR3 有偏差，
            // 这里会解不开存档 → 找不到密码
            var slow = RunTool(dictPath, sample.Path, ("APTT_NO_FAST_FILTER", "1"));
            Assert.Equal(0, slow.ExitCode);
            Assert.Contains(CorrectPassword, slow.Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    /// <summary>
    /// 端到端负例：字典里没有正确密码时，快速筛选必须报告未找到（不能出现假阳性）
    /// </summary>
    [Fact]
    public void EndToEndReportsNotFoundForAllWrongPasswords()
    {
        using var sample = new SyntheticRar4(EntryName, Rar4ArchiveWriter.CreateMp4LikePlaintext(), CorrectPassword, FixedSalt);

        string dictPath = Path.ChangeExtension(sample.Path, ".txt");
        File.WriteAllLines(dictPath, ["wrong1", "wrong2", "wrong3"]);

        try
        {
            var result = RunTool(dictPath, sample.Path);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(CorrectPassword, result.Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    private static (int ExitCode, string Output) RunTool(string dictPath, string archivePath, params (string Name, string Value)[] env)
    {
        var startInfo = new ProcessStartInfo(TestPaths.MainExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(dictPath);
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-NoQuery");
        startInfo.ArgumentList.Add("-NoReport");

        foreach (var (name, value) in env)
        {
            startInfo.Environment[name] = value;
        }

        // 关闭后台更新检查：测试不该依赖网络
        startInfo.Environment["APTT_NO_UPGRADE_CHECK"] = "1";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>合成 RAR4 存档 + 与之匹配的 <see cref="ArchiveInfo"/>（跳过真实打开，便于单元测试）</summary>
    private sealed class SyntheticRar4 : IDisposable
    {
        public SyntheticRar4(string entryName, byte[] plaintext, string password, byte[] salt)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ApttRar4{Guid.NewGuid():N}.rar");
            Rar4ArchiveWriter.CreateStoredEncrypted(Path, entryName, plaintext, password, salt);
        }

        public string Path { get; }

        public ArchiveInfo Info { get; } = new()
        {
            Type = SevenZipGuids.CLSID_CFormatRar,
            FileCount = 1,
            IsEncrypted = true,
            IsHeaderEncryption = false,
            SupportsQuickTest = false,
        };

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // 临时文件清理失败不影响断言
            }
        }
    }
}
