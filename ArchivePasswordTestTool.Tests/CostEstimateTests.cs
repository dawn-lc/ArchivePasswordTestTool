namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 慢速完整解码的耗时估算。
/// <para>
/// 场景：7z 用 Copy(不压缩) 编码且未加密文件名时，密文没有可校验结构，
/// 每个错误密码都要解完整个数据才能靠 CRC 判错。实测 1.2GB + 250 万字典约 100 小时，
/// 且加并行度无效。估算只用于**提前预警**，不参与判定，所以宁可偏乐观。
/// </para>
/// </summary>
public sealed class CostEstimateTests
{
    private const ulong Mb = 1024UL * 1024UL;
    private const ulong Gb = 1024UL * Mb;

    [Fact]
    public void ZeroInputsYieldZero()
    {
        Assert.Equal(TimeSpan.Zero, CostEstimate.Estimate(0, Gb));
        Assert.Equal(TimeSpan.Zero, CostEstimate.Estimate(1_000_000, 0));
    }

    /// <summary>
    /// 1.2GB × 250 万字典：按 2 GB/s 的乐观吞吐约 24 天。
    /// 实测默认并行度约 100 小时（本机更快），因此断言必须落在"远超一天"量级上——
    /// 估算值偏大是刻意为之（宁可让用户重视），但不能离谱到失去参考意义。
    /// </summary>
    [Fact]
    public void LargeStoreArchiveEstimateIsManyDays()
    {
        var estimate = CostEstimate.Estimate(2_500_000, 1_200 * Mb);

        Assert.True(estimate.TotalDays > 10, $"预计 {estimate.TotalDays:F1} 天，应远超一天");
        Assert.True(CostEstimate.ShouldWarn(estimate));
    }

    /// <summary>小字典不应打扰用户：200MB × 2000 条约 3 分钟，低于一小时阈值</summary>
    [Fact]
    public void SmallDictionaryDoesNotWarn()
    {
        var estimate = CostEstimate.Estimate(2000, 200 * Mb);

        Assert.True(estimate < TimeSpan.FromHours(1));
        Assert.False(CostEstimate.ShouldWarn(estimate));
    }

    /// <summary>阈值边界：正好一小时应当预警（"约需一小时"已经值得告知）</summary>
    [Fact]
    public void OneHourIsWarned()
    {
        Assert.True(CostEstimate.ShouldWarn(TimeSpan.FromHours(1)));
        Assert.False(CostEstimate.ShouldWarn(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(512UL, "512 B")]
    [InlineData(2048UL, "2 KB")]
    [InlineData(5UL * 1024 * 1024, "5 MB")]
    [InlineData(1536UL * 1024 * 1024, "1.5 GB")]
    public void FormatBytesUsesReadableUnits(ulong bytes, string expected)
    {
        Assert.Equal(expected, CostEstimate.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(30, "30 min")]
    [InlineData(90, "1.5 h")]
    [InlineData(60 * 24, "1 d 0 h")]
    public void FormatDurationPicksLargestUnit(int minutes, string expected)
    {
        Assert.Equal(expected, CostEstimate.FormatDuration(TimeSpan.FromMinutes(minutes)));
    }

    /// <summary>不足一分钟的估算不应显示成 "0 min"（会让人以为不用等）</summary>
    [Fact]
    public void SubMinuteDurationRoundsUpToOneMinute()
    {
        Assert.Equal("1 min", CostEstimate.FormatDuration(TimeSpan.FromSeconds(5)));
    }
}
