namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 版本比较逻辑单测（无网络）：覆盖旧版 Utils.Upgrade 移植的
/// 数值段比较与版本类型等级比较，tag 格式 "x.y.z-Type"
/// </summary>
public class UpgradeTests
{
    [Theory]
    [InlineData("2.0.1-Release", true)]                       // 补丁号更高
    [InlineData("2.1.0-Release", true)]                       // 次版本号更高
    [InlineData("3.0.0-Release", true)]                       // 主版本号更高
    [InlineData("v2.1.0-Release", true)]                      // v 前缀剥离
    [InlineData("2.0.0", false)]                              // 无类型段，当前 release 平级
    [InlineData("2.0.0-Release", false)]                      // 完全相同
    [InlineData("1.9.9-Release", false)]                      // 更低
    [InlineData("2.0.0-Fixpush", true)]                       // 同版本号，热修类型更高 → 提示
    public void IsNewerVersion_NumericAndTypeComparison(string tag, bool expected)
    {
        Assert.Equal(expected, Upgrade.IsNewerVersion(tag, [2, 0, 0], "Release"));
    }

    [Fact]
    public void IsNewerVersion_SameVersionTypeHierarchy()
    {
        // 同版本号：类型等级 fixpush > final > release > preview > beta > alpha > test
        Assert.True(Upgrade.IsNewerVersion("2.0.0-Fixpush", [2, 0, 0], "Preview"));
        Assert.False(Upgrade.IsNewerVersion("2.0.0-Beta", [2, 0, 0], "Preview"));
        Assert.True(Upgrade.IsNewerVersion("2.0.0-Final", [2, 0, 0], "Preview"));
    }

    [Theory]
    [InlineData("2.0.0.1-Release", false)]                    // 段数不一致，保守不提示
    [InlineData("not-a-version", false)]                      // 无法解析
    [InlineData("", false)]                                   // 空串
    public void IsNewerVersion_InvalidTagsAreConservative(string tag, bool expected)
    {
        Assert.Equal(expected, Upgrade.IsNewerVersion(tag, [2, 0, 0], "Release"));
    }

    [Fact]
    public void IsNewerVersion_UnrecognizedTypeMeansUpgrade()
    {
        // 未识别的类型按最低档：任何识别类型都更新（宁可信其有）
        Assert.True(Upgrade.IsNewerVersion("2.0.0-Whatever", [2, 0, 0], "Release"));
    }

    [Fact]
    public void IsNewerVersion_RealGitHubTag()
    {
        // 仓库实际最新 Release tag 形如 "1.5.8-Release"
        Assert.True(Upgrade.IsNewerVersion("1.5.8-Release", [1, 0, 0], "Release"));
        Assert.False(Upgrade.IsNewerVersion("1.5.8-Release", [2, 0, 0], "Release"));
    }

    /// <summary>
    /// 更新提示必须先于「是否保存测试结果」确认被消费。
    /// 后者包含两个阻塞式确认（保存文件、打开报告），用户若停在那里或直接关窗口，
    /// 排在后面的更新提示就再也没机会出现——这是实际被用户撞到的问题。
    /// </summary>
    [Fact]
    public void UpdatePromptIsConsumedBeforeSaveResultConfirmation()
    {
        string runnerPath = Path.Combine(
            TestPaths.RepoRoot, "ArchivePasswordTestTool", "Services", "Runner.cs");
        string source = File.ReadAllText(runnerPath);

        int updateIndex = source.IndexOf("Upgrade.CompleteBackgroundCheck()", StringComparison.Ordinal);
        int saveIndex = source.IndexOf("ResultsWriter.WriteIfRequested(", StringComparison.Ordinal);

        Assert.True(updateIndex >= 0, "Runner.cs 中未找到 Upgrade.CompleteBackgroundCheck() 调用");
        Assert.True(saveIndex >= 0, "Runner.cs 中未找到 ResultsWriter.WriteIfRequested 调用");
        Assert.True(
            updateIndex < saveIndex,
            "更新提示必须排在保存结果确认之前，否则用户卡在保存确认时看不到更新提示");
    }

    /// <summary>
    /// 「检查时间戳」必须由消费端（CompleteBackgroundCheck）写入，不能在后台请求内部写入。
    /// 否则请求虽成功、但本次来不及展示（快速测试几百毫秒就结束），时间戳却已落下，
    /// 用户要再等满 24 小时才可能看到提示。
    /// </summary>
    [Fact]
    public void CheckTimestampIsWrittenByConsumerNotInsideBackgroundTask()
    {
        string upgradePath = Path.Combine(
            TestPaths.RepoRoot, "ArchivePasswordTestTool", "Services", "Upgrade.cs");
        string source = File.ReadAllText(upgradePath);

        int startMethod = source.IndexOf("public static void StartBackgroundCheck()", StringComparison.Ordinal);
        int completeMethod = source.IndexOf("public static void CompleteBackgroundCheck()", StringComparison.Ordinal);

        Assert.True(startMethod >= 0 && completeMethod >= 0, "未找到 StartBackgroundCheck / CompleteBackgroundCheck");
        Assert.True(startMethod < completeMethod, "两个方法顺序异常");

        string startBody = source[startMethod..completeMethod];
        Assert.DoesNotContain("SaveConfig", startBody);
    }
}