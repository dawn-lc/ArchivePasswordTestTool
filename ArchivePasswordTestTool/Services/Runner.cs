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

        // 归档哈希：Base64 版用于结果文件，Hex 版作为在线服务的归档标识。
        // 均为惰性计算（见 ComputeArchiveHash）——离线且不保存结果时一次都不读全文件
        private string archiveHash = string.Empty;
        private string archiveHashHex = string.Empty;
        private bool hashComputed;

        /// <summary>未加密压缩包没有密码可记录，结果文件无意义（跳过保存询问）</summary>
        private bool archiveEncrypted = true;

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

                // 更新检查在后台发起：网络耗时与后续引导、测密重叠，不阻塞首屏
                Upgrade.StartBackgroundCheck();

                var options = Options.Parse(args);
                archivePath = options.ArchivePath;
                dictionaryPath = options.DictionaryPath;

                SevenZip.Initialize(options.SevenZipDllPath);

                // 在线服务只在真正配置了地址时才可能用到哈希；
                // 占位地址（未部署服务）下绝不为此读一遍整个压缩包
                bool onlineEnabled = Sync.IsConfigured(options.OnlineServiceUrl);

                using var archiveContext = new ArchiveContext(options.SevenZipDllPath, archivePath, password: null);
                var archiveInfo = archiveContext.Info;

                if (!archiveInfo.IsEncrypted)
                {
                    archiveEncrypted = false;
                    AnsiConsole.WriteLine(string.Format(Resources.ArchiveNotEncrypted, archivePath));
                    return;
                }

                // 在线共享（默认开启）：按哈希向服务器查询共享密码（一个哈希对应一个密码），
                // 存在则置于队列最前优先测试；失败只提示不阻断。
                // 工作进程不做网络操作，只有主进程在此请求
                string? onlinePassword = null;
                if (options.QueryOnline)
                {
                    if (onlineEnabled)
                    {
                        ComputeArchiveHash();
                    }

                    onlinePassword = Sync.RequestOnlinePassword(options.OnlineServiceUrl, archiveHashHex);
                }

                using var dictionary = new PasswordDictionary(dictionaryPath, onlinePassword: onlinePassword);
                dictionaryCount = dictionary.Count;

                var (Mode, WorkerCount, ThreadParallelism, DisplayParallelism) = planner.Plan(options.SevenZipDllPath, archiveInfo, archivePath, dictionaryCount);
                Ui.WriteArchiveInfo(
                    archivePath,
                    hashComputed ? archiveHash : null,
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
                if (!string.IsNullOrEmpty(foundPassword) && options.ReportOnline && onlineEnabled)
                {
                    ComputeArchiveHash();
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
                if (archiveEncrypted && !string.IsNullOrEmpty(archivePath) && !string.IsNullOrEmpty(dictionaryPath))
                {
                    // 哈希按需计算：用户确认保存后才读文件（见 WriteIfRequested 内部调用顺序）
                    ResultsWriter.WriteIfRequested(
                        archivePath,
                        dictionaryPath,
                        foundPassword,
                        stopwatch.Elapsed,
                        () =>
                        {
                            ComputeArchiveHash();
                            return archiveHash;
                        });
                }

                // 更新检查结果放到最后消费：不与测试进度条、结果确认抢屏幕
                Upgrade.CompleteBackgroundCheck();
            }
        }

        /// <summary>
        /// 计算压缩包哈希（惰性、幂等）：Base64 版给结果文件，Hex 版给在线服务。
        /// 分块 TransformBlock 计算并驱动进度条按字节推进（大文件不再黑屏干等）
        /// </summary>
        private void ComputeArchiveHash()
        {
            if (hashComputed)
            {
                return;
            }

            using var file = File.OpenRead(archivePath);
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

            hashComputed = true;
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
                                context = new ArchiveContext(dllPath, archivePath, password, archiveInfo);
                            }

                            // 快速测试目标（最小加密文件索引）由 ArchiveContext 打开归档时算好并缓存，
                            // 这里不再逐密码新建索引数组
                            if (context.TestPassword(password))
                            {
                                foundPassword = password;
                                stopwatch.Stop();
                                state.Stop();
                            }
                        }
                        catch (ArchivePasswordException)
                        {
                            // 只吞"预期内的密码不匹配"（header 加密 / 全加密包在 Open 阶段校验密码）。
                            // 其余异常（7z.dll 加载失败、OOM、未知格式等）不再静默——
                            // 否则真实故障会被伪装成"未找到密码"
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
