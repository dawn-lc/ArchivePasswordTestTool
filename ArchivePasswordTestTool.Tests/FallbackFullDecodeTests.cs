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

            // 快筛命中即结束，不会走到复核分支。
            // 同样按约定枚举全部语言的文案——只写一种语言在别的 UI 语言下会永不命中（假绿）
            string prose = KeepProse(result.Output);
            string? leaked = LocalizedTexts("FallbackFullDecodeStarted")
                .FirstOrDefault(marker => prose.Contains(marker, StringComparison.Ordinal));

            Assert.True(leaked is null, $"不该出现复核提示，但输出含「{leaked}」：\n{result.Output}");
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 复核阶段的计数与耗时必须独立：最终「已测试」应等于字典条数（复核跑一遍），
    /// 而不是「快筛 N 条 + 复核 N 条」的两倍——那是两个不同量级的成本，混在一起无法解读。
    /// <para>
    /// 标签文案按项目约定从各语言资源枚举（<c>ResultTested</c>），不能写死某一种语言：
    /// 写死会在其它 UI 语言下永不命中（假绿或假红），CI 跑在英文系统上曾因此失败。
    /// </para>
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

            int? tested = FindTestedCount(result.Output);
            Assert.True(tested is not null, $"输出里找不到「已测试」行：\n{result.Output}");
            Assert.Equal(3, tested!.Value);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 取某个资源键在全部受支持语言下的文案（已按 KeepProse 同样规则净化）。
    /// 断言「某提示是否出现」必须这么做：只写一种语言，在其它 UI 语言下会永不命中。
    /// </summary>
    internal static IEnumerable<string> LocalizedTexts(string key)
    {
        foreach (string culture in new[] { "en", "zh-CN", "ja", "fr", "ru" })
        {
            string? value = ArchivePasswordTestTool.Properties.EmbeddedResourceManager.GetString(
                key, new System.Globalization.CultureInfo(culture));

            if (!string.IsNullOrWhiteSpace(value))
            {
                // 资源里可能有 {0} 占位符，取第一段常量文字作为标记
                string marker = KeepProse(value.Split('{')[0]).Trim();

                if (marker.Length > 0)
                {
                    yield return marker;
                }
            }
        }
    }

    /// <summary>
    /// 从输出里读出「已测试」的计数。标签在五种语言下各不相同，逐个尝试；
    /// 面板边框与折行可能夹在标签与数字之间，因此先滤成「字母 + 空格 + 数字」再匹配。
    /// </summary>
    internal static int? FindTestedCount(string output)
    {
        string prose = KeepProse(output);
        foreach (string culture in new[] { "en", "zh-CN", "ja", "fr", "ru" })
        {
            string? label = ArchivePasswordTestTool.Properties.EmbeddedResourceManager.GetString(
                "ResultTested", new System.Globalization.CultureInfo(culture));

            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                prose, System.Text.RegularExpressions.Regex.Escape(label.Trim()) + @"\s+(\d+)");

            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value);
            }
        }

        return null;
    }

    // ---------- 辅助 ----------

    /// <summary>
    /// 语言无关断言的自我保护：这两种「从输出里认文案」的逻辑必须对**每一种** UI 语言都成立。
    /// <para>
    /// 真实 CI 跑在英文系统上，只断言中文文案的用例会在那里失败（曾经发生过）；
    /// 反过来只断英文又会在中文环境下假绿。这里显式构造各语言的面板行直接验证解析逻辑，
    /// 不依赖跑测试的那台机器的系统语言。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    [InlineData("ja")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void TestedCountIsReadableInEveryUiLanguage(string culture)
    {
        var info = new System.Globalization.CultureInfo(culture);
        string label = ArchivePasswordTestTool.Properties.EmbeddedResourceManager.GetString("ResultTested", info)!;

        // 模拟真实面板行：边框 + 标签 + 缩进 + 数字（与 Ui.WriteOutcome 的 AddRow 一致）
        Assert.Equal(3, FindTestedCount($"│ {label}    3  │"));
    }

    /// <summary>同理验证「复核已开始」提示的识别：各语言的标记都必须在各自文案里命中</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    [InlineData("ja")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void FullDecodeMarkerIsDetectedInEveryUiLanguage(string culture)
    {
        var info = new System.Globalization.CultureInfo(culture);
        string template = ArchivePasswordTestTool.Properties.EmbeddedResourceManager.GetString(
            "FallbackFullDecodeStarted", info)!;

        string prose = KeepProse($"│ {string.Format(template, "3")} │");

        Assert.Contains(
            LocalizedTexts("FallbackFullDecodeStarted"),
            marker => prose.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// 带 ANSI 颜色的面板行必须照样解析得出计数。
    /// <para>
    /// 这是 CI 上实际失败的形态：Spectre 在 runner 上输出颜色，转义序列里的字母
    /// （<c>x</c>/<c>b</c>/<c>m</c>）会夹在标签与数字之间，把 <c>Tested␣␣␣3</c> 变成
    /// <c>Testedm m3</c> 之类，正则随即失配。本地重定向不带 ANSI，所以只在本机测不出来。
    /// </para>
    /// </summary>
    [Fact]
    public void TestedCountSurvivesAnsiColoring()
    {
        string label = ArchivePasswordTestTool.Properties.EmbeddedResourceManager.GetString(
            "ResultTested", System.Globalization.CultureInfo.InvariantCulture)!;

        // 模拟 Ui.WriteOutcome 的行：标签一段颜色、数值另一段颜色，中间还有边框
        string colored = $"\x1b[38;5;51m│\x1b[0m \x1b[38;5;8m{label}\x1b[0m     \x1b[38;5;15m3\x1b[0m  \x1b[38;5;51m│\x1b[0m";

        Assert.Equal(3, FindTestedCount(colored));
    }

    /// <summary>
    /// 输出里有 ANSI 转义序列与面板边框；净化规则见 <see cref="OutputText.LettersDigitsAndSpaces"/>
    /// （必须先剥 ANSI，否则转义里的字母会掺进文本中间）。
    /// </summary>
    internal static string KeepProse(string text) => OutputText.LettersDigitsAndSpaces(text);

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
