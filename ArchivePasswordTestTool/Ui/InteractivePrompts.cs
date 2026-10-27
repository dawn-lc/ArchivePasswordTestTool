using ArchivePasswordTestTool.Properties;
using System.Text;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 交互希望保留的键盘/鼠标行为都在真实终端里才可用。
    /// 无参数启动时的 CLI 交互引导：
    /// 依次引导用户输入密码字典与压缩包路径，支持把文件拖入窗口；
    /// 拖放粘贴的路径在短时空闲后自动验收（无需回车），
    /// 手动输入的路径可按回车强制确认；
    /// 拖拽产生的首尾引号会被剥离（校验与取值均按剥离后路径判断）；
    /// 校验失败时原位重新询问。
    /// stdin 被重定向（管道/自动化调用）时回退为逐行读取并沿用同一套存在性校验。
    /// 单键 y/n 确认（Confirm）：无需回车，按 Y/N/回车任一键立即生效
    /// </summary>
    internal static class InteractivePrompts
    {
        /// <summary>拖放粘贴的空闲阈值：超过该时长无新按键即认为粘贴结束并尝试自动验收</summary>
        private const int PasteIdleMilliseconds = 250;

        /// <summary>判定"快速连续到达"（拖放/粘贴）的最大按键间隔；手动打字通常远大于此值</summary>
        private const int BurstGapMilliseconds = 60;

        /// <summary>
        /// 突发输入判定（纯函数，便于测试）：
        /// 相邻字符间隔 ≤ <see cref="BurstGapMilliseconds"/> → 认为属于拖放/粘贴字符流（粘性，
        /// 避免粘贴末尾偶发调度抖动导致整段识别失败）；
        /// 间隔超过 <see cref="PasteIdleMilliseconds"/> → 视为用户已停顿后重新输入，复位为手动打字；
        /// 介于两者之间 → 保持原判定。
        /// </summary>
        internal static bool UpdateBurstState(bool burstDetected, long gapMilliseconds)
        {
            if (gapMilliseconds <= BurstGapMilliseconds)
            {
                return true;
            }

            if (gapMilliseconds > PasteIdleMilliseconds)
            {
                return false;
            }

            return burstDetected;
        }

        /// <summary>等待输入时的轮询间隔，避免忙等</summary>
        private const int IdlePollMilliseconds = 10;

        /// <summary>
        /// 报错后的退出确认是否必要：输入/输出被重定向（管道/自动化）→ 否；
        /// 控制台为当前进程专属（仅 1 个进程附加，典型于双击启动）→ 退出后窗口随之关闭，
        /// 错误信息不可见，需要等待确认；与 shell 共享控制台（终端启动）→ 窗口保留，无需等待。
        /// 比"是否双击"的判断更贴合目的：等的是"退出后错误是否还看得到"。
        /// </summary>
        public static bool NeedConfirmBeforeExit()
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return false;
            }

            // 双击 console 子系统 exe 时 Windows 会分配专属控制台，进程数为 1；
            // 从终端运行时与 shell 共享，进程数 ≥ 2。GetConsoleProcessList 比
            // 查父进程名更可靠（不依赖 explorer 假设，快捷方式/启动器同样适用）
            var count = WindowsNativeMethods.GetConsoleProcessList(new uint[2], 2);
            return count <= 1;
        }

        /// <summary>
        /// 把控制台窗口带回前台。拖放文件到控制台后焦点停留在资源管理器，
        /// conhost 不会自动还焦点——用户只能先点一下窗口再继续输入。
        /// 检测到真实按键输入（拖放粘贴或打字）时主动调用，让焦点回到本窗口。
        /// 最佳努力：进程若无前台权限（ForegroundLock 限制）调用失败也无副作用。
        /// </summary>
        private static void RestoreFocus()
        {
            try
            {
                IntPtr hwnd = WindowsNativeMethods.GetConsoleWindow();
                if (hwnd != IntPtr.Zero)
                {
                    WindowsNativeMethods.SetForegroundWindow(hwnd);
                }
            }
            catch
            {
                // 最佳努力：失败忽略
            }
        }

        /// <summary>依次询问密码字典与压缩包路径，均要求文件真实存在</summary>
        public static (string DictionaryPath, string ArchivePath) AskPaths()
        {
            var dictionary = Ask(Resources.InteractiveDictionaryPrompt);
            var archive = Ask(Resources.InteractiveArchivePrompt);
            return (dictionary, archive);
        }

        /// <summary>单键 y/n 确认：Y=N=立即生效，回车取默认值；仅限交互终端（重定向输入时静默取默认）</summary>
        public static bool ConfirmSingleKey(string promptMarkup, bool defaultYes)
        {
            if (Console.IsInputRedirected)
            {
                return defaultYes;
            }

            AnsiConsole.Markup($"{promptMarkup} [grey][[y/n]][/] [grey]({(defaultYes ? "y" : "n")}):[/] ");

            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
                {
                    Environment.Exit(1);
                }

                bool? answer = key.Key switch
                {
                    ConsoleKey.Y => true,
                    ConsoleKey.N => false,
                    ConsoleKey.Enter => defaultYes,
                    _ => null,
                };

                if (answer is null && key.KeyChar is 'y' or 'Y' or 'n' or 'N')
                {
                    answer = char.ToLowerInvariant(key.KeyChar) == 'y';
                }

                if (answer is null)
                {
                    continue;
                }

                AnsiConsole.MarkupLine($"[dodgerblue1]{(answer.Value ? "Y" : "N")}[/]");
                return answer.Value;
            }
        }

        /// <summary>
        /// 报错后的退出确认：仅在"退出后错误信息不可见"时提示并等待任意键——
        /// 专属控制台（双击启动）或重定向之外。终端启动（共享控制台）直接返回，
        /// 不打扰。见 <see cref="NeedConfirmBeforeExit"/>。
        /// </summary>
        public static void WaitBeforeExit()
        {
            if (!NeedConfirmBeforeExit())
            {
                return;
            }

            AnsiConsole.MarkupLine($"[grey]{Resources.InteractivePressAnyKeyToExit}[/]");
            Console.ReadKey(true);
        }

        /// <summary>询问一个已存在文件的路径；交互终端用拖放即验收输入，重定向输入用逐行回退</summary>
        private static string Ask(string title)
        {
            if (Console.IsInputRedirected)
            {
                return AskByLine(title);
            }

            return AskByKeys(title);
        }

        /// <summary>
        /// 真实终端的路径输入：逐键读取（无需回车）。
        /// 拖放/粘贴是一串**快速连续**到达的字符（间隔远小于手动打字）：
        /// 检测到突发输入后，等按键流停止（空闲超阈值）再尝试自动验收；
        /// 文件存在 → 立即接受（拖入即开始）；不存在 → 回到输入状态并提示一次。
        /// 手动打字不触发自动验收（避免打到一半误报错），回车才验收。
        /// 回车 → 强制验收（不存在时换行报错并重印提示）；退格 → 删除末字符。
        /// </summary>
        private static string AskByKeys(string title)
        {
            AnsiConsole.Markup(title);
            var buffer = new StringBuilder();
            var lastAlerted = string.Empty;
            var focusRestored = false;
            long lastKeyTick = 0;          // 上一个字符到达时刻（Environment.TickCount64）
            bool burstDetected = false;    // 当前输入是否来自拖放/粘贴的快速字符流

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
                    {
                        Environment.Exit(1);
                    }

                    // 首个输入到达（拖放粘贴/打字）时把控制台带回前台：
                    // 拖放后焦点仍在资源管理器，不还焦点用户只能先点窗口再继续
                    if (!focusRestored)
                    {
                        RestoreFocus();
                        focusRestored = true;
                    }

                    if (key.Key == ConsoleKey.Enter)
                    {
                        var candidate = Normalize(buffer.ToString());
                        if (File.Exists(candidate))
                        {
                            Console.WriteLine();
                            return candidate;
                        }

                        Console.WriteLine();

                        // 空输入直接重印提示，不报错
                        if (candidate.Length > 0)
                        {
                            AlertInvalid(candidate, inputKept: false, ref lastAlerted);
                        }

                        AnsiConsole.Markup(title);
                        buffer.Clear();
                        burstDetected = false;
                        lastKeyTick = 0;
                        continue;
                    }

                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (buffer.Length > 0)
                        {
                            buffer.Length -= 1;
                            Console.Write("\b \b");
                        }
                        lastKeyTick = 0;
                        burstDetected = false;
                        continue;
                    }

                    if (!char.IsControl(key.KeyChar))
                    {
                        long now = Environment.TickCount64;
                        long gap = lastKeyTick == 0 ? long.MaxValue : now - lastKeyTick;

                        burstDetected = UpdateBurstState(burstDetected, gap);

                        lastKeyTick = now;
                        buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }

                    // 逐个处理完当前可读按键后再回轮询，保证打字时字符零延迟回显
                    continue;
                }

                if (buffer.Length == 0)
                {
                    Thread.Sleep(IdlePollMilliseconds);
                    continue;
                }

                // 仅对拖放/粘贴产生的输入做"空闲即验收"；
                // 手动打字必须回车确认，避免打字中途停顿被误判为完整路径
                if (!burstDetected)
                {
                    Thread.Sleep(IdlePollMilliseconds);
                    continue;
                }

                Thread.Sleep(PasteIdleMilliseconds);
                if (Console.KeyAvailable)
                {
                    // 仍有后续字符，说明突发流未结束
                    continue;
                }

                var idleCandidate = Normalize(buffer.ToString());
                if (File.Exists(idleCandidate))
                {
                    Console.WriteLine();
                    return idleCandidate;
                }

                // 拖放内容不是有效文件：换行提示并原位重印，输入内容保留供继续编辑
                Console.WriteLine();
                AlertInvalid(idleCandidate, inputKept: true, ref lastAlerted);
                AnsiConsole.Markup(title);

                // 保留的缓冲内容必须重新回显，否则屏幕显示与实际输入不一致
                Console.Write(buffer.ToString());
                burstDetected = false;
                lastKeyTick = 0;
            }
        }

        private static string Normalize(string raw) => raw.Replace("\"", "").Trim();

        /// <summary>
        /// 输出一次无效路径提示（同一路径只提示一次）。
        /// <paramref name="inputKept"/> 为 true 时追加"输入已保留"说明
        /// （仅用于保留缓冲区的拖放场景，回车重试已清空缓冲则不追加）
        /// </summary>
        private static void AlertInvalid(string candidate, bool inputKept, ref string lastAlerted)
        {
            if (candidate == lastAlerted)
            {
                return;
            }

            lastAlerted = candidate;
            string message = $"[red]{Resources.InteractiveFileNotFound}[/]";
            if (inputKept)
            {
                message += $" [grey]{Resources.InteractiveInputKept}[/]";
            }

            AnsiConsole.MarkupLine(message);
        }

        /// <summary>
        /// 重定向输入回退：打印提示后从 stdin 逐行读取；
        /// 校验失败重新询问；EOF 时抛错交由统一的缺文件错误路径处理
        /// </summary>
        private static string AskByLine(string title)
        {
            AnsiConsole.Markup(title);

            while (Console.In.ReadLine() is { } line)
            {
                var path = line.Replace("\"", "").Trim();

                if (File.Exists(path))
                {
                    AnsiConsole.MarkupLine($"[dodgerblue1]{Ui.Escape(path)}[/]");
                    return path;
                }

                AnsiConsole.MarkupLine($"[red]{Resources.InteractiveFileNotFound}[/]");
                AnsiConsole.Markup(title);
            }

            throw new FileNotFoundException(Resources.ArchiveFileNotFound);
        }
    }
}
