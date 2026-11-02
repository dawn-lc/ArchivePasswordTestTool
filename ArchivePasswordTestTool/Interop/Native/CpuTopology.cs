using System.Numerics;
using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 物理核心数探测。
    /// <para>
    /// 全量解密路径（RAR / Zip 的进程内线程模式）是内存与 AES 吞吐密集型，
    /// 用 <see cref="Environment.ProcessorCount"/> 会把 SMT 逻辑核当物理核用。
    /// 实测 8 核 16 线程机器上：8 线程 9.6–11.2 密码/秒，15 线程掉到 6.4–7.3
    /// （超订导致缓存与内存带宽争用），并行度必须按物理核给。
    /// </para>
    /// <para>
    /// 7z 多进程路径不受此影响（那边瓶颈是 dll 内全局锁，进程数越多越好），
    /// 因此 <see cref="MultiProcess.ResolveWorkerCount"/> 仍用逻辑核数。
    /// </para>
    /// <para>
    /// <b>为什么要叠加亲和性掩码</b>：这两个 API 都不看进程亲和性——
    /// 实测把进程限制到 2 个逻辑核后，<see cref="Environment.ProcessorCount"/> 仍返回 16、
    /// 拓扑查询仍返回 8 个物理核，直接照此开线程会超订 4 倍。
    /// 任务管理器的「设置相关性」、Job object 的 <c>JOB_OBJECT_LIMIT_AFFINITY</c>
    /// （Windows 容器用它限制 CPU）都只改掩码，因此必须用 <c>GetProcessAffinityMask</c> 再收一次。
    /// </para>
    /// </summary>
    internal static class CpuTopology
    {
        /// <summary>LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore</summary>
        private const int RelationProcessorCore = 0;

        /// <summary>进程伪句柄（-1），传给 GetProcessAffinityMask 即可，无需关闭</summary>
        private static readonly IntPtr CurrentProcess = new(-1);

        private static readonly Lazy<int> PhysicalCores = new(Detect, isThreadSafe: true);

        /// <summary>
        /// 可用于全量解密的并行度：物理核数，且不超过进程实际可用的逻辑核数（亲和性掩码）。
        /// 探测失败时退回逻辑核数——宁可多开线程，也不要因为拿不到拓扑把并行度压到 1。
        /// </summary>
        public static int PhysicalCoreCount => PhysicalCores.Value;

        /// <summary>
        /// 取物理核与可用逻辑核的较小值。抽成纯函数便于单元测试
        /// （测试进程内改亲和性会干扰同进程内并行执行的其它用例）。
        /// </summary>
        internal static int Combine(int physicalCores, int availableLogicalCores)
        {
            if (physicalCores <= 0 || availableLogicalCores <= 0)
            {
                return Math.Max(1, Math.Max(physicalCores, availableLogicalCores));
            }

            return Math.Max(1, Math.Min(physicalCores, availableLogicalCores));
        }

        private static int Detect()
        {
            int logical = Math.Max(1, Environment.ProcessorCount);

            if (!OperatingSystem.IsWindows())
            {
                // 非 Windows 没有 GetLogicalProcessorInformationEx：
                // Linux 上 ProcessorCount 已经尊重 cgroup 配额与 sched_setaffinity，直接用它
                return logical;
            }

            int physical = DetectPhysicalCores(logical);
            int available = DetectAvailableLogicalCores(logical);

            return Combine(physical, available);
        }

        private static int DetectPhysicalCores(int fallback)
        {
            uint length = 0;

            // 第一次调用只为取所需缓冲区大小，预期返回 ERROR_INSUFFICIENT_BUFFER
            _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
            if (length == 0)
            {
                return fallback;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                {
                    return fallback;
                }

                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX 的 Relationship 与 Size 都是 4 字节：
                //   DWORD Relationship; DWORD Size; union { ... }
                // 按 Size 遍历，每个 RelationProcessorCore 条目对应一个物理核心
                int count = 0;
                int offset = 0;
                while (offset + 8 <= length)
                {
                    int relationship = Marshal.ReadInt32(buffer, offset);
                    int size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size <= 0)
                    {
                        break;
                    }

                    if (relationship == RelationProcessorCore)
                    {
                        count++;
                    }

                    offset += size;
                }

                return count > 0 ? count : fallback;
            }
            catch (Exception)
            {
                // 极老系统或受限环境：退回逻辑核数
                return fallback;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 进程实际可用的逻辑核数（亲和性掩码内的核数）。
        /// <para>
        /// 多处理器组（&gt;64 逻辑核）时 <c>GetProcessAffinityMask</c> 只覆盖当前组，
        /// 换算会低估可用核数，因此直接退回 <see cref="Environment.ProcessorCount"/>
        /// （它按所有组统计）。跨组机器极罕见，优先保证不误压并行度。
        /// </para>
        /// </summary>
        private static int DetectAvailableLogicalCores(int fallback)
        {
            if (GetActiveProcessorGroupCount() > 1)
            {
                return fallback;
            }

            if (!GetProcessAffinityMask(CurrentProcess, out IntPtr processMask, out _))
            {
                return fallback;
            }

            int bits = BitOperations.PopCount((ulong)processMask.ToInt64());
            return bits > 0 ? bits : fallback;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationshipType,
            IntPtr buffer,
            ref uint returnedLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessAffinityMask(
            IntPtr process,
            out IntPtr processAffinityMask,
            out IntPtr systemAffinityMask);

        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();
    }
}
