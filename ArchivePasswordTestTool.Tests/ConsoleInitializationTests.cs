namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// Windows 控制台适配的守护测试。字体宽度只能在真实终端里实测，这里锁定几件
/// 「删掉就会在特定环境下静默出问题」的事：
/// <list type="number">
/// <item>启动时先初始化控制台，再发起更新检查等其它动作；</item>
/// <item>进入备用缓冲区后重新修正字体——conhost 切换缓冲区会重置窗口字体；</item>
/// <item>退出时把字体与代码页还给用户——终端窗口是 shell 的，不是本程序的；</item>
/// <item>字体写入必须读回校验：conhost 会返回成功却只应用字号；</item>
/// <item>两种结构体声明都要保留——读写各自只认其中一种。</item>
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

    /// <summary>只看非注释行：注释里解释取舍是正常的，按整份文本匹配会把说明文字当成用法。</summary>
    private static List<string> CodeLines(string source) =>
        source
            .Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("*", StringComparison.Ordinal))
            .ToList();

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
    public void ConsoleSettingsAreRestoredOnExit()
    {
        string code = ReadSource("ArchivePasswordTestTool", "Program.cs");

        // 正常返回走 finally；Environment.Exit（错误路径、Ctrl+C 接管）不跑 finally，
        // 由 ProcessExit 兜底。两条路都不能少。
        Assert.Contains("finally", code, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit", code, StringComparison.Ordinal);
        Assert.Contains("WindowsNativeMethods.RestoreConsole();", code, StringComparison.Ordinal);

        string interop = ReadSource("ArchivePasswordTestTool", "Interop", "Native", "WindowsNativeMethods.cs");
        Assert.Contains("public static void RestoreConsole()", interop, StringComparison.Ordinal);
        Assert.Contains("SetConsoleOutputCP(originalOutputCodePage)", interop, StringComparison.Ordinal);

        // 代码页必须在改动前记录：Console.OutputEncoding 的 setter 自己会改原生代码页
        Assert.Contains("CaptureConsoleCodePage()", interop, StringComparison.Ordinal);
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
    public void FontWriteIsVerifiedByReadBack()
    {
        string interop = ReadSource("ArchivePasswordTestTool", "Interop", "Native", "WindowsNativeMethods.cs");

        // conhost 的 SetCurrentConsoleFontEx 会返回 true 却只应用字号，
        // 不读回校验就会以为切换成功（实测：恢复时字体名不变，终端停在程序字体上）。
        Assert.Contains("IsConsoleFontApplied", interop, StringComparison.Ordinal);
        Assert.Contains("useInlineBuffer", interop, StringComparison.Ordinal);
    }

    [Fact]
    public void BothFontStructDeclarationsAreKept()
    {
        var code = CodeLines(ReadSource("ArchivePasswordTestTool", "Interop", "Native", "WindowsNativeMethods.cs"));

        // 内联 fixed char[32] 负责「新宋体 → Courier New」这类切换，
        // 定长字符串（ByValTStr）负责读到完整名字与另一方向的切换；少一个就有还原不了的情况。
        Assert.Contains(code, line => line.Contains("fixed char FaceName[32]", StringComparison.Ordinal));
        Assert.Contains(code, line => line.Contains("ByValTStr", StringComparison.Ordinal));
    }
}
