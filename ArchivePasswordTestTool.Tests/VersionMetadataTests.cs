using System.Reflection;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 版本号注入链路：唯一真相源是 git tag，构建时经 MSBuild 写入程序集元数据，
/// 源码里没有版本常量。这里锁定两个容易静默失效的契约。
/// </summary>
public sealed class VersionMetadataTests
{
    [Fact]
    public void VersionHasExactlyThreeSegments()
    {
        // Upgrade.IsNewerVersion 在版本段数与线上 tag 不一致时直接返回 false
        //（"段数不同无法比较，视为不更新"）——这是静默失效：程序照常运行，
        // 但所有用户永远收不到更新提示。段数必须固定三段。
        Assert.Equal(3, Program.Version.Length);
        Assert.All(Program.Version, segment => Assert.True(segment >= 0));
    }

    [Fact]
    public void BuildInjectsVersionMetadata()
    {
        // 元数据缺失时 ResolveMetadata 会回退到固定默认值，程序不会崩，
        // 但版本号会恒定不变 —— 用反射确认 csproj 的注入确实生效
        var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();

        Assert.Contains(metadata, item => item.Key == "AppVersion" && !string.IsNullOrWhiteSpace(item.Value));
        Assert.Contains(metadata, item => item.Key == "AppVersionType" && !string.IsNullOrWhiteSpace(item.Value));
    }

    [Fact]
    public void VersionMatchesInjectedMetadata()
    {
        // Program.Version 必须真的来自元数据，而不是残留的硬编码常量
        string injected = typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(item => item.Key == "AppVersion")
            .Value!;

        var segments = injected.Split('.').Select(int.Parse).ToArray();
        Assert.Equal(segments, Program.Version);
    }

    [Fact]
    public void VersionTypeIsNotBlank()
    {
        Assert.False(string.IsNullOrWhiteSpace(Program.VersionType));
    }

    [Fact]
    public void DevelopmentVersionTypeIsExplicitMarkerNotNumericGuess()
    {
        // 开发构建的判定必须依据显式类型标记，而不是「版本号是否为 0」：
        // 后者在版本元数据意外缺失时会把发布版误判成开发版，静默关闭更新检查
        Assert.Equal("Dev", Program.DevelopmentVersionType);

        // 默认构建（未注入）的 VersionType 必须是该标记
        bool injected = typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(item => item.Key == "AppVersionType" && item.Value != Program.DevelopmentVersionType);

        if (!injected)
        {
            Assert.Equal(Program.DevelopmentVersionType, Program.VersionType);
        }
    }
}
