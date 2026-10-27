using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 7-Zip 的 7zAES 密钥派生（PrepareKey）在 7z.dll 内受进程级全局锁串行化
    /// （见 7zAes.cpp 的 g_GlobalKeyCacheCriticalSection / MT_LOCK，每个密码 KDF 约 16ms），
    /// 线程内并行无法提升 7z 测密吞吐。本类改用多进程绕开：
    /// 主进程仍持有字典（去重/线程安全），把密码逐批经 stdin 送入 N 个自身副本
    /// （-Worker 模式，无 UI），每个副本加载独立的 7z.dll（独立进程地址空间 →
    /// 独立全局锁），KDF 不再互相排队，吞吐随进程数近似线性。
    /// RAR5/zip 等无此全局锁的格式仍走进程内线程路径。
    /// </summary>
    internal static partial class MultiProcess
    {
        public const string WorkerFlag = "-Worker";

        /// <summary>多进程收益阈值：更小的字典用进程内线程更划算（进程启动开销占主导）</summary>
        public const int MinPasswordsForMultiProcess = 256;

        /// <summary>
        /// 单批密码上限。批越大管道往返与 JSON 开销占比越低（实测往返 &lt;1ms，
        /// 而一批 64 个密码的计算约 1.2s，开销占比 &lt;0.1%），但也决定了调度粒度：
        /// 固定 64 会让小字典只够少数工作进程瓜分，其余进程启动完立刻空转退出。
        /// </summary>
        private const int MaxBatchSize = 64;

        /// <summary>单批密码下限：再小则往返次数上升，且进度回调过于琐碎</summary>
        private const int MinBatchSize = 8;

        /// <summary>每个工作进程期望摊到的批数，决定批大小的均衡粒度</summary>
        private const int TargetBatchesPerWorker = 4;

        private const string ReadyMarker = "READY";
        private const string StopMarker = "STOP";
        private const string QuitMarker = "QUIT";
        private const string WorkersEnvVar = "APTT_WORKERS";
        private const string ParallelismEnvVar = "APTT_PARALLELISM";

        /// <summary>
        /// 按字典规模推算单批大小，使每个工作进程都能分到活且负载大致均匀。
        /// 目标为每个进程约 <see cref="TargetBatchesPerWorker"/> 批：
        /// 字典远大于「进程数 × 上限」时结果就是 <see cref="MaxBatchSize"/>（与固定批等价），
        /// 只有「小字典 + 多进程」时批才收缩，避免出现「300 个密码仅 5 个进程干活、
        /// 其余 15 个空转」这类批粒度不匹配的浪费。
        /// </summary>
        internal static int ResolveBatchSize(int passwordCount, int workerCount)
        {
            if (passwordCount <= 0 || workerCount <= 0)
            {
                return MinBatchSize;
            }

            long divisor = (long)workerCount * TargetBatchesPerWorker;
            int share = (int)((passwordCount + divisor - 1) / divisor);
            return Math.Clamp(share, MinBatchSize, MaxBatchSize);
        }

        /// <summary>
        /// 决定工作进程数：APTT_WORKERS 显式指定优先；否则默认逻辑核心数，
        /// 并在设置了 APTT_PARALLELISM（总并发的语义）时取更小值。
        /// 返回值 &lt;=1 表示退出多进程模式。
        /// </summary>
        public static int ResolveWorkerCount()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(WorkersEnvVar), out var explicitWorkers) && explicitWorkers > 0)
            {
                return explicitWorkers;
            }

            int workers = Environment.ProcessorCount;
            if (int.TryParse(Environment.GetEnvironmentVariable(ParallelismEnvVar), out var parallelism) && parallelism > 0)
            {
                workers = Math.Min(workers, parallelism);
            }

            return workers;
        }

        /// <summary>
        /// 运行多进程测试：启动 workerCount 个工作进程并等待全部结束。
        /// 密码由主进程从 dictionary 逐批取出去重分发；结果经回调回传
        /// （onTested 按批 ACK 累计进度；onFound 收到正确密码即取消其余进程）。
        /// 任一工作进程报告错误时抛出异常。
        /// </summary>
        public static void RunWorkers(
            string exePath,
            string? dllPath,
            string archivePath,
            PasswordDictionary dictionary,
            int workerCount,
            Action<int> onTested,
            Action<string> onFound)
        {
            var retry = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            using var cancellation = new CancellationTokenSource();

            int batchSize = ResolveBatchSize(dictionary.Count, workerCount);
            var tasks = Enumerable.Range(0, workerCount)
                .Select(_ => Task.Run(() => WorkerLoop(exePath, dllPath, archivePath, dictionary, batchSize, retry, errors, cancellation, onTested, onFound)))
                .ToArray();

            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException ex)
            {
                foreach (var inner in ex.InnerExceptions)
                {
                    errors.Enqueue(inner.Message);
                }
            }

            if (errors.TryDequeue(out var first))
            {
                throw new InvalidOperationException(first);
            }
        }

        /// <summary>
        /// 工作进程入口：无 UI，stdout 只输出协议行。
        /// 启动成功输出 READY；每批回复一行 JSON（Tested/Found/Error）；
        /// 退出码：0=找到密码或收到 STOP，1=字典耗尽，2=启动/运行错误。
        /// </summary>
        public static int RunWorker(string[] args)
        {
            try
            {
                string archivePath = Arguments.GetParameter(args, "F", "").Replace("\"", "");
                if (string.IsNullOrEmpty(archivePath))
                {
                    return FailStartup("Archive file not found.");
                }

                string? dllPath = SevenZip.LocateInstalledLibrary();
                string lib = Arguments.GetParameter(args, "lib", "").Replace("\"", "");
                if (!string.IsNullOrEmpty(lib))
                {
                    dllPath = lib;
                }

                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                {
                    return FailStartup($"7z.dll not found: {lib}");
                }

                try
                {
                    SevenZipLibrary.GetShared(dllPath);
                }
                catch (Exception ex)
                {
                    return FailStartup($"Failed to initialize 7z.dll: {ex}");
                }

                // 打开归档探测信息。header 加密时无密码打开得到空文件列表的 info，
                // 但 IsHeaderEncryption 判定可靠，每个密码用带密码的临时上下文处理
                ArchiveInfo info;
                ArchiveContext? sharedContext;
                try
                {
                    var probe = new ArchiveContext(dllPath, archivePath, null);
                    info = probe.Info;
                    if (info.IsHeaderEncryption)
                    {
                        probe.Dispose();
                        sharedContext = null;
                    }
                    else
                    {
                        sharedContext = probe; // 非 header 加密：单上下文复用测密
                    }
                }
                catch (Exception ex)
                {
                    return FailStartup(ex.ToString());
                }

                var output = Console.Out;
                output.WriteLine(ReadyMarker);
                output.Flush();

                string? line;
                while ((line = Console.ReadLine()) is not null)
                {
                    if (line == StopMarker)
                    {
                        return 0;
                    }

                    if (line == QuitMarker)
                    {
                        return 1;
                    }

                    List<string>? batch;
                    try
                    {
                        batch = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.ListString);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (batch is null || batch.Count == 0)
                    {
                        continue;
                    }

                    int tested = 0;
                    string? found = null;

                    foreach (var password in batch)
                    {
                        bool ok;
                        try
                        {
                            if (sharedContext != null)
                            {
                                ok = sharedContext.TestPassword(password);
                            }
                            else
                            {
                                using var context = new ArchiveContext(dllPath, archivePath, password, info);
                                ok = context.TestPassword(password);
                            }
                        }
                        catch (ArchivePasswordException)
                        {
                            // 与进程内路径一致：密码错误（header/全加密包在 Open 阶段校验）
                            ok = false;
                        }

                        tested++;
                        if (ok)
                        {
                            found = password;
                            break;
                        }
                    }

                    WriteReply(new WorkerBatchReply { Tested = tested, Found = found });
                    if (found is not null)
                    {
                        return 0;
                    }
                }

                return 1;
            }
            catch (Exception ex)
            {
                return FailStartup(ex.ToString());
            }
        }

        private static void WorkerLoop(
            string exePath,
            string? dllPath,
            string archivePath,
            PasswordDictionary dictionary,
            int batchSize,
            ConcurrentQueue<string> retry,
            ConcurrentQueue<string> errors,
            CancellationTokenSource cancellation,
            Action<int> onTested,
            Action<string> onFound)
        {
            var token = cancellation.Token;

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory,
                // worker 的输出被重定向，Program.Main 会把它的 stdout/stderr 设为 UTF-8；
                // 读取端必须用同一编码，否则中文密码/中文诊断消息都会读成乱码
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            // 剥离诊断开关：工作进程 stderr 无人读取，ComTrace 输出可能填满管道缓冲导致死锁
            startInfo.EnvironmentVariables.Remove("APTT_TRACE");
            startInfo.ArgumentList.Add(WorkerFlag);
            startInfo.ArgumentList.Add("-F");
            startInfo.ArgumentList.Add(archivePath);
            if (!string.IsNullOrEmpty(dllPath))
            {
                startInfo.ArgumentList.Add("-lib");
                startInfo.ArgumentList.Add(dllPath);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                errors.Enqueue("无法启动工作进程");
                cancellation.Cancel();
                return;
            }

            var stdin = process.StandardInput;
            var stdout = process.StandardOutput;

            // stderr 必须持续消费：重定向后若无人读取，worker 一旦写满管道缓冲
            // （未处理异常、OOM、failfast 都会写）就会阻塞在 Write，
            // 而主进程在 stdout.ReadLine 等回复 —— 双方互等即死锁。
            // 同时保留尾部内容：worker 异常退出时这就是唯一的崩溃线索。
            var stderrTail = new WorkerStderrTail();
            process.ErrorDataReceived += (_, e) => stderrTail.Append(e.Data);
            process.BeginErrorReadLine();

            try
            {
                var ready = stdout.ReadLine();
                if (ready != ReadyMarker)
                {
                    errors.Enqueue(ParseWorkerError(ready, stderrTail));
                    cancellation.Cancel();
                    return;
                }

                while (!token.IsCancellationRequested)
                {
                    var batch = new List<string>(batchSize);
                    while (batch.Count < batchSize)
                    {
                        // 重投队列优先（工作进程崩溃时整批未知结果，须补测）
                        string? password = retry.TryDequeue(out var requeued) ? requeued : dictionary.Pop();
                        if (password is null)
                        {
                            break;
                        }

                        batch.Add(password);
                    }

                    if (batch.Count == 0)
                    {
                        stdin.WriteLine(QuitMarker);
                        stdin.Flush();
                        break;
                    }

                    stdin.WriteLine(JsonSerializer.Serialize(batch, WorkerJsonContext.Default.ListString));
                    stdin.Flush();

                    var replyLine = stdout.ReadLine();
                    if (replyLine is null)
                    {
                        // 工作进程中途异常退出：该批结果未知，整批重投（宁可重测，不可漏测）
                        foreach (var password in batch)
                        {
                            retry.Enqueue(password);
                        }

                        // 退出但无回复 = 崩溃。把 stderr 尾部当作错误上报，
                        // 否则用户只能看到"工作进程已退出"而无从定位
                        string diagnostics = stderrTail.Describe();
                        if (diagnostics.Length > 0)
                        {
                            errors.Enqueue(diagnostics);
                            cancellation.Cancel();
                        }

                        break;
                    }

                    WorkerBatchReply? reply;
                    try
                    {
                        reply = JsonSerializer.Deserialize(replyLine, WorkerJsonContext.Default.WorkerBatchReply);
                    }
                    catch (JsonException)
                    {
                        reply = null;
                    }

                    if (reply is null)
                    {
                        foreach (var password in batch)
                        {
                            retry.Enqueue(password);
                        }

                        break;
                    }

                    if (reply.Tested > 0)
                    {
                        onTested(reply.Tested);
                    }

                    if (!string.IsNullOrEmpty(reply.Error))
                    {
                        errors.Enqueue(reply.Error!);
                        cancellation.Cancel();
                        return;
                    }

                    if (!string.IsNullOrEmpty(reply.Found))
                    {
                        onFound(reply.Found!);
                        cancellation.Cancel();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex.Message);
                cancellation.Cancel();
            }
            finally
            {
                try
                {
                    stdin.WriteLine(StopMarker);
                    stdin.Flush();
                    stdin.Close();
                }
                catch
                {
                    // 管道可能已随进程退出而关闭
                }

                if (!process.WaitForExit(3000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // 进程可能已自行退出
                    }
                }

                process.CancelErrorRead();
            }
        }

        /// <summary>
        /// 缓存在 worker 的 stderr 尾部若干行，用于崩溃诊断。
        /// 只留尾部：正常运行时 stderr 应该是空的，大量输出意味着出了状况，
        /// 而最能说明问题的是最后几行。
        /// </summary>
        private sealed class WorkerStderrTail
        {
            private const int MaxLines = 20;

            private readonly Lock gate = new();
            private readonly Queue<string> lines = new();

            public void Append(string? line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                lock (gate)
                {
                    lines.Enqueue(line);
                    while (lines.Count > MaxLines)
                    {
                        lines.Dequeue();
                    }
                }
            }

            /// <summary>拼成可直接展示的诊断文本；无内容时返回空串</summary>
            public string Describe()
            {
                lock (gate)
                {
                    return lines.Count == 0
                        ? string.Empty
                        : string.Join(Environment.NewLine, lines);
                }
            }
        }

        private static string ParseWorkerError(string? line, WorkerStderrTail stderrTail)
        {
            if (line is null)
            {
                string diagnostics = stderrTail.Describe();
                return diagnostics.Length > 0 ? diagnostics : "工作进程已退出";
            }

            try
            {
                var reply = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerBatchReply);
                if (reply?.Error is not null)
                {
                    return reply.Error;
                }
            }
            catch (JsonException)
            {
            }

            return line;
        }

        private static int FailStartup(string message)
        {
            WriteReply(new WorkerBatchReply { Error = message });
            return 2;
        }

        private static void WriteReply(WorkerBatchReply reply)
        {
            var output = Console.Out;
            output.WriteLine(JsonSerializer.Serialize(reply, WorkerJsonContext.Default.WorkerBatchReply));
            output.Flush();
        }
    }

    /// <summary>工作进程对一批密码的回复（JSON，行式）</summary>
    internal sealed class WorkerBatchReply
    {
        public int Tested { get; set; }

        public string? Found { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>
    /// Json 源生成：管道协议序列化不依赖反射，IL 裁剪后可用
    /// （反射式 JsonSerializer 在裁剪后默认被禁用，已实测报
    /// JsonSerializerIsReflectionDisabled）
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(WorkerBatchReply))]
    internal partial class WorkerJsonContext : JsonSerializerContext
    {
    }
}
