using ArchivePasswordTestTool.Properties;
using System.Diagnostics;
using System.Security.Cryptography;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 命令行测试主流程：头部展示 → 解析参数（7z 环境优先，再取路径）→
    /// 初始化 7z → 打开归档 → 选择执行模式（多进程 / 进程内并行）→ 输出结果。
    /// 异常与退出码语义（E2E 锁定）：缺参数/格式不识别 → exit 1；
    /// 正常流程（含未找到密码）→ exit 0。
    /// </summary>
    internal sealed class Runner(string[] args)
    {
        private static readonly SHA256 HashAlgorithm = SHA256.Create();
        private readonly Stopwatch stopwatch = new();
        private readonly Planner planner = new();

        private string archivePath = string.Empty;
        private string dictionaryPath = string.Empty;
        private string archiveHash = string.Empty;
        private string archiveHashHex = string.Empty;
        private long dictionaryCount;
        private long testedCount;
        private string? foundPassword;

        public void Run()
        {
            Ui.RunInAlternateScreenWhenSupported(RunCore);
        }

        private void RunCore()
        {
            try
            {
                // 启动流程微调：头部最先展示，其后依次是 7z 环境解析、
                // 路径引导/参数校验、初始化与分析
                if (AnsiConsole.Profile.Out.IsTerminal)
                {
                    AnsiConsole.Clear();
                }
                Ui.WriteHeader(Program.Version);

                Upgrade.CheckForUpdate();

                var options = Options.Parse(args);
                archivePath = options.ArchivePath;
                dictionaryPath = options.DictionaryPath;

                SevenZip.Initialize(options.SevenZipDllPath);

                using (var file = File.OpenRead(archivePath))
                {
                    // 统一计算文件哈希（动态赋值不会触发 CS0219）：
                    // Base64 版用于测试结果文件；Hex 版作为在线服务的归档标识。
                    // 分块 TransformBlock 计算并驱动进度条按字节推进（大文件不再黑屏干等）。
                    // 两处"分析中"提示已合并：进度条任务描述即提示，不再单独 MarkupLine
                    HashAlgorithm.Initialize();
                    long fileLength = Math.Max(1, file.Length);

                    AnsiConsole.Progress()
                        .AutoClear(true)
                        .HideCompleted(true)
                        .Columns([
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new ElapsedTimeColumn(),
                            new RemainingTimeColumn(),
                            new SpinnerColumn()
                        ])
                        .Start(ctx =>
                        {
                            var hashTask = ctx.AddTask(Resources.AnalyzingArchiveInformation, maxValue: fileLength);

                            byte[] buffer = new byte[1024 * 1024];
                            int read;
                            while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                HashAlgorithm.TransformBlock(buffer, 0, read, null, 0);
                                hashTask.Increment(read);
                            }

                            HashAlgorithm.TransformFinalBlock([], 0, 0);
                            var hashBytes = HashAlgorithm.Hash!;
                            archiveHash = Convert.ToBase64String(hashBytes);
                            archiveHashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

                            // 空文件无字节可读，强制补满避免进度条停在起点
                            hashTask.Value = hashTask.MaxValue;
                        });
                }

                // 在线共享（默认开启）：哈希计算完成后立即按哈希向服务器查询共享密码
                // （一个哈希对应一个密码），存在则置于队列最前优先测试；
                // 失败只提示不阻断。工作进程不做网络操作，只有主进程在此请求
                string? onlinePassword = null;
                if (options.QueryOnline)
                {
                    onlinePassword = Sync.RequestOnlinePassword(options.OnlineServiceUrl, archiveHashHex);
                }
                using var archiveContext = new ArchiveContext(options.SevenZipDllPath, archivePath, null);
                var archiveInfo = archiveContext.Info;

                if (!archiveInfo.IsEncrypted)
                {
                    AnsiConsole.WriteLine(string.Format(Resources.ArchiveNotEncrypted, archivePath));
                    foundPassword = string.Empty;
                    return;
                }

                using var dictionary = new PasswordDictionary(dictionaryPath, onlinePassword: onlinePassword);
                dictionaryCount = dictionary.Count;

                var (Mode, WorkerCount, ThreadParallelism, DisplayParallelism) = planner.Plan(options.SevenZipDllPath, archiveInfo, archivePath, dictionaryCount);
                Ui.WriteArchiveInfo(
                    archivePath,
                    archiveHash,
                    archiveInfo.Format,
                    archiveInfo.FileCount,
                    archiveInfo.IsHeaderEncryption,
                    archiveInfo.SupportsQuickTest,
                    dictionaryPath,
                    dictionaryCount,
                    DisplayParallelism);

                if (Mode == ExecutionMode.MultiProcess)
                {
                    RunMultiProcess(options.SevenZipDllPath, dictionary, WorkerCount);
                }
                else
                {
                    RunThreaded(options.SevenZipDllPath, archiveInfo, dictionary, ThreadParallelism);
                }

                Ui.WriteOutcome(
                    !string.IsNullOrEmpty(foundPassword),
                    archivePath,
                    dictionaryPath,
                    foundPassword,
                    stopwatch.Elapsed,
                    Interlocked.Read(ref testedCount));

                // 密码回传（默认开启）：找到即把 哈希+密码 POST 给服务端供他人共享；失败只提示
                if (!string.IsNullOrEmpty(foundPassword) && options.ReportOnline)
                {
                    Sync.ReportFoundPassword(options.OnlineServiceUrl, archiveHashHex, foundPassword);
                }
            }
            catch (FileNotFoundException ex)
            {
                Ui.WriteError(ex.Message);
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Ui.WriteError(ex.Message);
                Environment.Exit(1);
            }
            finally
            {
                if (!string.IsNullOrEmpty(archivePath) && !string.IsNullOrEmpty(dictionaryPath))
                {
                    ResultsWriter.WriteIfRequested(
                        archivePath,
                        dictionaryPath,
                        archiveHash,
                        foundPassword is null ? null : foundPassword,
                        stopwatch.Elapsed);
                }
            }
        }

        /// <summary>
        /// 多进程模式：主进程负责字典分发（去重/线程安全），密码逐批经管道送入
        /// N 个自身副本；每个副本加载独立的 7z.dll（独立全局锁），绕开 KDF 串行化。
        /// 进度条由主进程按 worker 回传的批测试计数推进
        /// </summary>
        private void RunMultiProcess(string dllPath, PasswordDictionary dictionary, int workerCount)
        {
            stopwatch.Restart();

            AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .Columns([
                    new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new ElapsedTimeColumn(),
                            new RemainingTimeColumn(),
                            new SpinnerColumn()
                ])
                .Start(ctx =>
                {
                    var testProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: dictionaryCount);

                    MultiProcess.RunWorkers(
                        Environment.ProcessPath!,
                        dllPath,
                        archivePath,
                        dictionary,
                        workerCount,
                        onTested: acked =>
                        {
                            Interlocked.Add(ref testedCount, acked);
                            testProgressBar.Increment(acked);
                        },
                        onFound: password =>
                        {
                            foundPassword = password;
                            stopwatch.Stop();
                        });

                    // 确保进度条完成
                    testProgressBar.Value = testProgressBar.MaxValue;
                });
        }

        /// <summary>
        /// 进程内并行模式：上下文池复用（非 header 加密）或按密码重开（header 加密），
        /// Parallel.For 消费字典，进度条随测试计数推进
        /// </summary>
        private void RunThreaded(string dllPath, ArchiveInfo archiveInfo, PasswordDictionary dictionary, int parallelism)
        {
            var isFullyEncrypted = archiveInfo.IsHeaderEncryption;

            stopwatch.Restart();

            AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .Columns([
                    new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new ElapsedTimeColumn(),
                            new RemainingTimeColumn(),
                            new SpinnerColumn()
                ])
                .Start(ctx =>
                {
                    var testProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: dictionaryCount);

                    Parallel.For(0, dictionaryCount, new ParallelOptions() { MaxDegreeOfParallelism = parallelism }, (i, state) =>
                    {
                        ArchiveContext? context = null;
                        try
                        {
                            string? password = dictionary.Pop();
                            if (password is null)
                            {
                                // 字典已耗尽（不应发生：迭代数等于 Count），安全跳过
                                return;
                            }

                            if (!isFullyEncrypted)
                            {
                                context = planner.RentOrCreate(dllPath, archivePath, archiveInfo);
                            }
                            else
                            {
                                context = new ArchiveContext(dllPath, archivePath, archiveInfo, password);
                            }

                            bool passwordCorrect;

                            if (archiveInfo.SupportsQuickTest && archiveInfo.MinEncryptedFile is not null)
                            {
                                uint[] fileIndices = [archiveInfo.MinEncryptedFile.Value.Key];
                                passwordCorrect = context.TestPassword(password, fileIndices);
                            }
                            else
                            {
                                passwordCorrect = context.TestPassword(password);
                            }

                            if (passwordCorrect)
                            {
                                foundPassword = password;
                                stopwatch.Stop();
                                state.Stop();
                                return;
                            }
                        }
                        catch (OperationCanceledException)
                        {

                        }
                        catch (Exception)
                        {
                            // 打开压缩包失败（完全加密压缩包密码错误时 Open 会返回错误）
                            // 视为"密码不匹配"，继续测试下一个密码
                        }
                        finally
                        {
                            if (!isFullyEncrypted && context != null)
                            {
                                planner.Return(context);
                            }
                            else
                            {
                                context?.Dispose();
                            }

                            Interlocked.Increment(ref testedCount);
                            testProgressBar.Increment(1);
                        }
                    });

                    // 确保进度条完成
                    testProgressBar.Value = testProgressBar.MaxValue;
                });

            planner.DisposeAll();
        }
    }
}
