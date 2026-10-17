namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// TryGetVolumePaths 单元测试：不依赖压缩包样本，用临时文件验证分卷收集规则。
/// </summary>
public sealed class VolumePathTests : IDisposable
{
    private readonly string _dir;

    public VolumePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ApttVol{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private string Volume(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3]); // 内容不重要，只验证存在性与命名规则
        return path;
    }

    [Fact]
    public void TwoVolumesAreLinkedInOrder()
    {
        Volume("a.7z.001");
        Volume("a.7z.002");

        bool linked = ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "a.7z.001"), out var paths);

        Assert.True(linked);
        Assert.Equal(2, paths.Count);
        Assert.EndsWith("a.7z.001", paths[0]);
        Assert.EndsWith("a.7z.002", paths[1]);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void ThreeDigitSuffixIsSupported()
    {
        Volume("b.7z.001");
        Volume("b.7z.002");
        Volume("b.7z.010");

        // 仅收集连续分卷：.003 缺失，.010 不应出现
        bool linked = ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "b.7z.001"), out var paths);

        Assert.True(linked);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void SingleVolumeIsNotLinked()
    {
        Volume("c.7z.001");

        Assert.False(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "c.7z.001"), out var paths));
        Assert.Empty(paths);
    }

    [Fact]
    public void MidVolumeIsRejected()
    {
        Volume("d.7z.001");
        Volume("d.7z.002");

        // 从中间分卷无法拼出完整流，必须从 .001 开始
        Assert.False(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "d.7z.002"), out _));
    }

    [Fact]
    public void MissingFirstVolumeIsRejected()
    {
        Volume("e.7z.002");
        Volume("e.7z.003");

        Assert.False(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "e.7z.001"), out _));
    }

    [Fact]
    public void PlainArchiveNameWithoutVolumeSuffixIsRejected()
    {
        Volume("plain.7z");

        Assert.False(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "plain.7z"), out _));
    }

    [Theory]
    [InlineData("f.7z.1")]     // 1 位序号
    [InlineData("f.7z.0001")]  // 4 位序号
    [InlineData("f.7z.00x")]   // 非数字
    public void InvalidVolumeSuffixIsRejected(string name)
    {
        Volume(name);
        Volume("f.7z.002");

        Assert.False(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, name), out _));
    }

    [Fact]
    public void NameWithoutExtensionIsRejected()
    {
        string path = Path.Combine(_dir, "nodot");
        File.WriteAllBytes(path, [1]);

        Assert.False(ArchiveContext.TryGetVolumePaths(path, out _));
    }
}
