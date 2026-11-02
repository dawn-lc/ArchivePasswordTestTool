using System.Collections.Concurrent;

namespace ArchivePasswordTestTool
{
    /// <summary>快速筛选结果</summary>
    /// <param name="Candidates">通过格式签名、需要用 7-Zip 权威确认的候选密码</param>
    /// <param name="Unsupported">超出快速路径适用范围、必须直接完整测密的密码（如超长密码）</param>
    /// <param name="Completed">是否扫描完整个字典（false 表示命中候选后提前停止，实测计数不完整）</param>
    internal readonly record struct FastFilterResult(
        List<string> Candidates,
        List<string> Unsupported,
        bool Completed);

    /// <summary>
    /// 快速筛选：对每个候选密码只做「RAR4 密钥派生 + 解密首块 + 格式签名比对」，
    /// 每候选约 10ms（硬件 SHA1），不读 1.2GB 数据。
    /// <para>
    /// 它只负责排除错误密码。命中者必须由 7-Zip 完整解包确认——
    /// 筛选结果永远不作为"找到密码"的依据。
    /// </para>
    /// </summary>
    internal static class FastFilter
    {
        /// <summary>
        /// 诊断开关：置 1 时关闭快速筛选，退回完整测密（排查筛选逻辑怀疑误判时用）
        /// </summary>
        private const string DisableEnvVar = "APTT_NO_FAST_FILTER";

        /// <summary>
        /// 单轮最多收集的候选数，达到即停止本轮扫描（调用方确认完会接着扫剩余部分）。
        /// <para>
        /// 全错字典下每个候选的误报率：兜底签名表里最弱的是 3 字节魔数（2⁻²⁴，
        /// 共 4 个：JPEG / FLV / bzip2 / SWF），其余四十多个都是 4 字节以上，
        /// 整体约 3×10⁻⁷。250 万候选的期望误报不到 1 次，
        /// 取 64 意味着「一轮就凑满」需要约 10⁸ 量级候选，实际上不会触发；
        /// 留这个上限是为了在筛选逻辑本身出 bug（比如签名表被改坏、恒定命中）时，
        /// 不会把整个字典都变成需要完整解密的候选。
        /// </para>
        /// </summary>
        private const int MaxCandidates = 64;

        /// <summary>进度汇报粒度：每测这么多条回调一次，避免每密码刷新一次进度条</summary>
        private const int ProgressBatch = 64;

        public static bool IsEnabled => Environment.GetEnvironmentVariable(DisableEnvVar) != "1";

        /// <summary>
        /// 并行筛选整个字典（消费式，与完整测密共用 <see cref="PasswordDictionary.Pop"/>）。
        /// 密钥派生是纯 CPU 负载且 SMT 有效（实测 16 线程 650/秒 vs 8 线程 499/秒），
        /// 因此这里用逻辑核数而非物理核数。
        /// </summary>
        /// <param name="probe">已建立的快速判错器</param>
        /// <param name="dictionary">密码字典（会被消费）</param>
        /// <param name="parallelism">并行度</param>
        /// <param name="onProgress">进度回调（参数为新增的已测条数）</param>
        public static FastFilterResult Run(
            IPasswordProbe probe,
            PasswordDictionary dictionary,
            int parallelism,
            Action<int> onProgress)
        {
            var candidates = new ConcurrentBag<string>();
            var unsupported = new ConcurrentBag<string>();
            int found = 0;
            int pending = 0;

            void Report()
            {
                if (Interlocked.Increment(ref pending) >= ProgressBatch)
                {
                    int delta = Interlocked.Exchange(ref pending, 0);
                    if (delta > 0)
                    {
                        onProgress(delta);
                    }
                }
            }

            _ = Parallel.For(0, dictionary.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, (_, state) =>
            {
                if (Volatile.Read(ref found) >= MaxCandidates)
                {
                    state.Stop();
                    return;
                }

                string? password = dictionary.Pop();
                if (password is null)
                {
                    return;
                }

                try
                {
                    switch (probe.Probe(password))
                    {
                        case PasswordProbeResult.Match:
                            candidates.Add(password);
                            _ = Interlocked.Increment(ref found);
                            break;

                        case PasswordProbeResult.Unsupported:
                            unsupported.Add(password);
                            break;
                    }
                }
                catch (Exception)
                {
                    // Probe 的契约是「不抛异常」，但真抛了（如 CNG 句柄耗尽）
                    // 也必须保守地退回完整测密，而不是让整个任务失败或丢弃候选
                    unsupported.Add(password);
                }
                finally
                {
                    Report();
                }
            });

            // 冲刷最后不足一批的计数
            int rest = Interlocked.Exchange(ref pending, 0);
            if (rest > 0)
            {
                onProgress(rest);
            }

            return new FastFilterResult(
                [.. candidates],
                [.. unsupported],
                Completed: Volatile.Read(ref found) < MaxCandidates);
        }
    }
}
