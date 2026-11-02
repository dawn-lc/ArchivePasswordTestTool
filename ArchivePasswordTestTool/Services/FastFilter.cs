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
    /// 快速筛选：对每个候选密码只做「RAR3 密钥派生 + 解密首块 + 格式签名比对」，
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
        /// 命中候选数达到该值即停止扫描。签名误判率约 2^-32，
        /// 8 个误报连续出现在真密码之前的概率可忽略；限制上限是为了
        /// 在筛选逻辑本身有 bug 时不会把所有密码都变成候选。
        /// </summary>
        private const int MaxCandidates = 8;

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
            Rar4StoreProbe probe,
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

            Parallel.For(0, dictionary.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, (_, state) =>
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
                            Interlocked.Increment(ref found);
                            break;

                        case PasswordProbeResult.Unsupported:
                            unsupported.Add(password);
                            break;
                    }
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
