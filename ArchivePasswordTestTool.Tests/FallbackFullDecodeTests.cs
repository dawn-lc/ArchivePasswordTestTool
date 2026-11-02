using System.Diagnostics;
using System.Text;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 「快筛全否后的完整解密复核」（<c>-FallbackFullDecode</c>）。
/// <para>
/// 快速筛选的前提是「正确密码解出的首块必然匹配某个已知文件格式」。当归档内扩展名撒谎
/// （400 错误页存成 <c>movie.mp4</c>）或载荷被剥掉容器头（裸 PCM 存成 <c>.wav</c>）时，
/// 这个前提不成立，正确密码会被当作错误密码丢弃——静默漏测。
/// </para>
/// <para>
/// 本组用例用**首块没有任何已知魔数**的内容复现该漏测，再验证带开关时能被完整解密救回。
/// 默认（不带开关）必须保持原行为：不自动复核，否则「密码不在字典里」这个最常见情形
/// 会莫名其妙多付一份完整解密的代价。
/// </para>
/// </summary>
public sealed class FallbackFullDecodeTests
{
    private const string CorrectPassword = "Correct123!";

    /// <summary>归档内的名字必须对应一个已收录的签名格式，快筛才会启用</summary>
    private const string DeclaredName = "Movie.mp4";

    private static readonly Lazy<TestDataFixture> Fixture = new(() => new TestDataFixture());

    /// <summary>
    /// 核心用例：纯文本内容装成 <c>.mp4</c>，快筛必然排除全部候选。
    /// 不带开关 → 未找到（既有行为）；带开关 → 完整解密找回正确密码。
    /// </summary>
    [Fact]
    public void FallbackFlagRecoversPasswordRejectedBySignature()
    {
        string dir = CreateDirectory();
        try
        {
            string archivePath = Path.Combine(dir, "NoMagic.7z");
            CreateStoredArchive(dir, archivePath, CreatePlainText(60000));

            string dictPath = WriteDictionary(archivePath);

            // ① 默认：快筛按签名排除全部候选，报未找到（这与引入本功能前的行为一致）
            var without = RunTool(dictPath, archivePath, fallback: false);
            Assert.Equal(0, without.ExitCode);
            Assert.DoesNotContain(CorrectPassword, without.Output);

            // ② 打开复核开关：只多花一次完整解密的钱，把正确答案找回来
            var with = RunTool(dictPath, archivePath, fallback: true);
            Assert.Equal(0, with.ExitCode);
            Assert.Contains(CorrectPassword, with.Output);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 开关不能改变「本来就是正常包」的结论：内容带真实 MP4 魔数时快筛直接命中，
    /// 复核根本不该触发（也不该在输出里出现复核提示）。
    /// </summary>
    [Fact]
    public void FallbackDoesNotTriggerWhenSignatureMatches()
    {
        string dir = CreateDirectory();
        try
        {
            string archivePath = Path.Combine(dir, "RealMp4.7z");
            CreateStoredArchive(dir, archivePath, CreateMp4Like(60000));

            string dictPath = WriteDictionary(archivePath);

            var result = RunTool(dictPath, archivePath, fallback: true);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(CorrectPassword, result.Output);

            // 快筛命中即结束，不会走到复核分支
            Assert.DoesNotContain("Full-decode verification started", KeepProse(result.Output));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 复核阶段的计数与耗时必须独立：最终「已测试」应等于字典条数（复核跑一遍），
    /// 而不是「快筛 N 条 + 复核 N 条」的两倍——那是两个不同量级的成本，混在一起无法解读。
    /// </summary>
    [Fact]
    public void FallbackReportsItsOwnPassCount()
    {
        string dir = CreateDirectory();
        try
        {
            string archivePath = Path.Combine(dir, "NoMagic2.7z");
            CreateStoredArchive(dir, archivePath, CreatePlainText(60000));

            // 3 条密码：复核应当只报 3，不是 6
            string dictPath = WriteDictionary(archivePath);

            var result = RunTool(dictPath, archivePath, fallback: true);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(CorrectPassword, result.Output);

            var match = System.Text.RegularExpressions.Regex.Match(
                KeepProse(result.Output), @"已测试\s+(\d+)");
            Assert.True(match.Success, $"输出里找不到「已测试」行：\n{result.Output}");
            Assert.Equal(3, int.Parse(match.Groups[1].Value));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    // ---------- 辅助 ----------

    /// <summary>
    /// 输出里有 ANSI 序列与面板边框；只保留字母与数字便于跨边框做包含判断
    /// （与 EndToEndTests.LongPasswordIsReportedInFull 同一手法）。
    /// </summary>
    private static string KeepProse(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || c == ' ')
            {
                _ = builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string WriteDictionary(string archivePath)
    {
        string dictPath = Path.ChangeExtension(archivePath, ".txt");
        File.WriteAllLines(dictPath, ["DefinitelyWrong1", CorrectPassword, "DefinitelyWrong2"]);
        return dictPath;
    }

    /// <summary>7z Copy（<c>-mx=0</c>）：密文无结构，正是快筛的目标场景</summary>
    private static void CreateStoredArchive(string dir, string archivePath, byte[] payload)
    {
        string payloadPath = Path.Combine(dir, DeclaredName);
        File.WriteAllBytes(payloadPath, payload);

        var startInfo = new ProcessStartInfo(Fixture.Value.SevenZipExe)
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

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"7z 创建压缩包失败（exit {process.ExitCode}）：\n{stdout}\n{stderr}");
    }

    private static (int ExitCode, string Output) RunTool(string dictPath, string archivePath, bool fallback)
    {
        var startInfo = new ProcessStartInfo(TestPaths.MainExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // 显式重定向 stdin：让子进程判定为「非交互」，复核询问不会阻塞测试。
            // 真实终端里同样的场景会询问一次（默认 N）。
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(dictPath);
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-NoQuery");
        startInfo.ArgumentList.Add("-NoReport");

        if (fallback)
        {
            startInfo.ArgumentList.Add("-FallbackFullDecode");
        }

        startInfo.Environment["APTT_NO_UPGRADE_CHECK"] = "1";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        process!.StandardInput.Close();

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>纯文本：首块 16 字节全是可打印 ASCII，不匹配任何已知魔数</summary>
    private static byte[] CreatePlainText(int length) =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
            "The quick brown fox jumps over the lazy dog. 0123456789 ", (length / 56) + 1)))[..length];

    /// <summary>真实 MP4 首块：ftyp 盒子</summary>
    private static byte[] CreateMp4Like(int length)
    {
        byte[] content = new byte[length];
        ReadOnlySpan<byte> head =
        [
            0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70,
            0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00,
        ];
        head.CopyTo(content);

        for (int i = head.Length; i < content.Length; i++)
        {
            content[i] = (byte)((i * 37) % 251);
        }

        return content;
    }

    private static string CreateDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ApttFallback{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结果
        }
    }
}
