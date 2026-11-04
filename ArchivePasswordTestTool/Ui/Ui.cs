using ArchivePasswordTestTool.Properties;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// Spectre.Console 界面输出辅助，集中处理标题、信息面板与结果面板
    /// </summary>
    internal static class Ui
    {
        /// <summary>
        /// 转义文本中的标记字符，防止路径等内容破坏 Markup 解析
        /// </summary>
        public static string Escape(string? value)
        {
            return Markup.Escape(value ?? string.Empty);
        }

        /// <summary>当前是否处于备用屏幕缓冲区（供 <see cref="LeaveAlternateScreen"/> 判定）</summary>
        private static bool alternateScreenActive;

        /// <summary>
        /// 终端支持 ANSI 且支持备用屏幕缓冲时，在备用屏幕中执行 action：
        /// 进入时自动切换（CSI ?1049h），委托返回或异常时在 finally 中自动切回主屏
        /// （CSI ?1049l），主屏原始内容完整保留。
        /// 任一能力不可用（不支持 ANSI 的终端/输出被重定向）时静默回退：
        /// 直接在当前主屏执行——传统模式，行为与旧版完全一致
        /// </summary>
        public static void RunInAlternateScreenWhenSupported(Action action)
        {
            bool alternateScreenSupported = AnsiConsole.Profile.Capabilities.Ansi
                && AnsiConsole.Profile.Capabilities.AlternateBuffer;

            if (!alternateScreenSupported)
            {
                action();
                return;
            }

            alternateScreenActive = true;
            try
            {
                AnsiConsole.AlternateScreen(() =>
                {
                    // 切换缓冲区会重置窗口字体：conhost 的备用缓冲区有独立的一份字体，
                    // 在外面设好的字体会被恢复成控制台默认字体（中文 Windows 是新宋体，
                    // 会把 ± · × 这类歧义宽度字符画成两格）。必须在首帧绘制前再修正一次，
                    // 否则进度画面会逐帧错位。非 Windows / 重定向时该方法内部直接返回。
                    _ = WindowsNativeMethods.EnsureNarrowAmbiguousWidth();

                    action();
                });
            }
            finally
            {
                alternateScreenActive = false;
            }
        }

        /// <summary>
        /// 立即切回主屏幕缓冲区。只给「不经过正常返回路径」的场景用（Ctrl+C 接管后要继续做事），
        /// 正常路径由 <see cref="RunInAlternateScreenWhenSupported"/> 的 finally 负责。
        /// </summary>
        public static void LeaveAlternateScreen()
        {
            if (!alternateScreenActive)
            {
                return;
            }

            alternateScreenActive = false;

            try
            {
                Console.Out.Write("\u001b[?1049l");
                Console.Out.Flush();
            }
            catch
            {
                // 控制台已失效（被关闭等）：忽略
            }
        }

        /// <summary>
        /// 程序头部：ASCII 标题 + 版本/作者/主页分隔线
        /// </summary>
        public static void WriteHeader()
        {
            AnsiConsole.WriteLine(Banner.Text);

            var footer = new Rule($"[grey]v{Escape(Program.VersionText)} · {Escape(Resources.Developer)} · {Escape(Resources.AppHomePage)}[/]")
            {
                Style = Style.Parse("grey dim"),
                Justification = Justify.Left,
            };
            AnsiConsole.Write(footer);
        }

        /// <summary>
        /// 压缩包与测试参数信息面板（开始测试前展示）。
        /// <para>
        /// 除了「是什么包」之外，重点回答**用户真正关心的两个问题**：
        /// ① 会不会慢 —— 压缩方式与测密目标大小直接决定每个错误密码的代价；
        /// ② 测的到底是哪部分数据 —— 是最小加密文件、全部加密数据，还是仅仅文件头。
        /// 早期版本只写「加密状态：是」「快速测试：是」，用户无法判断自己的包属于哪种情形。
        /// </para>
        /// <para>
        /// <paramref name="archiveHash"/> 为 null 表示哈希尚未按需计算（离线且不需要写结果文件时），此时整行省略。
        /// </para>
        /// </summary>
        public static void WriteArchiveInfo(
            ArchiveInfo info,
            string archivePath,
            string? archiveHash,
            string dictionaryPath,
            long passwordCount,
            int parallelism)
        {
            var grid = new Grid();
            _ = grid.AddColumn();
            _ = grid.AddColumn();

            AddRow(grid, Resources.InfoArchive, archivePath);
            AddRow(grid, Resources.InfoFormat, info.Format);

            // 磁盘占用只在「确实代表整个包、且与压缩后大小明显不同」时才写：
            // 分卷用另一行显示合计；普通包只差几十字节的头部开销，
            // 写出来与「大小」一行重复（反例：RAR 的 .part1.rar 只传入第一个分卷时
            // 本类读到的 8 KB 不代表整个归档）。header 加密读不到压缩量（为 0），
            // 这时磁盘数就是唯一能给的数字
            if (info.VolumeCount > 1)
            {
                AddRow(grid, Resources.InfoVolumes, string.Format(
                    Resources.ValueVolumeCount,
                    info.VolumeCount,
                    CostEstimate.FormatBytes((ulong)Math.Max(0, info.DiskSize))));
            }
            else if (HasNotableDiskOverhead(info))
            {
                AddRow(grid, Resources.InfoDiskSize, CostEstimate.FormatBytes((ulong)info.DiskSize));
            }

            // header 加密时条目数为 0 只代表「读不到」，不代表包是空的——写 0 会误导
            if (!info.IsHeaderEncryption || info.FileCount > 0)
            {
                AddRow(grid, Resources.InfoFiles, info.FileCount.ToString());
            }

            // 大小与压缩方式只有在 header 未加密时才读得到；读不到就整行省略，
            // 而不是显示一个会被误读的 0
            if (info.UnpackedSize > 0)
            {
                AddRow(grid, Resources.InfoUnpackedSize, FormatSizeWithRatio(info));
            }

            if (info.Method is not null)
            {
                AddRow(grid, Resources.InfoMethod, info.Method);
            }

            if (info.IsSolid)
            {
                AddRow(grid, Resources.InfoSolid, Resources.ValueYes);
            }

            AddRow(grid, Resources.InfoEncryption, DescribeEncryption(info));
            AddRow(grid, Resources.InfoTestTarget, DescribeTestTarget(info));

            // 非归档数据：自解压 stub、被拼接的文件头、尾随垃圾都属于这一类。
            // 它们不影响测密（7-Zip 会自己跳过），但用户看到文件大小与包内大小对不上时，
            // 这一行就是解释；也是「可以分离出干净副本」的唯一提示
            if (HasNonArchiveData(info))
            {
                AddRow(grid, Resources.InfoNonArchiveData, DescribeNonArchiveData(info));
            }

            if (archiveHash is not null)
            {
                AddRow(grid, Resources.InfoHash, archiveHash);
            }

            AddRow(grid, Resources.InfoDictionary, dictionaryPath);
            AddRow(grid, Resources.InfoPasswords, passwordCount.ToString());
            AddRow(grid, Resources.InfoParallelism, parallelism.ToString());

            var panel = new Panel(grid)
                .Header($"[bold cyan]{Escape(Resources.InfoTitle)}[/]")
                .RoundedBorder()
                .BorderColor(Color.Cyan1);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }

        /// <summary>
        /// 文件里是否存在不属于归档的字节（前置 stub/拼接数据 或 尾随垃圾）。
        /// 尾随量未知（null）时不算「有」——不能拿「不知道」当结论展示。
        /// </summary>
        private static bool HasNonArchiveData(ArchiveInfo info) =>
            info.SignatureOffset > 0 || info.TrailingBytes > 0;

        /// <summary>
        /// 「前 X · 后 Y」：只列出真实存在的部分。尾随量未知时整段省略，
        /// 因为「未知」写成 0 或 <c>?</c> 都会被读成确定的结论。
        /// </summary>
        private static string DescribeNonArchiveData(ArchiveInfo info)
        {
            var parts = new List<string>(2);

            if (info.SignatureOffset > 0)
            {
                parts.Add(string.Format(
                    Resources.ValueNonArchiveLeading,
                    CostEstimate.FormatBytes((ulong)info.SignatureOffset)));
            }

            if (info.TrailingBytes is long trailing && trailing > 0)
            {
                parts.Add(string.Format(
                    Resources.ValueNonArchiveTrailing,
                    CostEstimate.FormatBytes((ulong)trailing)));
            }

            return string.Join(" · ", parts);
        }

        /// <summary>
        /// 原始大小 + 压缩后大小 + 压缩比。压缩比按「压缩后 ÷ 原始」算，
        /// 小于 100% 说明压缩生效；Store/Copy 的包通常是 100% 上下，这个数字本身就是诊据。
        /// </summary>
        private static string FormatSizeWithRatio(ArchiveInfo info)
        {
            string unpacked = CostEstimate.FormatBytes(info.UnpackedSize);

            if (info.PackedSize == 0)
            {
                return unpacked;
            }

            double percent = 100.0 * info.PackedSize / info.UnpackedSize;
            return string.Format(
                Resources.ValueSizeWithRatio,
                unpacked,
                CostEstimate.FormatBytes(info.PackedSize),
                percent.ToString("0.#"));
        }

        /// <summary>
        /// 加密范围。三种情形对用户意义完全不同：header 加密时密码错误连文件名都看不到，
        /// 且验证发生在 Open 阶段（最快）；仅数据加密时则要解数据才能判错。
        /// </summary>
        private static string DescribeEncryption(ArchiveInfo info) => info switch
        {
            { IsHeaderEncryption: true } => Resources.ValueEncryptionHeader,
            { IsEncrypted: true } => Resources.ValueEncryptionDataOnly,
            _ => Resources.ValueEncryptionNone,
        };

        /// <summary>
        /// 实际会被测试的数据范围——这是「每个错误密码要付出多少代价」的直接答案。
        /// <para>
        /// 判据用 <see cref="ArchiveInfo.QuickTestFileIndices"/> 而不是
        /// <see cref="ArchiveInfo.SupportsQuickTest"/>：后者的含义是「部分加密且存在合格目标」
        /// （给快筛探测器判适用范围用），而这里要知道的是「7-Zip 到底会解哪几个条目」。
        /// 全加密包同样会挑一个最小文件来测，两者不是一回事。
        /// </para>
        /// </summary>
        private static string DescribeTestTarget(ArchiveInfo info)
        {
            // header 加密：Open 阶段就验完密码，不碰任何文件数据
            if (info.IsHeaderEncryption)
            {
                return Resources.ValueTestTargetHeader;
            }

            if (info.QuickTestFileIndices is { Length: > 0 } && info.QuickTestEntryName is not null)
            {
                return string.Format(
                    Resources.ValueTestTargetQuickFile,
                    info.QuickTestEntryName,
                    CostEstimate.FormatBytes(info.QuickTestUnpackedSize));
            }

            return string.Format(
                Resources.ValueTestTargetAll,
                CostEstimate.FormatBytes(info.FailurePathBytes));
        }

        /// <summary>
        /// 测试结果面板：成功绿色、失败红色，附统计信息。
        /// <para>
        /// 找到的密码放在面板**内容**里而不是标题栏：Spectre 的 Panel Header 宽度受限时
        /// 会把超长内容截成 <c>…</c>（实测 52 字符以上的密码会整段变成省略号，用户看不到密码），
        /// 而 Grid 单元格会折行，完整内容不会丢。
        /// </para>
        /// <para>
        /// <paramref name="signatureFiltered"/> 为 true 表示「未找到」这个结论只来自快速筛选的签名比对，
        /// 没有经过完整解密复核。此时不能说「未找到正确的解压密码」——那是把不确定说成了确定。
        /// 标题改写为限定形式，并在面板里给出原因与补救开关。
        /// </para>
        /// </summary>
        public static void WriteOutcome(
            bool found,
            string archivePath,
            string dictionaryPath,
            string? password,
            TimeSpan elapsed,
            long tested,
            bool signatureFiltered = false)
        {
            string speed = elapsed.TotalSeconds > 0
                ? string.Format(Resources.ResultPwPerSec, Math.Round(tested / elapsed.TotalSeconds).ToString("N0"))
                : "-";

            var grid = new Grid();
            _ = grid.AddColumn();
            _ = grid.AddColumn();

            AddRow(grid, Resources.InfoArchive, archivePath);
            AddRow(grid, Resources.InfoDictionary, dictionaryPath);
            if (found)
            {
                // 密码本身不转义会破坏 Markup（如 [red]X[/] 会被渲染成样式），仍走同一个 Escape
                AddRow(grid, Resources.InfoPassword, password ?? string.Empty);
            }

            AddRow(grid, Resources.ResultTested, tested.ToString());
            AddRow(grid, Resources.ResultElapsed, elapsed.ToString());
            AddRow(grid, Resources.ResultSpeed, speed);

            bool tentative = !found && signatureFiltered;

            string header = found
                ? $"[bold green]√ {Escape(Resources.PasswordFoundSuccess)}[/]"
                : tentative
                    ? $"[bold yellow]× {Escape(Resources.PasswordNotFoundFiltered)}[/]"
                    : $"[bold red]× {Escape(Resources.PasswordNotFound)}[/]";

            Spectre.Console.Rendering.IRenderable content = grid;
            if (tentative)
            {
                content = new Rows(
                    grid,
                    new Markup(string.Empty),
                    new Markup($"[yellow]{Escape(Resources.ResultSignatureFilteredNote)}[/]"));
            }

            var panel = new Panel(content)
                .Header(header)
                .RoundedBorder()
                .BorderColor(found ? Color.Green : tentative ? Color.Yellow : Color.Red);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(panel);
        }

        /// <summary>
        /// 快速筛选否决全部候选后的决策面板（只在交互终端显示）。
        /// <para>
        /// 这里必须回答三个问题，否则用户无从决定：
        /// <list type="number">
        /// <item>结论是什么、不是什么——「否决全部」不等于「密码不在字典里」；</item>
        /// <item>什么情况下会漏——扩展名与真实内容不符，用户可以据此判断自己的包是否会中招；</item>
        /// <item>两个选项各自的代价——复核要多久，跳过会得到什么。</item>
        /// </list>
        /// 早先只有一句「已按文件签名排除全部 N 个密码，是否复核？预计 X」：
        /// 用户既不知道「按签名排除」意味着什么，也不知道不做的后果，等于把决定压给了一个无法判断的问题。
        /// </para>
        /// </summary>
        public static void WriteFastFilterExhausted(TimeSpan estimate)
        {
            var options = new Grid();
            _ = options.AddColumn();
            _ = options.AddColumn();
            _ = options.AddRow(
                $"[bold yellow]{Escape(Resources.FastFilterExhaustedReview)}[/]",
                $"[white]{Escape(string.Format(Resources.FastFilterExhaustedReviewCost, CostEstimate.FormatDuration(estimate)))}[/]");
            _ = options.AddRow(
                $"[grey]{Escape(Resources.FastFilterExhaustedSkip)}[/]",
                $"[grey]{Escape(Resources.FastFilterExhaustedSkipCost)}[/]");

            var content = new Rows(
                new Markup($"[white]{Escape(Resources.FastFilterExhaustedLead)}[/]"),
                new Markup(string.Empty),
                new Markup($"[grey]{Escape(Resources.FastFilterExhaustedReason)}[/]"),
                new Markup(string.Empty),
                options);

            var panel = new Panel(content)
                .Header($"[bold yellow]! {Escape(Resources.FastFilterExhaustedTitle)}[/]")
                .RoundedBorder()
                .BorderColor(Color.Yellow);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }

        /// <summary>
        /// 红色错误信息（转义后输出）
        /// </summary>
        public static void WriteError(string message)
        {
            AnsiConsole.MarkupLine($"[red]{Escape(message)}[/]");
        }

        /// <summary>
        /// 黄色警告信息（转义后输出）
        /// </summary>
        public static void WriteWarning(string message)
        {
            AnsiConsole.MarkupLine($"[yellow]{Escape(message)}[/]");
        }

        /// <summary>
        /// 灰色次要信息（转义后输出）：未配置提示、无记录提示、进行中的说明等
        /// </summary>
        public static void WriteMuted(string message)
        {
            AnsiConsole.MarkupLine($"[grey]{Escape(message)}[/]");
        }

        /// <summary>
        /// 完整测密前的慢速预警。
        /// <para>
        /// 针对「不压缩编码（7z Copy / zip Store）+ 仅数据加密」：密文没有任何可校验结构，
        /// 错误密码必须解完整个数据才能靠文件 CRC 判错。实测 200MB 单文件每个候选读满
        /// 126MB；1.2GB + 250 万字典约 100 小时，且增加并行度无效（瓶颈是内存带宽/IO）。
        /// </para>
        /// <para>
        /// 只在预计耗时超过一小时时提示——正常压缩包（LZMA 等首块即判错）不会被误报。
        /// 预估刻意取乐观吞吐，宁可少提示也不要把正常场景说成"要跑几小时"。
        /// </para>
        /// </summary>
        public static void WarnSlowFullDecodeIfNeeded(ArchiveInfo info, long passwordCount)
        {
            // header 加密在 Open 阶段就验完密码，不存在这个问题
            if (!info.FailureRequiresFullDecode || info.IsHeaderEncryption || info.FailurePathBytes == 0)
            {
                return;
            }

            TimeSpan estimate = CostEstimate.Estimate(passwordCount, info.FailurePathBytes);
            if (!CostEstimate.ShouldWarn(estimate))
            {
                return;
            }

            AnsiConsole.WriteLine();
            WriteWarning(string.Format(
                Resources.SlowFullDecodeWarning,
                CostEstimate.FormatBytes(info.FailurePathBytes),
                CostEstimate.FormatDuration(estimate)));
            AnsiConsole.WriteLine();
        }

        /// <summary>
        /// 磁盘占用与条目压缩后总量是否值得分别展示。两种情况要显示：
        /// ① 读不到压缩量（header 加密，<see cref="ArchiveInfo.PackedSize"/> 为 0）——
        ///   磁盘大小是唯一能给的数字；
        /// ② 磁盘明显更大（自解压包的 PE stub、或只传了第一个分卷的反向错位）。
        /// 差异小于 5% 时只是几十字节的头部开销，与「大小」一行重复，不显示。
        /// </summary>
        private static bool HasNotableDiskOverhead(ArchiveInfo info)
        {
            if (info.DiskSize <= 0)
            {
                return false;
            }

            return info.PackedSize == 0
                || (double)info.DiskSize > info.PackedSize * 1.05;
        }

        private static void AddRow(Grid grid, string label, string value)
        {
            _ = grid.AddRow($"[grey]{Escape(label)}[/]", $"[white]{Escape(value)}[/]");
        }
    }
}
