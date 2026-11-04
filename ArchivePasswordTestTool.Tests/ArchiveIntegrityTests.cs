using System.Diagnostics;
using System.Text;
using ArchivePasswordTestTool.Properties;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 「归档不完整」这一类输入的结论必须与「密码不对」分开。
/// <para>
/// 缺陷背景：7-Zip 的 <c>GetNumberOfItems()</c> 在分卷缺失、文件被截断时同样返回 0，
/// 而旧实现把 0 条目一律当成「header 加密」。header 加密分支的判密码方式只有一句
/// 「Open 是否返回 S_OK」，于是残缺归档下每个密码都被判错，最终输出
/// <c>未找到正确的解压密码！</c>（确定口吻、退出码 0）——把「你的文件不全」说成「密码不对」，
/// 用户会去换更大的字典，而正确做法是去补文件。
/// </para>
/// <para>
/// 这里锁两件事：① 三种真实的「0 条目」原因（缺卷 / 截断 / 本来没有条目）各自给出对应结论；
/// ② 真正的 header 加密归档不能因此被误报成残缺。
/// </para>
/// </summary>
[Collection(TestDataCollection.Name)]
public sealed class ArchiveIntegrityTests(TestDataFixture fx, ITestOutputHelper output) : IDisposable
{
    private const string SplitStem = "SecretSplit.7z";

    private static readonly byte[] DataSalt =
        [0x6B, 0x24, 0x80, 0x07, 0xF1, 0xE7, 0x71, 0x3F, 0x3D, 0xCE, 0xBE, 0x8B, 0xFE, 0xD4, 0x52, 0x49];

    private static readonly byte[] DataIv =
        [0xA4, 0x2D, 0x69, 0x89, 0xE2, 0x76, 0x92, 0xBC, 0xAF, 0x9B, 0x94, 0xCA, 0xA1, 0xE4, 0xA1, 0x34];

    private static readonly byte[] HeaderSalt =
        [0x53, 0xF9, 0xB2, 0x69, 0xD8, 0x43, 0xF5, 0x43, 0xB4, 0xF4, 0xD5, 0x4D, 0xF8, 0xCF, 0xFF, 0xD2];

    private readonly string _directory = Directory.CreateTempSubdirectory("ApttIntegrity").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论，交给系统清理
        }
    }

    private string Target(string fileName) => Path.Combine(_directory, fileName);

    private string SevenZipDll => Path.ChangeExtension(fx.SevenZipExe, ".dll");

    // ---------- 缺卷 ----------

    /// <summary>分卷编号连续时必须报「没有缺口」，否则每条正常分卷包都会被拦下</summary>
    [Fact]
    public void ContiguousVolumeSetReportsNoGap()
    {
        CopyVolumes();

        string first = Target($"{SplitStem}.001");
        _ = ArchiveContext.TryGetVolumePaths(first, out var volumes);

        Assert.True(volumes.Count > 1);
        Assert.False(ArchiveContext.TryFindMissingVolume(first, volumes, out string missing));
        Assert.Empty(missing);
    }

    /// <summary>缺中间分卷时要报出缺的是哪一卷：用户据此才知道该去补哪个文件</summary>
    [Fact]
    public void MissingMiddleVolumeIsNamedExactly()
    {
        CopyVolumes();
        File.Delete(Target($"{SplitStem}.004"));

        string first = Target($"{SplitStem}.001");
        _ = ArchiveContext.TryGetVolumePaths(first, out var volumes);

        // 收集逻辑遇缺口即停，只拿到前三卷——这正是旧实现「看起来一切正常」的原因
        Assert.Equal(3, volumes.Count);

        Assert.True(ArchiveContext.TryFindMissingVolume(first, volumes, out string missing));
        Assert.Equal($"{SplitStem}.004", Path.GetFileName(missing));
    }

    /// <summary>
    /// 缺中间分卷：结论必须是「文件不全」，不能是「密码不对」。
    /// 同时断言 header 加密这个结论没有留下来——一旦留下，后续每个密码都会被判错。
    /// </summary>
    [Fact]
    public void MissingMiddleVolumeIsReportedAsIncomplete()
    {
        CopyVolumes();
        File.Delete(Target($"{SplitStem}.004"));

        var ex = Assert.Throws<ArchiveIncompleteException>(() => OpenContext(Target($"{SplitStem}.001")));

        output.WriteLine(ex.Message);
        Assert.Equal(string.Format(Resources.ArchiveVolumeMissing, $"{SplitStem}.004"), ex.Message);
    }

    /// <summary>
    /// 只给了一个分卷（文件名仍是 .001）：分卷链接根本没启用，7-Zip 读到的是被截断的流。
    /// 这同样不能报成「密码不对」。
    /// </summary>
    [Fact]
    public void SingleVolumeOfASplitSetIsReportedAsIncomplete()
    {
        File.Copy(fx.Archive($"{SplitStem}.001"), Target($"{SplitStem}.001"));

        var ex = Assert.Throws<ArchiveIncompleteException>(() => OpenContext(Target($"{SplitStem}.001")));

        output.WriteLine(ex.Message);
        Assert.Equal(Resources.ArchiveIncomplete, ex.Message);
    }

    /// <summary>
    /// 首卷与更靠后的卷都在、中间缺一卷（<c>.001</c> + <c>.003</c>）：拼流根本不会启用（收集遇到缺口即停），
    /// 但目录里存在更大的卷号是确凿证据——要报出缺的是 <c>.002</c>，而不是笼统的「归档不完整」。
    /// </summary>
    [Fact]
    public void GapAfterFirstVolumeIsNamedEvenWhenLinkingIsNotEnabled()
    {
        File.Copy(fx.Archive($"{SplitStem}.001"), Target($"{SplitStem}.001"));
        File.Copy(fx.Archive($"{SplitStem}.003"), Target($"{SplitStem}.003"));

        var ex = Assert.Throws<ArchiveIncompleteException>(() => OpenContext(Target($"{SplitStem}.001")));

        output.WriteLine(ex.Message);
        Assert.Equal(string.Format(Resources.ArchiveVolumeMissing, $"{SplitStem}.002"), ex.Message);
    }

    // ---------- 截断 ----------

    /// <summary>被截断的单卷包：结构走不通，结论是文件不全而不是密码不对</summary>
    [Fact]
    public void TruncatedArchiveIsReportedAsIncomplete()
    {
        byte[] whole = File.ReadAllBytes(fx.Archive("Secret.7z"));
        File.WriteAllBytes(Target("Truncated.7z"), whole[..^10000]);

        var ex = Assert.Throws<ArchiveIncompleteException>(() => OpenContext(Target("Truncated.7z")));

        output.WriteLine(ex.Message);
        Assert.Equal(Resources.ArchiveIncomplete, ex.Message);
    }

    // ---------- 本来就没有条目 ----------

    /// <summary>
    /// 空 Zip（只有中央目录结束记录）：结构完好、无密码即可打开，所以它既不是「header 加密」，
    /// 也不是「损坏」——就是没有可测内容。
    /// <para>
    /// 这一条同时是假阳性的守卫：若照旧判成 header 加密，<c>TestPassword</c> 会认为 Open 成功即密码正确，
    /// 于是字典里的第一个词会被报成「正确密码」。
    /// </para>
    /// </summary>
    [Fact]
    public void EmptyZipIsReportedAsHavingNoEntries()
    {
        // 空 Zip 的全部内容就是 22 字节的 EOCD
        File.WriteAllBytes(Target("Empty.zip"), [0x50, 0x4B, 0x05, 0x06, .. new byte[18]]);

        var ex = Assert.Throws<ArchiveIncompleteException>(() => OpenContext(Target("Empty.zip")));

        output.WriteLine(ex.Message);
        Assert.Equal(Resources.ArchiveHasNoEntries, ex.Message);
    }

    // ---------- 真正的 header 加密不能被误报 ----------

    /// <summary>
    /// 头部加密的 RAR5：范围探测必须给出「格式自证加密」这个正向证据，
    /// 这样分类逻辑才不会把它当成残缺文件。错密码仍须被拒、正确密码仍须被接受。
    /// </summary>
    [Fact]
    public void HeaderEncryptedRar5IsStillTreatedAsEncrypted()
    {
        string path = Target("HeaderEncrypted.rar");
        byte[] plaintext = new byte[4096];
        new Random(20240102).NextBytes(plaintext);

        _ = Rar5ArchiveWriter.CreateHeaderEncrypted(
            path, "Payload.bin", plaintext, TestDataFixture.CorrectPassword, DataSalt, DataIv, HeaderSalt);

        var (format, offset) = ArchiveFormatDetector.DetectArchiveFormat(path);
        ArchiveExtent? extent = ArchiveExtentDetector.Detect(path, format, offset);

        Assert.NotNull(extent);
        Assert.True(extent!.HeaderEncrypted, "RAR5 归档加密头必须被识别为 header 加密的正向证据");
        Assert.Null(extent.End);

        using var withoutPassword = OpenContext(path);
        Assert.Equal(0u, withoutPassword.Info.FileCount);
        Assert.True(withoutPassword.Info.IsHeaderEncryption);

        // header 加密包的密码只能靠「带密码重新 Open」验证（见 TestPassword 的该分支），
        // 所以这里按生产路径各开一次：错密码必须被拒，正确密码必须被接受。
        // 关键是不能在「错密码」那次被误判成 ArchiveIncompleteException——那会让合法包直接报错退出
        var wrong = Record.Exception(() => new ArchiveContext(SevenZipDll, path, "WrongPassword"));
        if (wrong is not null)
        {
            Assert.IsType<ArchivePasswordException>(wrong);
        }

        using var withPassword = new ArchiveContext(SevenZipDll, path, TestDataFixture.CorrectPassword);
        Assert.True(withPassword.TestPassword(TestDataFixture.CorrectPassword), "正确密码必须被判为正确");
    }

    // ---------- 端到端：退出码与最终结论文案 ----------

    /// <summary>
    /// 端到端：残缺归档必须以非零退出码报错，且输出里不能出现「未找到正确的解压密码！」。
    /// 屏幕上的结论是用户唯一会读的东西，它说「密码不对」就是在撒谎。
    /// </summary>
    [Fact]
    public void IncompleteArchiveExitsWithErrorInsteadOfNotFoundVerdict()
    {
        CopyVolumes();
        File.Delete(Target($"{SplitStem}.004"));

        var (exitCode, text) = RunProgram($"-D \"{fx.Dict("DictOK.txt")}\" -F \"{Target($"{SplitStem}.001")}\"");

        Assert.NotEqual(0, exitCode);

        string squashed = OutputText.SquashForMatch(text);
        Assert.DoesNotContain(OutputText.SquashForMatch(Resources.PasswordNotFound), squashed, StringComparison.Ordinal);
        Assert.Contains(OutputText.SquashForMatch($"{SplitStem}.004"), squashed, StringComparison.Ordinal);
    }

    /// <summary>端到端：截断的包同样不能报成「密码未找到」，且结论里要指出文件不完整</summary>
    [Fact]
    public void TruncatedArchiveExitsWithErrorInsteadOfNotFoundVerdict()
    {
        byte[] whole = File.ReadAllBytes(fx.Archive("Secret.7z"));
        File.WriteAllBytes(Target("Truncated.7z"), whole[..^10000]);

        var (exitCode, text) = RunProgram($"-D \"{fx.Dict("DictOK.txt")}\" -F \"{Target("Truncated.7z")}\"");

        Assert.NotEqual(0, exitCode);

        string squashed = OutputText.SquashForMatch(text);
        Assert.DoesNotContain(OutputText.SquashForMatch(Resources.PasswordNotFound), squashed, StringComparison.Ordinal);
        Assert.Contains(OutputText.SquashForMatch(Resources.ArchiveIncomplete), squashed, StringComparison.Ordinal);
    }

    // ---------- 工具 ----------

    private void CopyVolumes()
    {
        foreach (string volume in Directory.EnumerateFiles(fx.TestDataDir, $"{SplitStem}.*"))
        {
            File.Copy(volume, Target(Path.GetFileName(volume)));
        }
    }

    private ArchiveContext OpenContext(string archivePath) => new(SevenZipDll, archivePath, password: null);

    private (int ExitCode, string Output) RunProgram(string arguments)
    {
        Assert.True(File.Exists(TestPaths.MainExePath), $"主程序不存在：{TestPaths.MainExePath}");

        var startInfo = new ProcessStartInfo(TestPaths.MainExePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
}
