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
        private readonly ConcurrentBag<ArchiveContext> _contextPool = new();

        /// <summary>
        /// 执行模式与并发配置。多进程仅对 7z 且字典足够大时启用（绕开 dll 内 KDF 全局锁）
        /// </summary>
        public (ExecutionMode Mode, int WorkerCount, int ThreadParallelism, int DisplayParallelism) Plan(
            string dllPath,
            ArchiveInfo archiveInfo,
            string archivePath,
            long dictionaryCount)
        {
            int threadParallelism = Math.Max(1, Environment.ProcessorCount - 1);

            if (int.TryParse(Environment.GetEnvironmentVariable("APTT_PARALLELISM"), out int envParallelism) && envParallelism > 0)
            {
                threadParallelism = Math.Min(envParallelism, threadParallelism);
            }

            int workerProcessCount = MultiProcess.ResolveWorkerCount();
            bool useMultiProcess = archiveInfo.Type == SevenZipGuids.CLSID_CFormat7z
                && dictionaryCount >= MultiProcess.MinPasswordsForMultiProcess
                && workerProcessCount > 1;

            if (archiveInfo.IsHeaderEncryption)
            {
                AnsiConsole.MarkupLine($"[yellow]{Resources.FullyEncryptedArchiveWarning}[/]");
            }

            if (!useMultiProcess && !archiveInfo.IsHeaderEncryption)
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
                : new ArchiveContext(dllPath, archivePath, archiveInfo, null);
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
                    _contextPool.Add(new ArchiveContext(dllPath, archivePath, archiveInfo, null));
                }
                catch
                {
                    // 池成员创建失败时按需补建
                }
            }
        }
    }
}
