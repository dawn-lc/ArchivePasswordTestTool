using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 线程安全、单趟读取、构造期去重的密码字典。
    /// 设计要点：
    /// - 一趟顺序扫描完成"读取 + 去重 + 计数"：唯一密码按首次出现顺序存入列表，
    ///   Count = 唯一密码数；不再为建集合先扫一遍、再用流读第二遍（省一次全文件 I/O）；
    /// - 消费按游标推进，取走即把槽位置 null：已测试的密码可被 GC 回收，内存随进度递减；
    /// - 文件序服务：重复行仅在首次出现处被服务一次；
    /// - 多线程安全：Pop 在同一把锁内推进游标。
    /// Count 供 Parallel.For(0, Count) 迭代计划使用：迭代数与实际可服务数精确相等。
    /// 消费方：Runner（进程内并行模式）与 MultiProcess（master 分发）。
    /// </summary>
    public sealed class PasswordDictionary : IDisposable
    {
        private readonly Lock SyncRoot = new();

        // 去重后的密码（文件序），消费时按 Cursor 推进并把已服务槽位置 null
        private readonly List<string?> Pending;
        private int Cursor;

        /// <summary>唯一密码数（构造期顺序扫描去重后的行数，含在线密码并入的新密码）</summary>
        public int Count { get; init; }

        public string DictionaryPath { get; init; }

        /// <summary>
        /// 构造字典。onlinePassword 为在线服务按压缩包哈希下发的共享密码（可为 null，
        /// 一个哈希对应一个密码）：置于待办列表头部，保证被最先测试；
        /// 与文件内重复的密码按去重规则只测一次
        /// </summary>
        public PasswordDictionary(string dictionaryPath, string? onlinePassword = null)
        {
            DictionaryPath = dictionaryPath;

            // 单趟顺序扫描：边读边去重（去重集合只为判定重复，不保序，读完即可回收）
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string?>();

            // 在线共享密码放在最前（最优先测试）；pending.Add 去重，
            // 后续扫到文件中的同密码行会因 seen 已命中而跳过
            if (!string.IsNullOrEmpty(onlinePassword) && seen.Add(onlinePassword))
            {
                ordered.Add(onlinePassword);
            }

            foreach (var line in File.ReadLines(dictionaryPath, Encoding.UTF8))
            {
                if (seen.Add(line))
                {
                    ordered.Add(line);
                }
            }

            Pending = ordered;
            Count = ordered.Count;
        }

        /// <summary>
        /// 线程安全地取出下一个密码；字典耗尽时返回 null（防御性兜底，正常迭代数等于 Count）
        /// </summary>
        public string? Pop()
        {
            lock (SyncRoot)
            {
                while (Cursor < Pending.Count)
                {
                    string? password = Pending[Cursor];
                    if (password is null)
                    {
                        // 已被并发取走（正常路径下不会发生：游标保证每个槽只读一次）
                        Cursor++;
                        continue;
                    }

                    // 取走即释放引用，让已测试的密码可被 GC 回收
                    Pending[Cursor] = null;
                    Cursor++;
                    return password;
                }

                return null;
            }
        }

        public void Dispose()
        {
            lock (SyncRoot)
            {
                for (int i = 0; i < Pending.Count; i++)
                {
                    Pending[i] = null;
                }

                Cursor = Pending.Count;
            }
        }
    }
}
