using ArchivePasswordTestTool.Properties;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 结果文件（<c>&lt;压缩包&gt;[results].txt</c>）的密码行与保存询问条件。
/// <para>
/// 缺陷背景：判据曾用「归档是否加密」守 whole 保存询问，导致未加密的包
/// 连报告都不问（用户实测报告）。而报告的价值不止密码——还有哈希、用时，
/// 以及「未加密」这个结论，是「我检查过这个包」的凭证。
/// </para>
/// </summary>
public class ResultsWriterTests
{
    [Fact]
    public void NotEncryptedArchiveReportsNotEncrypted()
    {
        // 未加密必须单列一态：它的 password 是空串，按「空即未找到」会误导
        Assert.Equal(
            Resources.TestResultArchiveNotEncrypted,
            ResultsWriter.BuildPasswordLine(null, archiveEncrypted: false));
        Assert.Equal(
            Resources.TestResultArchiveNotEncrypted,
            ResultsWriter.BuildPasswordLine(string.Empty, archiveEncrypted: false));
    }

    [Fact]
    public void NotEncryptedIgnoresUnexpectedPassword()
    {
        // 即使调用方意外传了密码，未加密包也不该写出密码行：
        // 说明归档没加密，密码无从谈起
        Assert.Equal(
            Resources.TestResultArchiveNotEncrypted,
            ResultsWriter.BuildPasswordLine("whatever", archiveEncrypted: false));
    }

    [Fact]
    public void EncryptedArchiveWritesFoundPassword()
    {
        const string Password = "Correct123!";

        Assert.Equal(
            string.Format(Resources.TestResultDecompressionPassword, Password),
            ResultsWriter.BuildPasswordLine(Password, archiveEncrypted: true));
    }

    [Fact]
    public void EncryptedArchiveWithoutPasswordReportsNotFound()
    {
        Assert.Equal(
            Resources.TestResultPasswordNotFound,
            ResultsWriter.BuildPasswordLine(null, archiveEncrypted: true));
        Assert.Equal(
            Resources.TestResultPasswordNotFound,
            ResultsWriter.BuildPasswordLine(string.Empty, archiveEncrypted: true));
    }

    [Fact]
    public void PasswordIsNeverWrittenAsEmptyLine()
    {
        foreach (var (password, encrypted) in new (string?, bool)[]
                 {
                     (null, true),
                     (string.Empty, true),
                     (null, false),
                     (string.Empty, false),
                 })
        {
            string line = ResultsWriter.BuildPasswordLine(password, encrypted);

            Assert.False(
                string.IsNullOrWhiteSpace(line),
                $"密码行为空（password={password ?? "<null>"}, encrypted={encrypted}）");
        }
    }

    /// <summary>
    /// 保存询问的判据必须是「归档是否已成功探测」，不是「是否加密」。
    /// 用加密与否守卫会让未加密包完全不留报告（实际被用户撞到的回归）。
    /// </summary>
    [Fact]
    public void SavePromptIsGatedByInspectionNotEncryption()
    {
        string runnerPath = Path.Combine(
            TestPaths.RepoRoot, "ArchivePasswordTestTool", "Services", "Runner.cs");
        string source = File.ReadAllText(runnerPath);

        int callIndex = source.IndexOf("ResultsWriter.WriteIfRequested(", StringComparison.Ordinal);
        Assert.True(callIndex >= 0, "Runner.cs 中未找到 ResultsWriter.WriteIfRequested 调用");

        // 取调用前最近的 if 守卫行
        int guardStart = source.LastIndexOf("if (", callIndex, StringComparison.Ordinal);
        Assert.True(guardStart >= 0, "未找到 WriteIfRequested 的守卫条件");

        string guard = source[guardStart..callIndex];

        Assert.Contains("archiveInspected", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("archiveEncrypted", guard, StringComparison.Ordinal);
    }
}
