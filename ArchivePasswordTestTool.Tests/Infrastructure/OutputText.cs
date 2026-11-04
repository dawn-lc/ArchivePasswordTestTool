using System.Text.RegularExpressions;

namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 从子进程输出里提取「用户实际看到的文字」。
/// <para>
/// 两个必须做的净化，否则断言会在某些环境下静默失效：
/// </para>
/// <list type="number">
/// <item>
/// <b>剥离 ANSI 转义序列</b>。Spectre 在部分环境会输出颜色（<c>\x1b[38;5;15m</c>），
/// 而转义序列里含字母（<c>x</c>、<c>b</c>、<c>m</c>）。若只做「保留字母数字」的过滤，
/// 这些字母会掺进文本中间——实测把 150 字符的密码切成 41/64/64/22 段，断言直接失败。
/// CI runner 带 ANSI 而本地重定向不带，所以本地测不出来。
/// </item>
/// <item>
/// <b>丢掉折行与边框</b>。面板单元格折行时会在跨界处插入边框字符（<c>│</c>）与缩进，
/// 长内容因此被切成多段；只保留字母数字就能把跨行的片段重新接回去。
/// </item>
/// </list>
/// </summary>
internal static class OutputText
{
    /// <summary>CSI 序列：ESC [ 参数… 终止字母（颜色、光标、清屏等）。</summary>
    private static readonly Regex AnsiEscape = new(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>去掉所有 ANSI 转义序列，其余字符原样保留（含换行，便于按行分析）。</summary>
    public static string StripAnsi(string text) => AnsiEscape.Replace(text, string.Empty);

    /// <summary>
    /// 只保留字母与数字，用于跨折行拼接被边框/缩进打断的长内容（如超长密码）。
    /// </summary>
    public static string LettersAndDigits(string text)
    {
        string plain = StripAnsi(text);
        var builder = new System.Text.StringBuilder(plain.Length);

        foreach (char c in plain)
        {
            if (char.IsLetterOrDigit(c))
            {
                _ = builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 只保留字母、数字与空格，用于在带边框的面板输出里做「标签 + 值」的行内匹配。
    /// </summary>
    public static string LettersDigitsAndSpaces(string text)
    {
        string plain = StripAnsi(text);
        var builder = new System.Text.StringBuilder(plain.Length);

        foreach (char c in plain)
        {
            if (char.IsLetterOrDigit(c) || c == ' ')
            {
                _ = builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>面板边框与连接符（Spectre RoundedBorder / SquareBorder 用到的字符）</summary>
    private static readonly char[] BoxChars =
        ['│', '─', '╭', '╮', '╰', '╯', '┌', '┐', '└', '┘', '├', '┤', '┬', '┴', '┼'];

    /// <summary>
    /// 为「断言某段文案确实出现在面板里」准备文本：剥离 ANSI、去掉边框、去掉<b>全部</b>空白。
    /// <para>
    /// 为什么连空白都要去掉：Spectre 按显示宽度折行，跨行处会插入边框字符与缩进，
    /// 中文甚至可以在任意字符间断开（不插空格）。保留空白就要求子串恰好跨行方式一致，
    /// 那种断言会随终端宽度变化而飘。两侧都压掉空白后，折行完全不影响匹配。
    /// </para>
    /// <para>
    /// 不能用 <see cref="LettersDigitsAndSpaces"/> 代替：它只保留 ASCII 字母数字，
    /// 中文/日文/俄文文案会整段消失，断言随之静默失效。
    /// </para>
    /// </summary>
    public static string SquashForMatch(string text)
    {
        string plain = StripAnsi(text);
        var builder = new System.Text.StringBuilder(plain.Length);

        foreach (char c in plain)
        {
            if (char.IsWhiteSpace(c) || Array.IndexOf(BoxChars, c) >= 0)
            {
                continue;
            }

            _ = builder.Append(c);
        }

        return builder.ToString();
    }
}
