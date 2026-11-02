using ArchivePasswordTestTool.Properties;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        /// <summary>
        /// 归档是否已成功探测（决定是否询问保存报告）。
        /// <para>
        /// 不能用「是否加密」守卫：未加密的包同样值得留一份报告——里面有哈希、用时，
        /// 以及「未加密」这个结论，是「我检查过这个包」的凭证。
        /// 真正不该问的是探测就失败的路径（格式不识别、缺参数、7z 初始化失败），
        /// 那些连归档信息都拿不到。
        /// </para>
        /// </summary>
        private bool archiveInspected;

        /// <summary>归档是否加密（决定报告里的密码行写密码 / 未找到 / 未加密）</summary>
        private bool archiveEncrypted = true;

        private long dictionaryCount;
        private long testedCount;
        private string? foundPassword;

        /// <summary>
        /// 出错时的异常信息。留到离开备用屏幕后再打印——备用缓冲区切回主屏时内容整体消失，
        /// 在缓冲区里打印等于没打印
        /// </summary>
        private string? errorMessage;

        /// <summary>Ctrl+C 是否已被接管（防止连按两次重复替换）</summary>
        private int cancelHandled;

        /// <summary>执行测试主流程并返回退出码（0 成功 / 1 出错）</summary>
        public int Run()
        {
            int exitCode = 0;

            // Ctrl+C 也要能把已就绪的更新装上：默认行为是立刻杀进程，连替换都来不及
            Console.CancelKeyPress += OnCancelKeyPress;
            try
            {
                Ui.RunInAlternateScreenWhenSupported(() => exitCode = RunCore());
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKeyPress;
            }

            if (errorMessage is not null)
            {
                Ui.WriteError(errorMessage);
            }

            // 测密结束（正常结束或出错）：新版本已下载并校验通过就当场替换并重启一次，
            // 不必等用户下次启动。输出被重定时不重启——子进程的提示会混进调用方的数据流，
            // 而且自动化场景也不需要“立刻生效”。
            if (!Console.IsOutputRedirected)
            {
                PendingUpdate.TryApplyAndRestart(args);
            }

            return exitCode;
        }

        /// <summary>
        /// Ctrl+C：若已有校验通过的更新，先完成自我替换并重启再退出，而不是让进程被直接杀掉；
        /// 没有待应用更新时保持默认行为（立即终止）。
        /// </summary>
        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            if (Interlocked.Exchange(ref cancelHandled, 1) == 1 || !PendingUpdate.IsReady)
            {
                return;
            }

            // 取消默认终止：否则进程在替换完成前就被杀掉，.new 只能留到下次启动
            e.Cancel = true;

            ConsoleTitle.Restore();
            Ui.LeaveAlternateScreen();

            // 中断本身不算成功：没替换上就按惯例返回 130（128 + SIGINT）
            Environment.Exit(PendingUpdate.TryApplyAndRestart(args) ? 0 : 130);
        }

        private int RunCore()
        {
            try
            {
                // 标题最先设：后面读字典、算哈希都可能要几秒到几十秒，
                // 那段时间用户看到的应该已经是本程序，而不是 shell 遗留的标题
                ConsoleTitle.Initialize();

                // 启动流程微调：头部最先展示，其后依次是 7z 环境解析、
                // 路径引导/参数校验、初始化与分析
                if (AnsiConsole.Profile.Out.IsTerminal)
                {
                    AnsiConsole.Clear();
                }
                Ui.WriteHeader();

                // 上一次启动完成了自我替换：明确告知，避免用户误以为「版本号变了但没更新」
                if (PendingUpdate.JustUpdated)
                {
                    Ui.WriteWarning(string.Format(Resources.UpgradeApplied, Program.VersionText));
                }

                var options = Options.Parse(args);
                archivePath = options.ArchivePath;
                dictionaryPath = options.DictionaryPath;

                SevenZip.Initialize(options.SevenZipDllPath);

                // 在线服务只在真正配置了地址时才可能用到哈希；
                // 占位地址（未部署服务）下绝不为此读一遍整个压缩包
                bool onlineEnabled = Sync.IsConfigured(options.OnlineServiceUrl);

                using var archiveContext = new ArchiveContext(options.SevenZipDllPath, archivePath, password: null);
                var archiveInfo = archiveContext.Info;

                // 归档已打开、信息已知：后续无论加密与否都要给用户留一份报告
                archiveInspected = true;

                if (!archiveInfo.IsEncrypted)
                {
                    archiveEncrypted = false;
                    AnsiConsole.WriteLine(string.Format(Resources.ArchiveNotEncrypted, archivePath));
                    return 0;
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

                // 非 UTF-8 字典（典型：简中 Windows 记事本另存的 ANSI/GBK）照旧能正常破解，
                // 但要让用户知道密码文本是按哪种编码解读的，否则看到"怪字符"会以为读错了
                if (dictionary.Encoding.CodePage != Encoding.UTF8.CodePage)
                {
                    Ui.WriteWarning(string.Format(
                        Resources.DictionaryEncodingDetected, dictionary.Encoding.WebName));
                }

                // 快速判错能力探测放在规划之前：走快速路径时不必预建上下文池
                // （预建要为每个并行度打开一次归档，而确认阶段只用一次上下文）
                IPasswordProbe? fastProbe = FastFilter.IsEnabled
                    ? PasswordProbeFactory.TryCreate(archivePath, archiveInfo)
                    : null;

                var (Mode, WorkerCount, ThreadParallelism, DisplayParallelism) = planner.Plan(
                    options.SevenZipDllPath, archiveInfo, archivePath, dictionaryCount,
                    skipContextPool: fastProbe is not null);

                // 快速筛选是纯 CPU 的密钥派生，SMT 有效：用逻辑核数（实测 16 线程 650/秒 vs 8 线程 499/秒）。
                // APTT_PARALLELISM 作为总并发上限，用户显式限制时一并遵守
                int fastParallelism = fastProbe is null ? 0 : ResolveFastParallelism();

                Ui.WriteArchiveInfo(
                    archivePath,
                    hashComputed ? archiveHash : null,
                    archiveInfo.Format,
                    archiveInfo.FileCount,
                    archiveInfo.IsHeaderEncryption,
                    archiveInfo.SupportsQuickTest,
                    dictionaryPath,
                    dictionaryCount,
                    fastProbe is null ? DisplayParallelism : fastParallelism);

                if (fastProbe is not null)
                {
                    // 明确告知走的是"只派生密钥、不解包"的路径：此时"未找到"的语义
                    // 依赖文件格式签名，用户需要知道这个前提
                    Ui.WriteWarning(string.Format(Resources.FastFilterActive, fastProbe.ContentFormat));
                    RunFastFilter(options.SevenZipDllPath, archiveInfo, dictionary, fastProbe, fastParallelism);
                }
                else
                {
                    // 完整测密前先按格式特征预警：Copy/Store 编码在密文上没有任何可校验结构，
                    // 每个错误密码都要解完整个数据才能靠 CRC 判错，多进程也救不了。
                    // 放在这里而不是 Planner 里，是因为需要字典条数才能估算总耗时
                    Ui.WarnSlowFullDecodeIfNeeded(archiveInfo, dictionaryCount);

                    if (Mode == ExecutionMode.MultiProcess)
                    {
                        RunMultiProcess(options.SevenZipDllPath, dictionary, WorkerCount);
                    }
                    else
                    {
                        RunThreaded(options.SevenZipDllPath, archiveInfo, dictionary, ThreadParallelism);
                    }
                }

                Ui.WriteOutcome(
                    !string.IsNullOrEmpty(foundPassword),
                    archivePath,
                    dictionaryPath,
                    foundPassword,
                    stopwatch.Elapsed,
                    Interlocked.Read(ref testedCount));

                if (string.IsNullOrEmpty(foundPassword))
                {
                    ConsoleTitle.ShowNotFound();
                }
                else
                {
                    ConsoleTitle.ShowFound();
                }

                // 密码回传（默认开启）：找到即把 哈希+密码 POST 给服务端供他人共享；失败只提示
                if (!string.IsNullOrEmpty(foundPassword) && options.ReportOnline && onlineEnabled)
                {
                    ComputeArchiveHash();
                    Sync.ReportFoundPassword(options.OnlineServiceUrl, archiveHashHex, foundPassword);
                }

                return 0;
            }
            catch (Exception ex)
            {
                // 所有启动/测密异常处理完全相同（打印 + exit 1），原先分成两个 catch 块只是重复。
                // 错误信息留到离开备用屏幕后由 Run 打印（见 errorMessage 的说明）
                errorMessage = ex.Message;
                return 1;
            }
            finally
            {
                // 出错时既不提示更新也不问是否保存结果：屏幕即将切回主屏，提示都看不到，
                // 而没有有效结果时「是否保存」的阻塞式确认也没有意义
                if (errorMessage is null)
                {
                    // 更新提示先于保存结果确认消费：后者含两个阻塞式确认（是否保存、是否打开报告），
                    // 用户若卡在那里（或直接关窗口）就再也看不到更新提示了。
                    // 两者不会抢屏幕：进度条已 AutoClear，结果面板也已输出完毕。
                    Upgrade.CompleteBackgroundCheck();

                    if (archiveInspected && !string.IsNullOrEmpty(archivePath) && !string.IsNullOrEmpty(dictionaryPath))
                    {
                        // 哈希按需计算：用户确认保存后才读文件（见 WriteIfRequested 内部调用顺序）
                        ResultsWriter.WriteIfRequested(
                            archivePath,
                            dictionaryPath,
                            foundPassword,
                            archiveEncrypted,
                            stopwatch.Elapsed,
                            () =>
                            {
                                ComputeArchiveHash();
                                return archiveHash;
                            });
                    }
                }

                // 放在 finally 的最后：上面的保存/打开报告确认可能让用户停留很久，
                // 那期间标题应该还是结果（或进度），不该提前变回壳层标题
                ConsoleTitle.Restore();
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

            CreateProgress()
                .Start(ctx =>
                {
                    var hashTask = ctx.AddTask(Resources.AnalyzingArchiveInformation, maxValue: fileLength);

                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        HashAlgorithm.TransformBlock(buffer, 0, read, null, 0);
                        hashTask.Increment(read);
                        ConsoleTitle.ShowProgress(Resources.TitleAnalyzing, hashTask.Value, hashTask.MaxValue);
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
        /// 快速筛选的并行度：逻辑核数（KDF 是纯 CPU、SMT 有效），
        /// 再受 APTT_PARALLELISM 总并发上限约束
        /// </summary>
        private static int ResolveFastParallelism()
        {
            int parallelism = Math.Max(1, Environment.ProcessorCount);

            if (int.TryParse(Environment.GetEnvironmentVariable("APTT_PARALLELISM"), out int cap) && cap > 0)
            {
                parallelism = Math.Min(parallelism, cap);
            }

            return parallelism;
        }

        /// <summary>
        /// 快速筛选模式：每个候选只派生 RAR3 密钥 + 解密首块（约 10ms），
        /// 通过格式签名的极少数候选再由 7-Zip 完整解包确认。
        /// <para>
        /// 关键约束：筛选结果**绝不**直接作为"找到密码"。签名匹配只是强提示，
        /// 权威判定仍然是 7-Zip 的真实解包（含文件 CRC 校验）。
        /// </para>
        /// </summary>
        private void RunFastFilter(
            string dllPath,
            ArchiveInfo archiveInfo,
            PasswordDictionary dictionary,
            IPasswordProbe probe,
            int parallelism)
        {
            stopwatch.Restart();

            CreateProgress()
                .Start(ctx =>
                {
                    var testProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: dictionaryCount);

                    var result = FastFilter.Run(
                        probe,
                        dictionary,
                        parallelism,
                        delta =>
                        {
                            Interlocked.Add(ref testedCount, delta);
                            testProgressBar.Increment(delta);
                            ConsoleTitle.ShowProgress(Resources.TestingPasswordProgress, testProgressBar.Value, testProgressBar.MaxValue);
                        });

                    if (result.Completed)
                    {
                        // 字典确实扫完了：拉满进度（提前停止时剩余密码并未测试）
                        testProgressBar.Value = testProgressBar.MaxValue;
                    }

                    // 超长密码无法走快速路径，与命中候选一起交给 7-Zip 权威确认
                    var toConfirm = result.Candidates
                        .Concat(result.Unsupported)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (toConfirm.Count == 0)
                    {
                        return;
                    }

                    using var context = new ArchiveContext(dllPath, archivePath, password: null, archiveInfo);
                    foreach (string candidate in toConfirm)
                    {
                        if (context.TestPassword(candidate))
                        {
                            foundPassword = candidate;
                            stopwatch.Stop();
                            break;
                        }
                    }
                });
        }

        /// <summary>
        /// 多进程模式：主进程负责字典分发（去重/线程安全），密码逐批经管道送入
        /// N 个自身副本；每个副本加载独立的 7z.dll（独立全局锁），绕开 KDF 串行化。
        /// 进度条由主进程按 worker 回传的批测试计数推进
        /// </summary>
        private void RunMultiProcess(string dllPath, PasswordDictionary dictionary, int workerCount)
        {
            stopwatch.Restart();

            CreateProgress()
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
                            ConsoleTitle.ShowProgress(Resources.TestingPasswordProgress, testProgressBar.Value, testProgressBar.MaxValue);
                        },
                        onFound: password =>
                        {
                            foundPassword = password;
                            stopwatch.Stop();
                        });

                    // 只在真正跑完（字典耗尽）时才拉满：找到密码后剩余密码并未测试，
                    // 拉满会谎称 100%，而重定向输出下用户就是靠这个百分比判断进度
                    if (string.IsNullOrEmpty(foundPassword))
                    {
                        testProgressBar.Value = testProgressBar.MaxValue;
                    }
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

            CreateProgress()
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
                            ConsoleTitle.ShowProgress(Resources.TestingPasswordProgress, testProgressBar.Value, testProgressBar.MaxValue);
                        }
                    });

                    // 只在真正跑完（字典耗尽）时才拉满：找到密码后 state.Stop() 提前退出，
                    // 剩余密码并未测试，拉满会谎称 100%
                    if (string.IsNullOrEmpty(foundPassword))
                    {
                        testProgressBar.Value = testProgressBar.MaxValue;
                    }
                });

            planner.DisposeAll();
        }

        /// <summary>
        /// 进度条统一样式（哈希计算与两处测密的列配置此前各写一份，且缩进都跑偏了）。
        /// 重定向/非终端环境下 Spectre 自动退化，测试安全。
        /// </summary>
        private static Progress CreateProgress() =>
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
                ]);
    }
}
