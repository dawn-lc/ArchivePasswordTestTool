using ArchivePasswordTestTool.Properties;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 控制台窗口标题：启动时写入程序名与版本，运行中反映真实阶段与进度
    /// （<c>ArchivePasswordTestTool v2.3.0 · 测试密码 42%</c>）。
    /// <para>
    /// 为什么值得做：测密可能跑很久，用户切到别的窗口后，任务栏和 Alt+Tab 里
    /// 只有标题这一条信息。让它显示进度，就不必逐个窗口点回来确认有没有卡住。
    /// </para>
    /// <para>
    /// 退出时要不要把标题还回去，取决于控制台归谁：
    /// 双击启动时 Windows 分配专属控制台（1 个进程），窗口随进程关闭，
    /// 标题停在「已找到密码」正好是用户最后能带走的结果；
    /// 从终端启动时与 shell 共享同一个窗口，那是人家的标题栏，用完要还。
    /// </para>
    /// </summary>
    internal static class ConsoleTitle
    {
        /// <summary>
        /// 两次真正调用 SetConsoleTitle 的最小间隔。
        /// <para>
        /// 没有它，热路径上的每次调用都会改窗口标题：实测单进程模式 176 次/秒、
        /// 大字典可达数万次/秒，每次都触发 conhost 重绘标题栏与任务栏按钮。
        /// 200ms 足够让人眼看到"数字在动"，又不产生可见开销。
        /// </para>
        /// </summary>
        private const long MinimumIntervalMilliseconds = 200;

        /// <summary>标题前缀，所有状态都挂在它后面</summary>
        private static readonly string Product = "ArchivePasswordTestTool v" + Program.VersionText;

        /// <summary>进度回调来自 Parallel.For 的多个线程，串行化后才能安全读写下面几个字段</summary>
        private static readonly object Gate = new();

        private static string? applied;
        private static long lastAppliedTicks;

        /// <summary>启动时从壳层拿到的原标题；读不到就是 null</summary>
        private static string? original;

        /// <summary>控制台是否与 shell 共享（从终端启动）。只有共享时才需要归还标题。</summary>
        private static bool sharedConsole;

        /// <summary>
        /// 环境不支持改标题（无控制台窗口等）时置位，之后所有调用直接返回。
        /// 不必每次都去撞同一个失败。
        /// </summary>
        private static bool unavailable;

        /// <summary>写入「程序名 + 版本」。启动时调用一次。</summary>
        public static void Initialize()
        {
            lock (Gate)
            {
                // 专属控制台（双击启动）只需 1 个进程，共享时 ≥ 2。
                // 与 InteractivePrompts.NeedConfirmBeforeExit 用的是同一个判据。
                try
                {
                    sharedConsole = WindowsNativeMethods.GetConsoleProcessList(new uint[2], 2) > 1;
                }
                catch (Exception)
                {
                    // 取不到就不归还：改错标题比不改更糟
                    sharedConsole = false;
                }

                // 记下壳层的原标题，退出时还回去。
                // 读不到（无控制台）就保持 null，Restore 会退回程序名。
                try
                {
                    original = Console.Title;
                }
                catch (Exception)
                {
                    original = null;
                }
            }

            Apply(Product, force: true);
        }

        /// <summary>
        /// 把标题还给 shell（共享控制台时）。双击启动的窗口随进程消失，无需处理。
        /// <para>
        /// 必须显式调用：<see cref="Environment.Exit"/> 不会跑 finally 块，异常分支只能自己调。
        /// </para>
        /// </summary>
        public static void Restore()
        {
            if (!sharedConsole)
            {
                return;
            }

            // 读不到原标题时退回程序名，总比停在「测试密码 42%」好
            Apply(string.IsNullOrEmpty(original) ? Product : original, force: true);
        }

        /// <summary>
        /// 显示某个阶段的完成度。节流合并高频调用，同一百分比不会重复设置。
        /// </summary>
        /// <param name="label">阶段名（进度条上用的是同一个资源字符串）</param>
        /// <param name="value">已完成量</param>
        /// <param name="maxValue">总量；非正数时忽略本次调用</param>
        public static void ShowProgress(string label, double value, double maxValue)
        {
            if (!(maxValue > 0) || double.IsNaN(value))
            {
                return;
            }

            double fraction = value / maxValue;
            if (fraction < 0)
            {
                fraction = 0;
            }
            else if (fraction > 1)
            {
                fraction = 1;
            }

            int percent = (int)(fraction * 100);
            Apply($"{Product} · {label} {percent}%");
        }

        /// <summary>测试结束：已找到密码。用 force 保证不被节流掉，否则标题可能停在「95%」。</summary>
        public static void ShowFound()
        {
            Apply($"{Product} · {Resources.TitlePasswordFound}", force: true);
        }

        /// <summary>测试结束：未找到密码</summary>
        public static void ShowNotFound()
        {
            Apply($"{Product} · {Resources.TitlePasswordNotFound}", force: true);
        }

        private static void Apply(string title, bool force = false)
        {
            lock (Gate)
            {
                if (unavailable)
                {
                    return;
                }

                if (!force)
                {
                    // 百分比取整后大量重复，先比较内容再谈间隔——这是最省的那道闸
                    if (string.Equals(title, applied, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (Environment.TickCount64 - lastAppliedTicks < MinimumIntervalMilliseconds)
                    {
                        return;
                    }
                }

                try
                {
                    // 没有控制台窗口时 SetConsoleTitle 必然失败，先判掉而不是靠异常兜底。
                    // 覆盖的场景：被管道/CI 调用的 CLI、多进程 worker（CreateNoWindow）。
                    if (WindowsNativeMethods.GetConsoleWindow() == IntPtr.Zero)
                    {
                        unavailable = true;
                        return;
                    }

                    Console.Title = title;
                    applied = title;
                    lastAppliedTicks = Environment.TickCount64;
                }
                catch (Exception)
                {
                    // 标题只是锦上添花：任何环境限制（无控制台、非 Windows、
                    // 权限不足）都不该让测密失败
                    unavailable = true;
                }
            }
        }
    }
}
