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
        /// <summary>拖放粘贴完成后的空闲等待毫秒数：超时无新按键即认为粘贴结束并验收</summary>
        private const int PasteIdleMilliseconds = 250;

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
        /// 拖放粘贴是一串快速到达的字符——短时空闲后若已有缓冲则尝试验收：
        /// 文件存在 → 立即接受（拖入即开始）；不存在 → 静默继续等待（不丢已输入内容，
        /// 避免手打前缀恰好匹配到已有文件导致提前提交后反而无需继续输入）；
        /// 回车 → 强制验收（不存在时原位报错重问）；退格 → 删除末字符。
        /// </summary>
        private static string AskByKeys(string title)
        {
            AnsiConsole.Markup(title);
            var buffer = new StringBuilder();
            var lastAlerted = string.Empty;
            var focusRestored = false;

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

                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            {
                                var candidate = Normalize(buffer.ToString());
                                if (File.Exists(candidate))
                                {
                                    Console.WriteLine();
                                    return candidate;
                                }

                                AlertInvalid(candidate, ref lastAlerted);
                                Console.WriteLine();
                                AnsiConsole.Markup(title);
                                buffer.Clear();
                                continue;
                            }
                        case ConsoleKey.Backspace:
                            if (buffer.Length > 0)
                            {
                                buffer.Length -= 1;
                                Console.Write("\b \b");
                            }
                            continue;
                        default:
                            if (!char.IsControl(key.KeyChar))
                            {
                                buffer.Append(key.KeyChar);
                                Console.Write(key.KeyChar);
                            }
                            continue;
                    }
                }

                if (buffer.Length == 0)
                {
                    Thread.Sleep(50);
                    continue;
                }

                // 粘贴完成启发：无新按键到达且缓冲非空 → 短暂等待后尝试自动验收
                Thread.Sleep(PasteIdleMilliseconds);
                if (Console.KeyAvailable)
                {
                    continue;
                }

                var idleCandidate = Normalize(buffer.ToString());
                if (File.Exists(idleCandidate))
                {
                    Console.WriteLine();
                    return idleCandidate;
                }

                // 无效路径不自动清空（手打过程中可能还没输完）；换内容后提示一次
                AlertInvalid(idleCandidate, ref lastAlerted);
            }
        }

        private static string Normalize(string raw) => raw.Replace("\"", "").Trim();

        private static void AlertInvalid(string candidate, ref string lastAlerted)
        {
            if (candidate == lastAlerted)
            {
                return;
            }

            lastAlerted = candidate;
            AnsiConsole.MarkupLine($"[red]{Resources.InteractiveFileNotFound}[/] [grey]{Resources.InteractiveInputKept}[/]");
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
