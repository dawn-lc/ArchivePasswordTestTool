using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 扩展名伪装：压缩包内的文件名是打包者定的，可能与实际内容不符
/// （实测：PNG 内容命名为 <c>Sample.mp4</c>）。
/// <para>
/// 快速判错必须对此免疫。若只按扩展名推断签名，正确密码解出的 PNG 首块会被判为
/// <see cref="PasswordProbeResult.NoMatch"/> 而丢弃，用户得到「未找到密码」——
/// 这是密码破解工具最坏的失效模式（静默漏测）。已用真实样本复现，
/// 修复方式是「扩展名签名 **或** 任一已知容器签名」都算候选。
/// </para>
/// <para>
/// 所有用例都同时跑「快筛启用」与「快筛关闭」，两者结论必须一致——
/// 这是判断筛选有没有丢掉正确答案的唯一可靠标准。
/// </para>
/// </summary>
public sealed class ContentSignatureSpoofingTests
{
    private const string CorrectPassword = "Correct123!";

    private const string DeclaredName = "Sample.mp4";

    private static readonly byte[] FixedSalt = [0x90, 0xBB, 0xAE, 0xF8, 0xD1, 0xC9, 0x0B, 0xC2];

    private static readonly Lazy<TestDataFixture> Fixture = new(() => new TestDataFixture());

    // ---------- 匹配层（纯函数，直接验证兜底逻辑） ----------

    /// <summary>PNG 内容不会被误判成 MP4——这正是修复前漏掉正确答案的直接原因</summary>
    [Fact]
    public void PngContentDoesNotMatchMp4Signature()
    {
        var mp4 = ContentSignature.ForFileName(DeclaredName);

        Assert.NotNull(mp4);
        Assert.Equal("MP4", mp4!.Format);
        Assert.False(mp4.Matches(PngBlock()));
    }

    /// <summary>但兜底表能认出它是 PNG，因此组合判定必须通过</summary>
    [Fact]
    public void PngContentMatchesAnyKnownContainer()
    {
        var mp4 = ContentSignature.ForFileName(DeclaredName)!;

        Assert.True(ContentSignature.MatchesAnyKnownContainer(PngBlock()));
        Assert.True(mp4.MatchesFileOrAnyKnownContainer(PngBlock()));
    }

    /// <summary>随机字节不该被任何签名接受（否则快筛退化成完整测密）</summary>
    [Fact]
    public void RandomBlockIsRejected()
    {
        byte[] garbage = new byte[16];
        for (int i = 0; i < garbage.Length; i++)
        {
            garbage[i] = (byte)(i * 37 + 11);
        }

        Assert.False(ContentSignature.MatchesAnyKnownContainer(garbage));
    }

    /// <summary>
    /// 兜底表只收录魔数 ≥ 3 字节的格式：2 字节魔数随机命中率 2⁻¹⁶，
    /// 250 万候选会有上百次误报，足以把候选上限打满并让扫描提前中断。
    /// 这类格式仍可通过扩展名精确匹配。
    /// </summary>
    [Theory]
    [InlineData(".exe")]   // MZ，2 字节
    [InlineData(".bmp")]   // BM，2 字节
    [InlineData(".gz")]    // 1F 8B，2 字节
    public void WeakTwoByteSignaturesAreExcludedFromFallback(string extension)
    {
        var signature = ContentSignature.ForFileName(extension);
        Assert.NotNull(signature);

        // 构造一个「魔数正确、其余为任意字节」的首块
        byte[] block = new byte[16];
        block[0] = (byte)(extension == ".exe" ? 'M' : extension == ".bmp" ? 'B' : 0x1F);
        block[1] = (byte)(extension == ".exe" ? 'Z' : extension == ".bmp" ? 'M' : 0x8B);

        Assert.True(signature!.Matches(block));                        // 扩展名精确匹配仍然可用
        Assert.False(ContentSignature.MatchesAnyKnownContainer(block)); // 但不进兜底表
    }

    // ---------- 端到端：三种格式各跑一次 ----------

    /// <summary>7z Copy + AES：PNG 内容命名为 .mp4</summary>
    [Fact]
    public void SevenZipCopySpoofedExtensionStillFindsPassword()
    {
        string dir = CreateDirectory();
        try
        {
            string archivePath = Path.Combine(dir, "Spoof.7z");
            CreateSevenZipArchive(dir, archivePath, CreatePng(4096));

            AssertPasswordFound(archivePath);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>ZipCrypto + Store：PNG 内容命名为 .mp4</summary>
    [Fact]
    public void ZipCryptoStoreSpoofedExtensionStillFindsPassword()
    {
        string dir = CreateDirectory();
        try
        {
            string payloadPath = Path.Combine(dir, DeclaredName);
            File.WriteAllBytes(payloadPath, CreatePng(4096));

            string archivePath = Path.Combine(dir, "Spoof.zip");
            RunSevenZip([
                "a", "-tzip", "-mx=0", "-mem=ZipCrypto",
                "-p" + CorrectPassword, "-y", archivePath, payloadPath,
            ]);

            AssertPasswordFound(archivePath);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>RAR4 存储模式：PNG 内容命名为 .mp4</summary>
    [Fact]
    public void Rar4StoreSpoofedExtensionStillFindsPassword()
    {
        string dir = CreateDirectory();
        try
        {
            string archivePath = Path.Combine(dir, "Spoof.rar");
            Rar4ArchiveWriter.CreateStoredEncrypted(
                archivePath, DeclaredName, CreatePng(4096), CorrectPassword, FixedSalt);

            AssertPasswordFound(archivePath);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    // ---------- 辅助 ----------

    /// <summary>同时跑快筛启用与关闭两次，两次都必须找到密码</summary>
    private static void AssertPasswordFound(string archivePath)
    {
        string dictPath = Path.ChangeExtension(archivePath, ".txt");
        File.WriteAllLines(dictPath, ["DefinitelyWrong1", CorrectPassword, "DefinitelyWrong2"]);

        try
        {
            var fast = RunTool(dictPath, archivePath, noFastFilter: false);
            Assert.Equal(0, fast.ExitCode);
            Assert.True(
                fast.Output.Contains(CorrectPassword, StringComparison.Ordinal),
                $"快速筛选漏掉了正确答案（扩展名伪装）。输出：\n{fast.Output}");

            // 关闭快速筛选作为对照：完整解密必然能找到，两者结论必须一致
            var slow = RunTool(dictPath, archivePath, noFastFilter: true);
            Assert.Equal(0, slow.ExitCode);
            Assert.Contains(CorrectPassword, slow.Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    private static (int ExitCode, string Output) RunTool(string dictPath, string archivePath, bool noFastFilter)
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

        startInfo.Environment["APTT_NO_UPGRADE_CHECK"] = "1";
        if (noFastFilter)
        {
            startInfo.Environment["APTT_NO_FAST_FILTER"] = "1";
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }

    private static void CreateSevenZipArchive(string dir, string archivePath, byte[] payload)
    {
        string payloadPath = Path.Combine(dir, DeclaredName);
        File.WriteAllBytes(payloadPath, payload);

        RunSevenZip([
            "a", "-t7z", "-mx=0", "-p" + CorrectPassword, "-y", archivePath, payloadPath,
        ]);
    }

    private static void RunSevenZip(string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Fixture.Value.SevenZipExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"7z 执行失败（exit {process.ExitCode}）：\n{stdout}\n{stderr}");
    }

    /// <summary>以真实 PNG 魔数开头、其余为确定性填充的内容</summary>
    private static byte[] CreatePng(int length)
    {
        byte[] content = new byte[length];
        ReadOnlySpan<byte> magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(content);

        for (int i = magic.Length; i < content.Length; i++)
        {
            content[i] = (byte)(i * 13 + 7);
        }

        return content;
    }

    private static byte[] PngBlock()
    {
        byte[] block = new byte[16];
        CreatePng(block.Length).CopyTo(block, 0);
        return block;
    }

    private static string CreateDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ApttSpoof{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时样本清理失败不影响断言
        }
    }
}
