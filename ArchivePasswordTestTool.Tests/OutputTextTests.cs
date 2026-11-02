namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// <see cref="OutputText"/> 的净化规则。这些不是"顺手写的字符串工具"——
/// 每条规则都对应一次真实的 CI 失败（本地因为环境差异测不出来），所以单独锁住。
/// </summary>
public sealed class OutputTextTests
{
    /// <summary>CSI 颜色序列必须被完整剥离，包括参数里带 <c>;</c> 的 256 色形式</summary>
    [Theory]
    [InlineData("\x1b[0m")]
    [InlineData("\x1b[38;5;15m")]
    [InlineData("\x1b[1;38;5;14m")]
    [InlineData("\x1b[2;38;5;8m")]
    public void StripAnsiRemovesColorSequences(string escape)
    {
        Assert.Equal("abc", OutputText.StripAnsi($"{escape}abc\x1b[0m"));
    }

    /// <summary>
    /// CI 上真实失败的形态复现：Spectre 输出颜色，转义序列里的字母（<c>x</c>/<c>b</c>/<c>m</c>）
    /// 若不被剥离，会夹进长密码中间，把它切碎成 41/64/64/22 四段，<c>Contains</c> 随即失配。
    /// </summary>
    [Fact]
    public void LettersAndDigitsReconnectsWrappedLongPassword()
    {
        string password = new('K', 150);

        // 模拟 Grid 单元格折行：三段密码，每段两侧是边框与颜色，段间还有缩进
        string first = password[..64];
        string second = password[64..128];
        string third = password[128..];

        string panel =
            $"\x1b[38;5;51m│\x1b[0m \x1b[38;5;8mPassword\x1b[0m  \x1b[38;5;15m{first}\x1b[0m \x1b[38;5;51m│\x1b[0m\n" +
            $"\x1b[38;5;51m│\x1b[0m           \x1b[38;5;15m{second}\x1b[0m \x1b[38;5;51m│\x1b[0m\n" +
            $"\x1b[38;5;51m│\x1b[0m           \x1b[38;5;15m{third}\x1b[0m \x1b[38;5;51m│\x1b[0m";

        Assert.Contains(password, OutputText.LettersAndDigits(panel));
    }

    /// <summary>
    /// 标签与数值之间隔着颜色序列时，净化后必须重新变成「标签 空白 数值」，
    /// 否则按标签找数值的正则会失配（CI 上 FallbackReportsItsOwnPassCount 就是这么挂的）。
    /// </summary>
    [Fact]
    public void LabelsAndValuesStayAdjacentAcrossColorBoundaries()
    {
        string panel = "\x1b[38;5;51m│\x1b[0m \x1b[38;5;8mTested\x1b[0m     \x1b[38;5;15m3\x1b[0m  \x1b[38;5;51m│\x1b[0m";

        Assert.Equal(" Tested     3  ", OutputText.LettersDigitsAndSpaces(panel));
    }

    /// <summary>换行与面板边框会破坏跨行内容，因此字母数字模式把它们全部丢弃</summary>
    [Fact]
    public void LettersAndDigitsDropsBoxDrawingCharacter()
    {
        Assert.Equal("abcd", OutputText.LettersAndDigits("│ab\n│cd│"));
    }
}
