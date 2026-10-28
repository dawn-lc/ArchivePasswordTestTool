namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 内嵌 7z.dll 的提取逻辑测试。
/// <para>
/// 内嵌的意义是「没装 7-Zip 也能用」，因此这里守护两件事：
/// ① 资源真的被嵌进程序集（构建期下载链路生效）；
/// ② 解压落地结果与资源逐字节同尺寸（写半截/写错文件都会立刻红）。
/// </para>
/// <para>
/// 提取出的 dll **能否真正加载并破解**由 EndToEndTests 里的进程级用例负责——
/// 本进程内 P/Invoke "7z.dll" 按名字绑定，测试宿主机若已加载过别的副本就测不准。
/// </para>
/// </summary>
public sealed class EmbeddedSevenZipTests
{
    private static long ResourceLength(string name)
    {
        // 直接取主程序集：测试项目通过 InternalsVisibleTo 能访问 internal 成员
        using var resource = typeof(ArchivePasswordTestTool.EmbeddedSevenZip).Assembly
            .GetManifestResourceStream(name);

        Assert.NotNull(resource);
        return resource!.Length;
    }

    [Fact]
    public void EmbeddedSevenZipIsPresentInAssembly()
    {
        Assert.True(
            ArchivePasswordTestTool.EmbeddedSevenZip.IsAvailable,
            "程序集里没有内嵌 7z.dll：构建期的 EnsureSevenZipDllAsset 未生效（检查 tools\\7zip\\ 或网络）");
    }

    [Fact]
    public void EmbeddedLicenseIsPresentInAssembly()
    {
        // 7-Zip 是 LGPL，官方 license.txt 要求二进制再分发必须附带许可证信息
        Assert.True(
            ResourceLength(ArchivePasswordTestTool.EmbeddedSevenZip.LicenseResourceName) > 0,
            "程序集里没有内嵌 7-Zip 许可证：再分发不合规");
    }

    [Fact]
    public void TryExtractDllWritesResourceWithExactSize()
    {
        long expected = ResourceLength(ArchivePasswordTestTool.EmbeddedSevenZip.DllResourceName);

        string? path = ArchivePasswordTestTool.EmbeddedSevenZip.TryExtractDll();

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"未落地：{path}");
        Assert.Equal(expected, new FileInfo(path!).Length);

        // 落地位置必须在用户缓存目录，不能写程序目录（可能只读）
        Assert.StartsWith(
            Path.GetFullPath(ArchivePasswordTestTool.EmbeddedSevenZip.CacheDirectory),
            Path.GetFullPath(path!),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryExtractDllAlsoWritesLicenseNextToDll()
    {
        string? path = ArchivePasswordTestTool.EmbeddedSevenZip.TryExtractDll();
        Assert.NotNull(path);

        string license = Path.Combine(Path.GetDirectoryName(path!)!, "7z-License.txt");
        Assert.True(File.Exists(license), $"许可证未落地：{license}");
        Assert.True(new FileInfo(license).Length > 0);
    }

    [Fact]
    public void SecondCallReusesAlreadyExtractedFile()
    {
        string? first = ArchivePasswordTestTool.EmbeddedSevenZip.TryExtractDll();
        Assert.NotNull(first);

        var stamp = File.GetLastWriteTimeUtc(first!);

        string? second = ArchivePasswordTestTool.EmbeddedSevenZip.TryExtractDll();

        Assert.Equal(first, second);
        // 命中缓存时不应重写文件（否则每次启动都白写 1.9MB）
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second!));
    }

    [Fact]
    public void NoTemporaryFilesAreLeftBehind()
    {
        ArchivePasswordTestTool.EmbeddedSevenZip.TryExtractDll();

        var leftovers = Directory
            .EnumerateFiles(ArchivePasswordTestTool.EmbeddedSevenZip.CacheDirectory, "*.tmp")
            .ToList();

        Assert.Empty(leftovers);
    }

    [Fact]
    public void ConcurrentExtractionIsSafe()
    {
        // 多进程测密会同时拉起 N 个工作进程，它们几乎同时走到提取逻辑
        const int Workers = 8;
        long expected = ResourceLength(ArchivePasswordTestTool.EmbeddedSevenZip.DllResourceName);

        var results = new string?[Workers];

        Parallel.For(0, Workers, index =>
        {
            results[index] = ArchivePasswordTestTool.EmbeddedSevenZip.TryExtractDll();
        });

        Assert.All(results, path => Assert.NotNull(path));
        // 所有并发调用必须收敛到同一个落地文件，不能各自写一份
        Assert.Single(results.Distinct(StringComparer.OrdinalIgnoreCase));

        string dll = results[0]!;
        Assert.Equal(expected, new FileInfo(dll).Length);
        Assert.Empty(Directory.EnumerateFiles(ArchivePasswordTestTool.EmbeddedSevenZip.CacheDirectory, "*.tmp"));
    }
}
