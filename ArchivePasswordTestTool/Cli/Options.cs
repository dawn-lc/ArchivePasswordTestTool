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
        /// 解析 7z.dll 路径，优先级：已安装 7-Zip → 显式 -lib / 程序目录 7z.dll → 内嵌版。
        /// <para>
        /// 已安装的 7-Zip 排最前：不写盘，且用户自己更新过就跟着用新版。
        /// 内嵌版是“没装 7-Zip”的兜底（构建期内嵌，运行时解压到 %LOCALAPPDATA% 缓存），
        /// 使「下载单个 exe 即可用」成立。
        /// </para>
        /// <para>
        /// 显式 -lib 指向的文件不存在时**直接报错**，不会静默改用内嵌版——
        /// 那样会掩盖用户的配置错误。
        /// </para>
        /// 全部落空：报错 + 官方下载引导 + 等待确认再退出（避免 GUI 双击启动时
        /// 错误信息随控制台立即关闭而不可见；重定向输入时不等待、不阻塞）。
        /// </summary>
        private static string ResolveSevenZipDll(string[] args)
        {
            // 诊断开关：跳过"已安装 7-Zip"，强制走内嵌版。
            // 用途 ① 验证内置 dll 本身可用；② 机器上装的 7-Zip 损坏/版本过旧时临时绕开。
            bool forceEmbedded = Environment.GetEnvironmentVariable("APTT_FORCE_EMBEDDED_7Z") == "1";

            // ① 系统已安装的 7-Zip
            string? installed = forceEmbedded ? null : SevenZip.LocateInstalledLibrary();
            if (installed is not null)
            {
                AnsiConsole.MarkupLine(string.Format(Resources.UsingInstalledSevenZip, Ui.Escape(installed)));
                return installed;
            }

            // ② 显式 -lib，或程序目录下的 7z.dll（便携场景）
            string defaultPath = Path.Combine(Environment.CurrentDirectory, "7z.dll");
            bool libSpecified = Arguments.StartupParametersCheck(args, "lib");
            string explicitPath = Arguments.GetParameter(args, "lib", defaultPath).Replace("\"", "");

            if (File.Exists(explicitPath))
            {
                AnsiConsole.MarkupLine(string.Format(Resources.UsingSpecifiedSevenZipDll, Ui.Escape(explicitPath)));
                return explicitPath;
            }

            if (libSpecified)
            {
                // 用户明确指定了路径却不存在：报错而不是偷偷改用内嵌版
                throw new FileNotFoundException(string.Format(Resources.SevenZipDllNotFound, explicitPath));
            }

            // ③ 构建期内嵌的 7z.dll（解压到 %LOCALAPPDATA%\\ArchivePasswordTestTool\\native）
            string? embedded = EmbeddedSevenZip.TryExtractDll();
            if (embedded is not null)
            {
                AnsiConsole.MarkupLine(string.Format(Resources.UsingEmbeddedSevenZip, Ui.Escape(embedded)));
                return embedded;
            }

            // ④ 全部落空：官方下载引导 + 等待确认退出
            InstallerGuide.ShowMissingDllAndOfferInstall(Resources.SevenZipDllPathRequired);
            Environment.Exit(1);

            // 进程已在上一行终止；此处只为满足编译期的返回值检查
            return string.Empty;
        }
    }
}
