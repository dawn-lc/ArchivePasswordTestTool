namespace ArchivePasswordTestTool
{
    /// <summary>
    /// COM 回调跟踪（诊断用）：<c>APTT_TRACE=1</c> 时把回调序列写到 stderr。
    /// <para>
    /// 独立成类型而非挂在某个回调类上——所有回调（流、打开、解包）都要用，
    /// 挂在 <c>FileInStream</c> 上会让其余类为了打一行日志去依赖一个具体的流实现。
    /// </para>
    /// <para>
    /// 多进程模式下该变量会被主进程从 worker 环境中剥离：worker 的 stderr 无人读取，
    /// 跟踪输出会填满管道缓冲并导致双方互等死锁（见 MultiProcess.WorkerLoop）。
    /// </para>
    /// </summary>
    internal static class ComTrace
    {
        private static readonly bool Enabled = Environment.GetEnvironmentVariable("APTT_TRACE") == "1";

        public static void Write(string message)
        {
            if (Enabled)
            {
                Console.Error.WriteLine($"[TRACE:{Environment.CurrentManagedThreadId:00}] {message}");
            }
        }
    }
}
