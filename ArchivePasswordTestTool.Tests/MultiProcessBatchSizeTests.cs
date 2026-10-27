namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 多进程批大小调度：批太大会让小字典只够少数工作进程瓜分（其余进程启动即空转退出），
/// 太小则管道往返与进度回调过于琐碎。这里锁定"每个进程都能分到活且负载大致均匀"。
/// </summary>
public sealed class MultiProcessBatchSizeTests
{
    [Fact]
    public void LargeDictionaryUsesMaxBatchToMinimizeRoundTrips()
    {
        // 字典远大于 进程数×上限：批大小饱和到上限附近（5000/20/4=62.5→63），
        // 大字典下与固定批实现行为等价
        Assert.InRange(MultiProcess.ResolveBatchSize(5000, 20), 60, 64);
        Assert.Equal(64, MultiProcess.ResolveBatchSize(100000, 20));
        Assert.Equal(64, MultiProcess.ResolveBatchSize(10000, 1));
    }

    [Fact]
    public void SmallDictionaryShrinksBatchSoEveryWorkerGetsWork()
    {
        // 300 个密码 / 20 进程：固定批 64 只有 5 个进程干活，其余 15 个空转
        int batch = MultiProcess.ResolveBatchSize(300, 20);

        Assert.True(batch < 64, $"批大小应小于上限，实际 {batch}");
        Assert.True(CeilDiv(300, batch) >= 20, $"批 {batch} 只够 {CeilDiv(300, batch)} 批，不足 20 个进程分配");
    }

    [Theory]
    [InlineData(256, 20)]
    [InlineData(300, 20)]
    [InlineData(600, 10)]
    [InlineData(1200, 20)]
    [InlineData(2000, 20)]
    [InlineData(5000, 20)]
    [InlineData(5000, 8)]
    [InlineData(100000, 32)]
    public void EveryWorkerReceivesAtLeastOneBatch(int passwordCount, int workerCount)
    {
        int batch = MultiProcess.ResolveBatchSize(passwordCount, workerCount);

        Assert.InRange(batch, 8, 64);
        Assert.True(
            CeilDiv(passwordCount, batch) >= workerCount,
            $"count={passwordCount} workers={workerCount} batch={batch} 批数={CeilDiv(passwordCount, batch)}");
    }

    [Fact]
    public void BatchStaysNearTargetBatchesPerWorker()
    {
        // 均衡粒度断言：每进程批数落在 [1, TargetBatchesPerWorker 上限 +1] 区间，
        // 避免出现"2 轮跑完后第 3 轮只剩一个进程干活"的尾巴
        foreach (var (count, workers) in new[] { (1200, 20), (2000, 20), (5000, 20), (300, 20) })
        {
            int batch = MultiProcess.ResolveBatchSize(count, workers);
            int batches = CeilDiv(count, batch);
            int perWorker = CeilDiv(batches, workers);

            Assert.InRange(perWorker, 1, 4);
        }
    }

    [Fact]
    public void DegenerateInputsFallBackToMinimumBatch()
    {
        Assert.Equal(8, MultiProcess.ResolveBatchSize(0, 20));
        Assert.Equal(8, MultiProcess.ResolveBatchSize(100, 0));
        Assert.Equal(8, MultiProcess.ResolveBatchSize(-5, -5));
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;
}
