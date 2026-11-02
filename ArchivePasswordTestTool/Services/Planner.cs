using System.Collections.Concurrent;
using ArchivePasswordTestTool.Properties;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>测试执行模式。多进程仅对 7z 且字典足够大时启用（绕开 dll 内 KDF 全局锁）</summary>
    internal enum ExecutionMode
    {
        Threaded,
        MultiProcess,
    }

    /// <summary>
    /// 执行模式规划与上下文池管理。从 Runner 拆分：
    /// 决定线程/多进程模式、并发参数，并预建可复用的 ArchiveContext 池。
    /// </summary>
    internal sealed class Planner
    {
        private readonly ConcurrentBag<ArchiveContext> _contextPool = [];

        /// <summary>
        /// 执行模式与并发配置。多进程仅对 7z 且字典足够大时启用（绕开 dll 内 KDF 全局锁）
        /// </summary>
        /// <param name="skipContextPool">
        /// 调用方将要走快速判错路径时置 true：预建上下文池要为每个并行度打开一次归档，
        /// 而快速判错只在最后用一次上下文做确认，预建纯属浪费
        /// </param>
        public (ExecutionMode Mode, int WorkerCount, int ThreadParallelism, int DisplayParallelism) Plan(
            string dllPath,
            ArchiveInfo archiveInfo,
            string archivePath,
            long dictionaryCount,
            bool skipContextPool = false)
        {
            // 全量解密路径按**物理核数**并行：该路径是内存/AES 吞吐密集型，
            // ProcessorCount-1 会把 SMT 逻辑核算进来。实测 8 核 16 线程机器上
            // 15 线程只有 6.4–7.3 密码/秒，8 线程反而 9.6–11.2（超订导致缓存与带宽争用）
            int threadParallelism = ParallelismLimit.Apply(Math.Max(1, CpuTopology.PhysicalCoreCount));

            int workerProcessCount = MultiProcess.ResolveWorkerCount();
            bool useMultiProcess = archiveInfo.Type == SevenZipGuids.CLSID_CFormat7z
                && dictionaryCount >= MultiProcess.MinPasswordsForMultiProcess
                && workerProcessCount > 1;

            if (archiveInfo.IsHeaderEncryption)
            {
                AnsiConsole.MarkupLine($"[yellow]{Resources.FullyEncryptedArchiveWarning}[/]");
            }

            if (!useMultiProcess && !archiveInfo.IsHeaderEncryption && !skipContextPool)
            {
                PreCreateContextPool(dllPath, archivePath, archiveInfo, threadParallelism);
            }

            return useMultiProcess
                ? (ExecutionMode.MultiProcess, workerProcessCount, threadParallelism, workerProcessCount)
                : (ExecutionMode.Threaded, 0, threadParallelism, threadParallelism);
        }

        /// <summary>从池中取出一个上下文；池空时按需新建</summary>
        public ArchiveContext RentOrCreate(string dllPath, string archivePath, ArchiveInfo archiveInfo)
        {
            return _contextPool.TryTake(out var context)
                ? context
                : new ArchiveContext(dllPath, archivePath, password: null, archiveInfo);
        }

        /// <summary>归还上下文到池中</summary>
        public void Return(ArchiveContext context)
        {
            _contextPool.Add(context);
        }

        /// <summary>释放池中全部上下文</summary>
        public void DisposeAll()
        {
            foreach (var context in _contextPool)
            {
                context.Dispose();
            }
            _contextPool.Clear();
        }

        private void PreCreateContextPool(string dllPath, string archivePath, ArchiveInfo archiveInfo, int parallelism)
        {
            for (int i = 0; i < parallelism; i++)
            {
                try
                {
                    _contextPool.Add(new ArchiveContext(dllPath, archivePath, password: null, archiveInfo));
                }
                catch
                {
                    // 池成员创建失败时按需补建
                }
            }
        }
    }
}
