using ArchivePasswordTestTool.Properties;
using Figgle.Fonts;
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

            AnsiConsole.AlternateScreen(action);
        }

        /// <summary>
        /// 程序头部：ASCII 标题 + 版本/作者/主页分隔线
        /// </summary>
        public static void WriteHeader(int[] version)
        {
            AnsiConsole.Write(FiggleFonts.Standard.Render(Resources.AppName));

            var footer = new Rule($"[grey]v{string.Join('.', version)} · {Escape(Resources.Developer)} · {Escape(Resources.AppHomePage)}[/]")
            {
                Style = Style.Parse("grey dim"),
                Justification = Justify.Left,
            };
            AnsiConsole.Write(footer);
        }

        /// <summary>
        /// 压缩包与测试参数信息面板（开始测试前展示）
        /// </summary>
        public static void WriteArchiveInfo(
            string archivePath,
            string archiveHash,
            string format,
            uint fileCount,
            bool headerEncryption,
            bool supportsQuickTest,
            string dictionaryPath,
            long passwordCount,
            int parallelism)
        {
            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();

            AddRow(grid, Resources.InfoArchive, archivePath);
            AddRow(grid, Resources.InfoFormat, format);
            AddRow(grid, Resources.InfoFiles, fileCount.ToString());
            AddRow(grid, Resources.InfoEncryption, headerEncryption ? Resources.ValueHeaderEncryption : Resources.ValueYes);
            AddRow(grid, Resources.InfoQuickTest, supportsQuickTest ? Resources.ValueYes : Resources.ValueNo);
            AddRow(grid, Resources.InfoHash, archiveHash);
            AddRow(grid, Resources.InfoDictionary, dictionaryPath);
            AddRow(grid, Resources.InfoParallelism, parallelism.ToString());
            AddRow(grid, Resources.InfoPasswords, passwordCount.ToString());

            var panel = new Panel(grid)
                .Header($"[bold cyan]{Escape(Resources.InfoTitle)}[/]")
                .RoundedBorder()
                .BorderColor(Color.Cyan1);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }

        /// <summary>
        /// 测试结果面板：成功绿色、失败红色，附统计信息
        /// </summary>
        public static void WriteOutcome(bool found, string archivePath, string dictionaryPath, string? password, TimeSpan elapsed, long tested)
        {
            string speed = elapsed.TotalSeconds > 0
                ? string.Format(Resources.ResultPwPerSec, Math.Round(tested / elapsed.TotalSeconds, 1).ToString("N0"))
                : "-";

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();

            AddRow(grid, Resources.InfoArchive, archivePath);
            AddRow(grid, Resources.InfoDictionary, dictionaryPath);
            AddRow(grid, Resources.ResultTested, tested.ToString());
            AddRow(grid, Resources.ResultElapsed, elapsed.ToString());
            AddRow(grid, Resources.ResultSpeed, speed);

            string header = found
                ? $"[bold green]√ {Escape(string.Format(Resources.PasswordFoundSuccess, password))}[/]"
                : $"[bold red]× {Escape(Resources.PasswordNotFound)}[/]";

            var panel = new Panel(grid)
                .Header(header)
                .RoundedBorder()
                .BorderColor(found ? Color.Green : Color.Red);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(panel);
        }

        /// <summary>
        /// 红色错误信息（转义后输出）
        /// </summary>
        public static void WriteError(string message)
        {
            AnsiConsole.MarkupLine($"[red]{Escape(message)}[/]");
        }

        private static void AddRow(Grid grid, string label, string value)
        {
            grid.AddRow($"[grey]{Escape(label)}[/]", $"[white]{Escape(value)}[/]");
        }
    }
}
