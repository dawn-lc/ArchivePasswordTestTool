namespace ArchivePasswordTestTool
{
    /// <summary>
    /// <c>APTT_PARALLELISM</c>：进程内并行度的用户上限（环境变量属外部契约，名字不随代码风格改）。
    /// <para>
    /// 三个消费点共用同一解释：<see cref="Planner"/>（全量解密的线程数）、
    /// <see cref="Runner"/>（快速筛选的线程数）、<see cref="MultiProcess"/>（工作进程数）。
    /// 各自手写一遍解析时，任何一处漏判都会让「用户限制了并发」在一条路径上失效。
    /// </para>
    /// </summary>
    internal static class ParallelismLimit
    {
        private const string EnvVar = "APTT_PARALLELISM";

        /// <summary>用户指定的上限；未设置、非法或非正数时返回 <see langword="null" /></summary>
        internal static int? FromEnvironment =>
            int.TryParse(Environment.GetEnvironmentVariable(EnvVar), out int value) && value > 0
                ? value
                : null;

        /// <summary>把上限施加到默认并行度上：只收紧，不放大</summary>
        public static int Apply(int parallelism) =>
            FromEnvironment is { } cap ? Math.Min(parallelism, cap) : parallelism;
    }
}
