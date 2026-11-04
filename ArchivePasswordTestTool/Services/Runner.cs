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
        /// 「未找到」这个结论是否只来自快速筛选的签名比对（没有做完整解密复核）。
        /// 决定结果面板用哪一种标题：不确定的结论不能用「未找到正确的解压密码」这种肯定语气。
        /// </summary>
        private bool signatureFiltered;

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
                _ = PendingUpdate.TryApplyAndRestart(args);
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

                // 显式要求时把归档部分单独拷出来。放在加密判断之前：
                // 「这个文件里哪部分才是归档」与加不加密无关，未加密的包同样值得导出一份
                if (options.SeparateArchive)
                {
                    TrySeparateArchive(archiveInfo, archivePath);
                }

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
                    archiveInfo,
                    archivePath,
                    hashComputed ? archiveHash : null,
                    dictionaryPath,
                    dictionaryCount,
                    fastProbe is null ? DisplayParallelism : fastParallelism);

                if (fastProbe is not null)
                {
                    // 明确告知走的是"只派生密钥、不解包"的路径：此时"未找到"的语义
                    // 依赖文件格式签名，用户需要知道这个前提
                    Ui.WriteWarning(string.Format(Resources.FastFilterActive, fastProbe.ContentFormat));
                    RunFastFilter(options.SevenZipDllPath, archiveInfo, dictionary, fastProbe, fastParallelism);

                    // 快筛按签名否决了全部候选：结论依赖「正确密码解出的首块必然匹配已知格式」，
                    // 扩展名撒谎或载荷被剥头时会漏。这里给用户一次用完整解密复核的机会
                    if (string.IsNullOrEmpty(foundPassword))
                    {
                        // 复核没做的活，「未找到」就只是签名比对的结论，不是终局：
                        // 结果面板必须把这一点说出来，否则等于把不确定当确定报给用户
                        signatureFiltered = !TryFallbackToFullDecode(options, archiveInfo, onlinePassword);
                    }
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
                    Interlocked.Read(ref testedCount),
                    signatureFiltered);

                if (string.IsNullOrEmpty(foundPassword))
                {
                    // 标题同样不能把「筛掉了」说成「没找到」：那是在窗口标题上留一条假结论
                    ConsoleTitle.ShowNotFound(signatureFiltered);
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
                            signatureFiltered,
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
        /// 把归档数据单独拷成 <c>&lt;名称&gt;[clean]&lt;扩展名&gt;</c>（-SeparateArchive）。
        /// <para>
        /// 三种情形明确拒绝而不是尽力而为：分卷（多文件拼成的流，切成单文件既不符合格式也不符合预期）、
        /// 末尾未知（头部加密的 RAR5 等，猜一个长度写出来只会得到坏文件）、
        /// 本来就没有多余数据（写一份与原文等大的副本纯属浪费磁盘）。
        /// </para>
        /// <para>
        /// 范围直接取自探测结果：<see cref="ArchiveInfo.SignatureOffset"/> 是起点，
        /// 终点 = 文件长度 - <see cref="ArchiveInfo.TrailingBytes"/>，不重新解析一遍。
        /// </para>
        /// </summary>
        private static void TrySeparateArchive(ArchiveInfo info, string archivePath)
        {
            if (info.VolumeCount > 1)
            {
                Ui.WriteWarning(Resources.SeparateArchiveMultiVolume);
                return;
            }

            if (info.TrailingBytes is not long trailing)
            {
                Ui.WriteWarning(Resources.SeparateArchiveUnknownEnd);
                return;
            }

            bool hasLeadingData = info.SignatureOffset > 0;
            if (!hasLeadingData && trailing == 0)
            {
                Ui.WriteWarning(Resources.SeparateArchiveNotNeeded);
                return;
            }

            string targetPath = ArchiveSeparator.BuildTargetPath(archivePath);
            ArchiveSeparator.Write(archivePath, info.SignatureOffset, info.DiskSize - trailing, targetPath);

            Ui.WriteWarning(string.Format(Resources.SeparateArchiveWritten, targetPath));
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
            using SHA256 hashAlgorithm = SHA256.Create();
            long fileLength = Math.Max(1, file.Length);

            CreateProgress()
                .Start(ctx =>
                {
                    var hashTask = ctx.AddTask(Resources.AnalyzingArchiveInformation, maxValue: fileLength);

                    byte[] buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        _ = hashAlgorithm.TransformBlock(buffer, 0, read, null, 0);
                        hashTask.Increment(read);
                        ConsoleTitle.ShowProgress(Resources.TitleAnalyzing, hashTask.Value, hashTask.MaxValue);
                    }

                    _ = hashAlgorithm.TransformFinalBlock([], 0, 0);
                    var hashBytes = hashAlgorithm.Hash!;
                    archiveHash = Convert.ToBase64String(hashBytes);
                    archiveHashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

                    // 空文件无字节可读，强制补满避免进度条停在起点
                    hashTask.Value = hashTask.MaxValue;
                });

            hashComputed = true;
        }

        /// <summary>
        /// 快速筛选的并行度：逻辑核数（KDF 是纯 CPU、SMT 有效），
        /// 再受 APTT_PARALLELISM 总并发上限约束（与全量解密、多进程共用同一解释）
        /// </summary>
        private static int ResolveFastParallelism() =>
            ParallelismLimit.Apply(Math.Max(1, Environment.ProcessorCount));

        /// <summary>
        /// 快速筛选模式：每个候选只派生 RAR4 密钥 + 解密首块（约 10ms），
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

            CreateProgress(showRate: true)
                .Start(ctx =>
                {
                    var testProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: dictionaryCount);

                    ArchiveContext? context = null;
                    try
                    {
                        // 候选上限会让扫描提前停止（命中候选即停）。若这些候选全被 7-Zip 否决，
                        // 剩余密码**根本没被测试过**，不能就此报「未找到」——必须继续扫完。
                        // 这是一个循环而不是一次扫描：每轮最多收集一批候选，确认失败就接着扫。
                        while (true)
                        {
                            var result = FastFilter.Run(
                                probe,
                                dictionary,
                                parallelism,
                                delta =>
                                {
                                    _ = Interlocked.Add(ref testedCount, delta);
                                    testProgressBar.Increment(delta);
                                    ConsoleTitle.ShowProgress(Resources.TestingPasswordProgress, testProgressBar.Value, testProgressBar.MaxValue);
                                });

                            // 超长密码无法走快速路径，与命中候选一起交给 7-Zip 权威确认
                            var toConfirm = result.Candidates
                                .Concat(result.Unsupported)
                                .Distinct(StringComparer.Ordinal)
                                .ToList();

                            if (toConfirm.Count > 0)
                            {
                                context ??= new ArchiveContext(dllPath, archivePath, password: null, archiveInfo);
                                foreach (string candidate in toConfirm)
                                {
                                    if (context.TestPassword(candidate))
                                    {
                                        foundPassword = candidate;
                                        stopwatch.Stop();
                                        break;
                                    }
                                }

                                if (foundPassword is not null)
                                {
                                    return;
                                }
                            }

                            if (result.Completed)
                            {
                                // 字典确实扫完了：拉满进度（此时剩余密码确实不存在）
                                testProgressBar.Value = testProgressBar.MaxValue;
                                return;
                            }
                        }
                    }
                    finally
                    {
                        context?.Dispose();
                    }
                });
        }

        /// <summary>
        /// 快速筛选按签名否决全部候选后的「完整解密复核」。
        /// <para>
        /// 存在理由：快筛只承认「正确密码解出的首块匹配已知文件格式」这一前提。
        /// 当归档内扩展名与实际内容不符（400 错误页存成 <c>movie.mp4</c>）、或载荷被剥掉容器头
        /// （裸 PCM 存成 <c>.wav</c>）时，正确密码解出的首块不带任何已知魔数，会被当作错误密码丢弃，
        /// 用户得到「未找到」——实测三种格式各复现一例。
        /// </para>
        /// <para>
        /// 代价与是否值得由用户决定：即便签名不符，绝大多数情况下正确答案确实不在字典里，
        /// 而完整复核要付整个字典的解密代价（1.2GB 包 250 万条约 100 小时）。
        /// 因此默认不自动执行：
        /// <list type="bullet">
        /// <item>带 <c>-FallbackFullDecode</c> → 无条件复核（无人值守场景）</item>
        /// <item>否则在真实终端里询问一次（默认 N）</item>
        /// <item>输入或输出被重定向（<c>&gt; log.txt</c>、CI、管道）→ 静默跳过，行为与引入本功能前一致</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <returns>是否真的执行了完整复核。返回 false 表示「未找到」这个结论仍只来自签名比对。</returns>
        private bool TryFallbackToFullDecode(Options options, ArchiveInfo archiveInfo, string? onlinePassword)
        {
            TimeSpan estimate = CostEstimate.Estimate(dictionaryCount, archiveInfo.FailurePathBytes);

            // 只在真实交互终端里询问。输出被重定向（`> log.txt`、CI）时用户看不到提示，
            // 而 ReadKey 会一直等下去——那会把自动化任务挂死。判据与
            // InteractivePrompts.NeedConfirmBeforeExit 保持一致
            bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

            bool proceed;
            if (options.FallbackFullDecode)
            {
                proceed = true;
            }
            else if (interactive)
            {
                // 先摆出「为什么可能漏 / 两个选项各自代价」，再问。单句询问无法支撑这个决定
                Ui.WriteFastFilterExhausted(estimate);
                proceed = InteractivePrompts.ConfirmSingleKey(
                    $"[yellow]{Ui.Escape(Resources.FallbackFullDecodePrompt)}[/]", defaultYes: false);
            }
            else
            {
                proceed = false;
            }

            if (!proceed)
            {
                return false;
            }

            AnsiConsole.WriteLine();
            Ui.WriteWarning(string.Format(
                Resources.FallbackFullDecodeStarted, dictionaryCount.ToString("N0")));
            Ui.WarnSlowFullDecodeIfNeeded(archiveInfo, dictionaryCount);

            // 快筛是消费式的（Pop 即释放引用），原字典已空。重新读一遍文件：
            // 编码探测与去重一并重做，保证复核的密码集合与快筛看到的完全一致
            using var fallbackDictionary = new PasswordDictionary(
                dictionaryPath, onlinePassword: onlinePassword);

            // 快筛路径跳过了上下文池预建（skipContextPool），复核要用它
            var (Mode, WorkerCount, ThreadParallelism, _) = planner.Plan(
                options.SevenZipDllPath, archiveInfo, archivePath, fallbackDictionary.Count);

            // 计数与耗时归零：把两个阶段的数字混在一起会让「速度」失去意义，
            // 而用户此刻关心的是完整复核本身的吞吐
            _ = Interlocked.Exchange(ref testedCount, 0);

            if (Mode == ExecutionMode.MultiProcess)
            {
                RunMultiProcess(options.SevenZipDllPath, fallbackDictionary, WorkerCount);
            }
            else
            {
                RunThreaded(options.SevenZipDllPath, archiveInfo, fallbackDictionary, ThreadParallelism);
            }

            return true;
        }

        /// <summary>
        /// 多进程模式：主进程负责字典分发（去重/线程安全），密码逐批经管道送入
        /// N 个自身副本；每个副本加载独立的 7z.dll（独立全局锁），绕开 KDF 串行化。
        /// 进度条由主进程按 worker 回传的批测试计数推进
        /// </summary>
        private void RunMultiProcess(string dllPath, PasswordDictionary dictionary, int workerCount)
        {
            stopwatch.Restart();

            CreateProgress(showRate: true)
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
                            _ = Interlocked.Add(ref testedCount, acked);
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

            CreateProgress(showRate: true)
                .Start(ctx =>
                {
                    var testProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: dictionaryCount);

                    _ = Parallel.For(0, dictionaryCount, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, (i, state) =>
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

                            context = isFullyEncrypted
                                ? new ArchiveContext(dllPath, archivePath, password, archiveInfo)
                                : planner.RentOrCreate(dllPath, archivePath, archiveInfo);

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

                            _ = Interlocked.Increment(ref testedCount);
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
        /// 进度条统一样式（哈希计算与三处测密共用）。
        /// <para>
        /// <paramref name="showRate" /> 只对测密有意义：哈希进度是按字节推进的，「个/秒」没有语义，
        /// 测密才需要让用户实时看到当前速度。速度列紧跟在进度条+百分比之后。
        /// </para>
        /// 重定向/非终端环境下 Spectre 自动退化，测试安全。
        /// </summary>
        private static Progress CreateProgress(bool showRate = false)
        {
            List<ProgressColumn> columns =
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn()
            ];

            if (showRate)
            {
                columns.Add(new RateColumn());
            }

            columns.Add(new ElapsedTimeColumn());
            columns.Add(new RemainingTimeColumn());
            columns.Add(new SpinnerColumn());

            return AnsiConsole.Progress()
                .AutoClear(true)
                .HideCompleted(true)
                .Columns([.. columns]);
        }
    }
}
