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
    [InlineData("f.7z.1")]     // 1 位序号：无法与普通扩展名区分，7-Zip 同样拒绝
    [InlineData("f.7z.00x")]   // 非数字
    [InlineData("f.7z.002")]   // 首卷编号必须是 0 或 1，.002 从中间开始拼不出完整流
    public void InvalidVolumeSuffixIsRejected(string name)
    {
        Volume(name);
        Volume("f.7z.003");

        Assert.False(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, name), out _));
    }

    /// <summary>
    /// 四位卷号（<c>.0001</c>）合法。旧实现对位宽设了 2—3 位的上限，
    /// 而 7-Zip 的 <c>CSeqName</c> 只要求「整个扩展名都是数字且长度 ≥ 2」，位宽不限。
    /// </summary>
    [Fact]
    public void FourDigitSuffixIsSupported()
    {
        Volume("g.7z.0001");
        Volume("g.7z.0002");
        Volume("g.7z.0003");

        Assert.True(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "g.7z.0001"), out var paths));
        Assert.Equal(3, paths.Count);
        Assert.EndsWith("g.7z.0003", paths[2]);
    }

    /// <summary>
    /// 从 0 开始的分卷序列也要认：Unix <c>split -d</c> 生成的就是 <c>.00</c>、<c>.01</c>…。
    /// 7-Zip 的判据是「末两位为 00 或 01」，两者都接受。
    /// </summary>
    [Fact]
    public void ZeroBasedSuffixIsSupported()
    {
        Volume("h.7z.00");
        Volume("h.7z.01");
        Volume("h.7z.02");

        Assert.True(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "h.7z.00"), out var paths));
        Assert.Equal(3, paths.Count);
        Assert.EndsWith("h.7z.00", paths[0]);
        Assert.EndsWith("h.7z.02", paths[2]);
    }

    /// <summary>
    /// 卷号超出位宽时自然变长：<c>.99</c> 的下一卷是 <c>.100</c>，不是 <c>.100</c> 被截成 <c>.00</c>。
    /// 7-Zip 的 <c>CSeqName::GetNextName</c> 在最高位进位时插入 <c>'1'</c>，行为一致。
    /// 这条同时锁住「缺卷检测不被位宽变化弄坏」：001—099 连续，100 是第 100 卷。
    /// </summary>
    [Fact]
    public void VolumeNumberWidensWhenItOverflows()
    {
        for (int i = 1; i <= 100; i++)
        {
            // D2 对 i=100 自然输出三位，正好是位宽增长的那一刻
            Volume($"i.7z.{i:D2}");
        }

        // 上面循环会生成 .01—.99 与 .100（D2 对 100 自然变成三位）
        Assert.True(File.Exists(Path.Combine(_dir, "i.7z.01")));
        Assert.True(File.Exists(Path.Combine(_dir, "i.7z.99")));
        Assert.True(File.Exists(Path.Combine(_dir, "i.7z.100")));

        Assert.True(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "i.7z.01"), out var paths));
        Assert.Equal(100, paths.Count);
        Assert.EndsWith("i.7z.100", paths[^1]);

        Assert.False(ArchiveContext.TryFindMissingVolume(Path.Combine(_dir, "i.7z.01"), paths, out _));
    }

    /// <summary>位宽变化处缺卷也必须被发现：<c>.99</c> 之后直接是 <c>.101</c>，缺的就是 <c>.100</c></summary>
    [Fact]
    public void MissingVolumeIsDetectedAcrossWidthChange()
    {
        for (int i = 1; i <= 99; i++)
        {
            Volume($"j.7z.{i:D2}");
        }

        Volume("j.7z.101");

        Assert.True(ArchiveContext.TryGetVolumePaths(Path.Combine(_dir, "j.7z.01"), out var paths));
        Assert.Equal(99, paths.Count);

        Assert.True(ArchiveContext.TryFindMissingVolume(Path.Combine(_dir, "j.7z.01"), paths, out string missing));
        Assert.EndsWith("j.7z.100", missing);
    }

    [Fact]
    public void NameWithoutExtensionIsRejected()
    {
        string path = Path.Combine(_dir, "nodot");
        File.WriteAllBytes(path, [1]);

        Assert.False(ArchiveContext.TryGetVolumePaths(path, out _));
    }
}
