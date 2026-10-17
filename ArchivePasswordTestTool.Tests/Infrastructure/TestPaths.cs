namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 仓库路径与构建产物路径解析。
/// dotnet test 会先通过 ProjectReference 构建主项目，因此 exe 在断言存在性前必然已生成。
/// </summary>
public static class TestPaths
{
    /// <summary>仓库根目录（包含 ArchivePasswordTestTool 与 TestData 两个目录）</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>测试样本目录</summary>
    public static string TestDataDir => Path.Combine(RepoRoot, "TestData");

    /// <summary>当前测试构建配置（Debug / Release），主程序使用同一配置产物</summary>
    public static string ConfigName { get; } =
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name switch
        {
            "Debug" => "Debug",
            "Release" => "Release",
            var other => throw new InvalidOperationException(
                $"无法识别配置目录：{other}（{AppContext.BaseDirectory}）")
        };

    /// <summary>主程序 exe 路径</summary>
    public static string MainExePath => Path.Combine(
        RepoRoot, "ArchivePasswordTestTool", "bin", ConfigName, "net10.0", "ArchivePasswordTestTool.exe");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArchivePasswordTestTool", "ArchivePasswordTestTool.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"未找到仓库根目录（沿 {AppContext.BaseDirectory} 向上未找到 ArchivePasswordTestTool/ArchivePasswordTestTool.csproj）");
    }
}
