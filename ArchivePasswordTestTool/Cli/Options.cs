using ArchivePasswordTestTool.Properties;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 命令行参数解析结果
    /// </summary>
    internal sealed class Options
    {
        /// <summary>7z.dll 路径（空 = 未显式指定，由解析时回填为已安装路径或默认查找）</summary>
        public string SevenZipDllPath { get; init; } = string.Empty;

        /// <summary>密码字典路径</summary>
        public string DictionaryPath { get; init; } = string.Empty;

        /// <summary>压缩包路径</summary>
        public string ArchivePath { get; init; } = string.Empty;

        /// <summary>在线服务地址（-Api 覆盖，默认 OnlineService.DefaultServiceUrl）</summary>
        public string OnlineServiceUrl { get; init; } = OnlineService.DefaultServiceUrl;

        /// <summary>测试前是否请求在线字典（默认开启，-NoQuery 关闭）</summary>
        public bool QueryOnline { get; init; }

        /// <summary>找到密码后是否回传（默认开启，-NoReport 关闭）</summary>
        public bool ReportOnline { get; init; }

        /// <summary>大小写不敏感的开关检测</summary>
        private static bool HasFlag(string[] args, string name)
        {
            return args.Any(arg => arg.Equals("-" + name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 解析工具参数：-lib（缺省回填已安装 7-Zip 或 CWD\7z.dll）、-D、-F。
        /// 解析顺序（启动流程微调）：① 先解析 7z.dll（交互模式也先验证 7z 环境，
        /// 缺失即时报错退出，避免白费引导输入）→ ② 再取得密码字典与压缩包路径
        /// （无参数时交互引导拖入，带参时 -D/-F 校验）。
        /// 带参启动缺失参数值抛 FileNotFoundException（E2E 锁定 exit 1）。
        /// </summary>
        public static Options Parse(string[] args)
        {
            // ① 解析 7z.dll：已安装 → CWD\7z.dll → -lib；全部缺失即时报错等待确认退出
            string dllPath = ResolveSevenZipDll(args);

            // ② 取得密码字典与压缩包路径：无参数启动走交互引导（拖入即验收）
            string dictionaryPath;
            string archivePath;
            bool interactive = args.Length == 0;
            if (interactive)
            {
                (dictionaryPath, archivePath) = InteractivePrompts.AskPaths();
            }
            else
            {
                if (!Arguments.StartupParametersCheck(args, "D"))
                {
                    throw new FileNotFoundException(Resources.DictionaryFileNotFound);
                }

                dictionaryPath = Arguments.GetParameter(args, "D", "PasswordDictionary.txt").Replace("\"", "");

                if (!Arguments.StartupParametersCheck(args, "F"))
                {
                    throw new FileNotFoundException(Resources.ArchiveFileNotFound);
                }

                archivePath = Arguments.GetParameter(args, "F", "").Replace("\"", "");
            }

            return new Options
            {
                SevenZipDllPath = dllPath,
                DictionaryPath = dictionaryPath,
                ArchivePath = archivePath,
                OnlineServiceUrl = Arguments.GetParameter(args, "Api", OnlineService.DefaultServiceUrl).Replace("\"", "").Trim().TrimEnd('/'),
                QueryOnline = !HasFlag(args, "NoQuery"),
                ReportOnline = !HasFlag(args, "NoReport"),
            };
        }

        /// <summary>
        /// 解析 7z.dll 路径：已安装 7-Zip → CWD\7z.dll → -lib 参数。
        /// 未找到且未指定 -lib：报错后等待确认再退出（避免 GUI 双击启动时错误信息
        /// 随控制台立即关闭而不可见；重定向输入时 WaitBeforeExit 直接返回，不阻塞）。
        /// -lib 指定但路径不存在：抛 FileNotFoundException 走统一错误路径。
        /// </summary>
        private static string ResolveSevenZipDll(string[] args)
        {
            string? installed = SevenZip.LocateInstalledLibrary();
            if (installed is not null)
            {
                AnsiConsole.MarkupLine(string.Format(Resources.UsingInstalledSevenZip, Ui.Escape(installed)));
                return installed;
            }

            string dllPath = Path.Combine(Environment.CurrentDirectory, "7z.dll");
            if (!File.Exists(dllPath) && !Arguments.StartupParametersCheck(args, "lib"))
            {
                // 未找到 7z.dll 且未指定 -lib：报错 + 显示官方下载链接 + 询问打开浏览器
                // + 等待确认退出（避免 GUI 双击启动时错误信息随控制台立即关闭而不可见）。
                // 重定向输入（管道/自动化）时浏览器询问与等待均自动跳过，不阻塞。
                InstallerGuide.ShowMissingDllAndOfferInstall(Resources.SevenZipDllPathRequired);
                Environment.Exit(1);
            }

            dllPath = Arguments.GetParameter(args, "lib", dllPath).Replace("\"", "");
            AnsiConsole.MarkupLine(string.Format(Resources.UsingSpecifiedSevenZipDll, Ui.Escape(dllPath)));

            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException(string.Format(Resources.SevenZipDllNotFound, dllPath));
            }

            return dllPath;
        }
    }
}
