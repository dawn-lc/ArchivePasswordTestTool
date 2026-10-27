namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 更新检查纯函数单测（无网络）：版本比较（tag 格式 "x.y.z-Type"）、
/// 请求节流判定、每版本只提示一次的判定，以及待应用更新的落盘约定契约。
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
    public void IsNewerVersionComparesNumbersAndType(string tag, bool expected)
    {
        Assert.Equal(expected, Upgrade.IsNewerVersion(tag, [2, 0, 0], "Release"));
    }

    [Fact]
    public void IsNewerVersionUsesTypeHierarchyForSameVersion()
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
    public void IsNewerVersionIsConservativeOnInvalidTags(string tag, bool expected)
    {
        Assert.Equal(expected, Upgrade.IsNewerVersion(tag, [2, 0, 0], "Release"));
    }

    [Fact]
    public void IsNewerVersionTreatsUnknownTypeAsLowest()
    {
        // 未识别的类型按最低档：任何识别类型都更新（宁可信其有）
        Assert.True(Upgrade.IsNewerVersion("2.0.0-Whatever", [2, 0, 0], "Release"));
    }

    [Fact]
    public void IsNewerVersionMatchesRealGitHubTag()
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
    /// 请求节流 1 小时：既避免每次启动都打 GitHub（未认证配额 60 次/小时），
    /// 又保证新版本发布后最多 1 小时就能被发现——早期 24 小时节流会让
    /// 「发布紧跟上次检查」的用户整整一天看不到提示。
    /// </summary>
    [Fact]
    public void CheckIsDueAfterOneHour()
    {
        var now = new DateTime(2025, 1, 1, 12, 0, 0);

        Assert.True(Upgrade.IsCheckDue(now.AddHours(-1), now));
        Assert.True(Upgrade.IsCheckDue(now.AddDays(-3), now));
        Assert.False(Upgrade.IsCheckDue(now.AddMinutes(-59), now));
        Assert.False(Upgrade.IsCheckDue(now, now));
    }

    [Fact]
    public void FirstRunIsAlwaysDue()
    {
        // DateTime.MinValue 是「从未检查过」的初始值
        Assert.True(Upgrade.IsCheckDue(DateTime.MinValue, DateTime.Now));
    }

    [Fact]
    public void ShouldPromptSkipsVersionAlreadyNotified()
    {
        // 同一版本只提示一次：这是把请求节流缩短到 1 小时而不重复骚扰的前提
        Assert.False(Upgrade.ShouldPrompt("2.1.0-Release", [2, 0, 0], "Release", "2.1.0-Release"));
        Assert.True(Upgrade.ShouldPrompt("2.1.0-Release", [2, 0, 0], "Release", "2.0.0-Release"));
        Assert.True(Upgrade.ShouldPrompt("2.1.0-Release", [2, 0, 0], "Release", null));
    }

    [Fact]
    public void ShouldPromptIgnoresTagCaseWhenDeduping()
    {
        Assert.False(Upgrade.ShouldPrompt("2.1.0-release", [2, 0, 0], "Release", "2.1.0-Release"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldPromptSkipsMissingTag(string? tag)
    {
        Assert.False(Upgrade.ShouldPrompt(tag, [2, 0, 0], "Release", null));
    }

    [Fact]
    public void ShouldPromptSkipsOlderVersion()
    {
        Assert.False(Upgrade.ShouldPrompt("1.9.9-Release", [2, 0, 0], "Release", null));
    }

    /// <summary>
    /// 待应用更新必须与主程序同目录：Windows 只允许「同卷」重命名，
    /// 放到临时目录（可能在别的盘）会让替换退化成复制+删除，不再是原子的。
    /// </summary>
    [Fact]
    public void PendingUpdateFilesSitNextToExecutable()
    {
        string executable = PendingUpdate.ExecutablePath;
        string folder = Path.GetDirectoryName(executable)!;

        Assert.Equal(executable + PendingUpdate.DownloadingSuffix, PendingUpdate.DownloadingPath);
        Assert.Equal(executable + PendingUpdate.ReadySuffix, PendingUpdate.ReadyPath);
        Assert.Equal(executable + PendingUpdate.BackupSuffix, PendingUpdate.BackupPath);

        Assert.Equal(folder, Path.GetDirectoryName(PendingUpdate.ReadyPath));
        Assert.Equal(folder, Path.GetDirectoryName(PendingUpdate.BackupPath));
    }

    [Fact]
    public void PendingUpdateSuffixesAreDistinct()
    {
        string[] suffixes =
        [
            PendingUpdate.DownloadingSuffix,
            PendingUpdate.ReadySuffix,
            PendingUpdate.BackupSuffix,
        ];

        Assert.Equal(suffixes.Length, suffixes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(suffixes, suffix => Assert.StartsWith(".", suffix, StringComparison.Ordinal));
    }

    /// <summary>
    /// 自我替换必须发生在启动路由之前：<see cref="PendingUpdate.TryApply"/> 返回 true 时
    /// 说明已换成新版本并拉起了新进程，当前进程要立刻退出——若排在路由之后，
    /// 旧版本会先把活干完，替换结果被无谓地推迟到再下一次启动。
    /// </summary>
    [Fact]
    public void PendingUpdateIsAppliedBeforeStartupRouting()
    {
        string programPath = Path.Combine(TestPaths.RepoRoot, "ArchivePasswordTestTool", "Program.cs");
        string source = File.ReadAllText(programPath);

        int applyIndex = source.IndexOf("PendingUpdate.TryApply(", StringComparison.Ordinal);
        int routeIndex = source.IndexOf("Instance.Execute(", StringComparison.Ordinal);

        Assert.True(applyIndex >= 0, "Program.cs 中未找到 PendingUpdate.TryApply 调用");
        Assert.True(routeIndex >= 0, "Program.cs 中未找到 Instance.Execute 调用");
        Assert.True(applyIndex < routeIndex, "自我替换必须排在启动路由之前");
    }
}