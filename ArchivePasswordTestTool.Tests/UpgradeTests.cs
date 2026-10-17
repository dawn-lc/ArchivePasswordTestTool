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
}
