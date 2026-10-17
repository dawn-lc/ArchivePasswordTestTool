using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 线程安全、分段读取、构造期去重的密码字典。
    /// 设计要点：
    /// - 分段消费：常驻缓冲是 ≤Limit 行的有界队列，逐段从顺序流读入；
    /// - 构造期一趟顺序扫描顺便去重与计数：Count = 唯一密码数；
    ///   全局去重的内存代价是 O(唯一密码数) 的哈希集合（信息论下限，无法避开），
    ///   换取重复密码零重复测试；消费每服务一个即从集合移除，内存随进度递减；
    /// - 文件序服务：重复行仅在首次出现处被服务一次；
    /// - 多线程安全：Pop/补充缓冲/读流均在同一把锁内完成。
    /// Count 供 Parallel.For(0, Count) 迭代计划使用：迭代数与实际可服务数精确相等。
    /// 消费方：Runner（进程内并行模式）与 MultiProcess（master 分发）。
    /// </summary>
    public sealed class PasswordDictionary : IDisposable
    {
        private readonly Lock SyncRoot = new();
        private readonly Queue<string> Buffer = new();

        // 待服务的唯一密码集合：构造扫描时建立，消费时 Remove 命中即服务，
        // 文件中的重复行因不再命中而被跳过；内存随消费进度递减
        private readonly HashSet<string> Pending;

        private StreamReader? Reader;
        private bool Exhausted;

        private int Limit { get; init; }

        /// <summary>唯一密码数（构造期顺序扫描去重后的行数，含在线密码并入的新密码）</summary>
        public int Count { get; init; }

        public string DictionaryPath { get; init; }

        /// <summary>
        /// 构造字典。onlinePassword 为在线服务按压缩包哈希下发的共享密码（可为 null，
        /// 一个哈希对应一个密码）：并入待办集合并放进缓冲区头部，保证被最先测试；
        /// 与文件内重复的密码按去重规则只测一次
        /// </summary>
        public PasswordDictionary(string dictionaryPath, int limit = 2048, string? onlinePassword = null)
        {
            DictionaryPath = dictionaryPath;
            Limit = limit > 0 ? limit : 2048;

            // 构造期一趟顺序扫描：建立唯一密码集合并计数（顺便完成去重）
            var pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(dictionaryPath, Encoding.UTF8))
            {
                pending.Add(line);
            }

            // 在线共享密码并进缓冲区头部（最优先测试）；pending.Add 去重，
            // 后续扫到文件中的同密码行会因 Remove 未命中而跳过
            if (!string.IsNullOrEmpty(onlinePassword) && pending.Add(onlinePassword))
            {
                Buffer.Enqueue(onlinePassword);
            }

            Pending = pending;
            Count = pending.Count;

            Reader = new StreamReader(dictionaryPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        }

        /// <summary>
        /// 线程安全地弹出下一个密码；字典耗尽时返回 null（防御性兜底，正常迭代数等于 Count）
        /// </summary>
        public string? Pop()
        {
            lock (SyncRoot)
            {
                if (Buffer.Count == 0 && !Exhausted)
                {
                    FillBuffer();
                }

                return Buffer.Count > 0 ? Buffer.Dequeue() : null;
            }
        }

        /// <summary>
        /// 从流中顺序读入下一批（≤Limit 行）。仅在持锁状态下调用；
        /// 每行须命中待办集合（首次出现）才入队，重复行跳过；
        /// 流到达末尾或待办清空（剩余全是重复行）时结束并释放流
        /// </summary>
        private void FillBuffer()
        {
            var reader = Reader;
            if (reader is null)
            {
                return;
            }

            int loaded = 0;
            bool endOfStream = false;

            while (loaded < Limit && Pending.Count > 0)
            {
                string? line = reader.ReadLine();
                if (line is null)
                {
                    endOfStream = true;
                    break;
                }

                // 命中 = 该密码的首次出现 → 服务；未命中 = 重复行 → 跳过
                if (Pending.Remove(line))
                {
                    Buffer.Enqueue(line);
                    loaded++;
                }
            }

            // 待办已清空（后续文件内容全是重复行）或流已读完，都无需再读
            if (endOfStream || Pending.Count == 0)
            {
                Exhausted = true;
                reader.Dispose();
                Reader = null;
            }
        }

        public void Dispose()
        {
            lock (SyncRoot)
            {
                Reader?.Dispose();
                Reader = null;
                Exhausted = true;
                Buffer.Clear();
                Pending.Clear();
            }
        }
    }
}
