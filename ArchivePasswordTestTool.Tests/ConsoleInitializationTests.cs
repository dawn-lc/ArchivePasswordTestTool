namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// Windows 控制台适配的守护测试。字体宽度只能在真实终端里实测，这里锁定三件
/// 「删掉就会在特定环境下静默错位」的事：
/// <list type="number">
/// <item>启动时先初始化控制台，再发起更新检查等其它动作；</item>
/// <item>进入备用缓冲区后重新修正字体——conhost 切换缓冲区会重置窗口字体；</item>
/// <item>字体结构体保持 blittable——对外发布的是 Native AOT 产物，不能依赖运行时封送。</item>
/// </list>
/// </summary>
public sealed class ConsoleInitializationTests
{
    private static string ReadSource(params string[] relativePath)
    {
        string path = TestPaths.RepoRoot;
        foreach (string part in relativePath)
        {
            path = Path.Combine(path, part);
        }

        return File.ReadAllText(path);
    }

    [Fact]
    public void ConsoleIsInitializedBeforeOtherStartupWork()
    {
        string code = ReadSource("ArchivePasswordTestTool", "Program.cs");

        int initialize = code.IndexOf("InitializeConsole();", StringComparison.Ordinal);
        int update = code.IndexOf("PendingUpdate.TryApply(args)", StringComparison.Ordinal);

        Assert.True(initialize >= 0, "Program.Main 应调用 InitializeConsole");
        Assert.True(update > initialize, "控制台初始化必须早于更新检查/自我替换");
        Assert.Contains("AnsiConsole.Profile.Capabilities.Unicode = true", code);
        Assert.Contains("TryEnableUtf8Console", code);
    }

    [Fact]
    public void AlternateScreenReappliesConsoleFont()
    {
        string code = ReadSource("ArchivePasswordTestTool", "Ui", "Ui.cs");

        int alternateScreen = code.IndexOf("AnsiConsole.AlternateScreen(", StringComparison.Ordinal);
        int ensureFont = code.IndexOf("EnsureNarrowAmbiguousWidth();", StringComparison.Ordinal);
        int action = alternateScreen < 0
            ? -1
            : code.IndexOf("action();", alternateScreen, StringComparison.Ordinal);

        Assert.True(alternateScreen >= 0 && ensureFont >= 0 && action >= 0);
        Assert.True(
            ensureFont > alternateScreen && ensureFont < action,
            "字体修正必须在进入备用缓冲区之后、首帧绘制之前执行");
    }

    [Fact]
    public void ConsoleFontStructStaysBlittableForAot()
    {
        string code = ReadSource("ArchivePasswordTestTool", "Interop", "WindowsNativeMethods.cs");

        // 定长缓冲是 blittable 的；string + ByValTStr 会引入运行时封送，
        // 在 Native AOT 下属于动态代码路径。注释里解释这个取舍是正常的，
        // 只检查非注释行（与其他源码守护测试一致）。
        var codeLines = code
            .Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("*", StringComparison.Ordinal));

        Assert.Contains(codeLines, line => line.Contains("fixed char FaceName[32]", StringComparison.Ordinal));
        Assert.DoesNotContain(codeLines, line => line.Contains("ByValTStr", StringComparison.Ordinal));
    }
}
