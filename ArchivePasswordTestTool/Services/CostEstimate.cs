namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 测密耗时估算（仅用于预警，不参与任何判定逻辑）。
    /// <para>
    /// 场景：7z 使用 Copy(不压缩) 编码且未加密文件名时，密文里没有任何可用的
    /// 判据——错误密钥解出的垃圾数据与正常数据无法区分，7-Zip 只能解到文件尾
    /// 靠文件 CRC 判错。实测 200MB 单文件每个候选读满 126MB，2000 条字典 55 秒；
    /// 外推到 1.2GB + 250 万字典约 100 小时。这种情况下增加并行度也没用：
    /// 瓶颈从 KDF 锁转移到了内存带宽/IO（实测 16 进程与 8 线程同为 36 个/秒）。
    /// </para>
    /// <para>
    /// 估算刻意用**偏保守（偏快）**的吞吐，避免把正常场景吓成"预计要跑几小时"；
    /// 只有当连最乐观估计都超过一小时才提示。
    /// </para>
    /// </summary>
    internal static class CostEstimate
    {
        /// <summary>
        /// 保守的解码吞吐（字节/秒）。本机实测：200MB 随机数据 Copy+7zAES，
        /// 8 线程 36 个/秒 → 7.2 GB/s（页缓存命中）；1.2GB RAR4 优化前
        /// 1.9 个/秒 → 2.1 GB/s（真实磁盘读）。取低端值 2 GB/s 作为乐观上界，
        /// 实际机械硬盘/低配机器只会更慢。
        /// </summary>
        private const double OptimisticDecodeBytesPerSecond = 2.0 * 1024 * 1024 * 1024;

        /// <summary>低于这个预计耗时就不提示——短时间的等待不需要打扰用户</summary>
        private static readonly TimeSpan WarnThreshold = TimeSpan.FromHours(1);

        /// <summary>
        /// 估算总耗时。数据量为 0 或字典为空时返回 <see cref="TimeSpan.Zero"/>。
        /// </summary>
        public static TimeSpan Estimate(long passwordCount, ulong bytesPerPassword)
        {
            if (passwordCount <= 0 || bytesPerPassword == 0)
            {
                return TimeSpan.Zero;
            }

            double seconds = passwordCount * (double)bytesPerPassword / OptimisticDecodeBytesPerSecond;

            // 超过 TimeSpan 上限（约 29247 年）时钳制，避免构造异常
            return seconds >= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromSeconds(seconds);
        }

        /// <summary>是否值得提示用户（预计耗时超过阈值）</summary>
        public static bool ShouldWarn(TimeSpan estimated) => estimated >= WarnThreshold;

        /// <summary>
        /// 人类可读的大小（"1.2 GB" / "126 MB"）。单位是国际通用缩写，不做本地化。
        /// </summary>
        public static string FormatBytes(ulong bytes)
        {
            const double Kb = 1024;
            const double Mb = Kb * 1024;
            const double Gb = Mb * 1024;
            const double Tb = Gb * 1024;

            double value = bytes;
            if (bytes >= Tb)
            {
                return $"{value / Tb:0.##} TB";
            }

            if (bytes >= Gb)
            {
                return $"{value / Gb:0.##} GB";
            }

            if (bytes >= Mb)
            {
                return $"{value / Mb:0.##} MB";
            }

            return bytes >= Kb ? $"{value / Kb:0.##} KB" : $"{bytes} B";
        }

        /// <summary>
        /// 人类可读的时长。与结果面板一致地使用 <see cref="TimeSpan"/> 的默认格式
        /// （"1.02:30:00"），但把超过一天的部分改写成"N 天"，避免用户去数小数点的位置。
        /// </summary>
        public static string FormatDuration(TimeSpan duration)
        {
            if (duration >= TimeSpan.MaxValue)
            {
                return "> 1000 d";
            }

            if (duration.TotalDays >= 1)
            {
                return $"{duration.TotalDays:0.#} d {duration.Hours} h";
            }

            return duration.TotalHours >= 1
                ? $"{duration.TotalHours:0.#} h"
                : $"{Math.Max(1, Math.Round(duration.TotalMinutes)):0} min";
        }
    }
}
