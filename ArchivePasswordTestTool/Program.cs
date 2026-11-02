using System.Reflection;
using System.Text;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 程序入口：仅做启动模式路由（工作进程/单实例/GUI 委派）并转发到测试主流程。
    /// 职责拆分见 Instance（路由）、Options（参数）、Environment（环境）、
    /// Runner（主流程）、MultiProcess（多进程引擎）、Ui（展示）。
    /// </summary>
    public class Program
    {
        /// <summary>
        /// 程序版本（固定三段）。唯一真相源是 git tag：发布时 CI 用
        /// <c>-p:AppVersion=x.y.z</c> 注入程序集元数据，本地开发构建为 <c>0.0.0</c>，
        /// 源码里不再硬编码版本号。
        /// 段数必须保持三段——<see cref="Upgrade.IsNewerVersion"/> 在段数与线上 tag 不一致时
        /// 直接判定为「无更新」，属于静默失效。
        /// </summary>
        public static readonly int[] Version = ResolveVersion();

        /// <summary>展示用的版本号文本（如 <c>2.1.0</c>）。版本号是点分三段，格式只在此处定义</summary>
        public static string VersionText => string.Join('.', Version);

        /// <summary>
        /// 本地开发构建的版本类型标记。与 csproj 中 <c>AppVersionType</c> 的默认值一致：
        /// 未注入版本号时 VersionType 即为该值，用于识别开发构建（见 Upgrade）。
        /// </summary>
        public const string DevelopmentVersionType = "Dev";

        /// <summary>
        /// 版本类型，与 GitHub Release tag 的 "x.y.z-Type" 后缀对应（更新检查语义见 Upgrade）。
        /// 发布时由 CI 注入，本地开发构建为 "Dev"（未识别类型 → 保守地不提示更新）。
        /// </summary>
        public static readonly string VersionType = ResolveMetadata("AppVersionType", DevelopmentVersionType);

        static int Main(string[] args)
        {
            // 必须在任何输出之前完成：首帧就要用正确的代码页与字宽绘制，
            // 否则备用缓冲区里的进度画面会逐帧错位（详见 InitializeConsole）。
            InitializeConsole();

            // 字体与代码页是临时借用的：从终端启动时那个窗口属于 shell，退出必须还回去。
            // 正常返回走下面的 finally；Environment.Exit（错误路径、Ctrl+C 接管）不跑
            // finally，由 ProcessExit 兜底。两处都调用，靠内部的「已还原」标记保证幂等。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsNativeMethods.RestoreConsole();

            try
            {
                return RunCore(args);
            }
            finally
            {
                WindowsNativeMethods.RestoreConsole();
            }
        }

        /// <summary>启动路由与主流程（详见 <see cref="Main"/> 的说明）。</summary>
        private static int RunCore(string[] args)
        {
            // 自我替换必须是启动动作的第一步：早于 GUI 委派、早于主流程。
            // 但工作进程要排除在外——它是主进程拉起的临时子进程，抢着替换会让
            // 主进程的多进程测密凭空少一个 worker，还会把替换后的新进程当成
            // worker 去跑 JSON 协议。多开场景下别人的 worker 同理。
            //
            // 更新检查与下载紧跟着最早发起（自我替换之后、路由之前）：后台任务与
            // 用户输入、测密全程重叠，结束前多半已下载完成，不必在退出前干等。
            // 结果由 Runner 消费（见 Upgrade.CompleteBackgroundCheck），
            // 测密结束时还会把已就绪的更新装上并重启一次（见 PendingUpdate.TryApplyAndRestart）。
            if (!args.Contains(MultiProcess.WorkerFlag))
            {
                if (PendingUpdate.TryApply(args))
                {
                    return 0;
                }

                // 上一次运行结束时完成了自我替换并发起了这次重启：只报告版本号，
                // 绝不能重跑一遍测密（重放用户的命令等于白测一次）
                if (args.Contains(PendingUpdate.RestartedFlag))
                {
                    PendingUpdate.ReportRestarted();
                    return 0;
                }

                Upgrade.StartBackgroundCheck();
            }

            return Instance.Execute(args, () => new Runner(args).Run());
        }

        /// <summary>
        /// 控制台准备：所有输出统一 UTF-8；Windows 真实终端再修正代码页与字体。
        /// </summary>
        /// <remarks>
        /// 两种场景的目标不同：
        /// <list type="bullet">
        /// <item>重定向：输出通常被其它工具读取，必须是自描述的 UTF-8。系统默认是 OEM 代码页
        /// （中文 Windows = 936/GBK），法语的 é、俄语的西里尔字母在 GBK 下无法表示，
        /// 会变成 '?'（数据损坏，不是显示问题）。</item>
        /// <item>交互终端：界面支持 5 种语言，而控制台代码页跟随系统语言——中文 Windows 上
        /// 跑法语界面同样会把重音字符编成 '?'。切到 UTF-8 后字形交给控制台字体，
        /// 同时强制 Spectre 使用 Unicode 边框，并按实测宽度修正字体。</item>
        /// </list>
        /// </remarks>
        private static void InitializeConsole()
        {
            if (Console.IsOutputRedirected)
            {
                try
                {
                    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                }
                catch (Exception)
                {
                    // 极端环境（无控制台句柄）下设置失败不影响功能
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                try
                {
                    // 先记录原值再改：Console.OutputEncoding 的 setter 自己就会改原生代码页，
                    // 顺序反了记到的就是我们刚设的 UTF-8，退出时便「还原」成 UTF-8。
                    WindowsNativeMethods.CaptureConsoleCodePage();

                    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

                    // stdin 被重定向时输入来自调用方的管道，编码由对方决定（字典文件另有
                    // 编码探测）；这里只改真实键盘输入的代码页。
                    if (!Console.IsInputRedirected)
                    {
                        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    }

                    // 编码属性不一定同步原生控制台代码页（尤其输入侧），再显式设一次。
                    // 宿主不允许时返回 false，不是错误。
                    _ = WindowsNativeMethods.TryEnableUtf8Console();
                }
                catch (Exception)
                {
                    // Windows Terminal / 无控制台句柄：代码页与编码由宿主管理，忽略即可
                }
            }

            // 必须在首次访问 Profile 之前设置：Spectre 在 Profile 创建时捕获当时的
            // Console.Out（默认代码页的 writer），之后再改 Console.OutputEncoding
            // 不会作用到它身上。放在编码设置之后，重定向输出才是 UTF-8。
            // 代码页已统一为 UTF-8，边框/表格可以放心用 Unicode 字符。
            AnsiConsole.Profile.Capabilities.Unicode = true;

            // 字体修正必须独立于编码设置：两者由宿主分别管理，一边失败不影响另一边。
            // 不改字宽的话，中文 Windows 默认字体（新宋体）会把 Spectre 算作一格的
            // ± · × 画成两格，面板与进度条逐帧错位（见 EnsureNarrowAmbiguousWidth）。
            // 非 Windows / 输出被重定向时该方法内部直接返回。
            _ = WindowsNativeMethods.EnsureNarrowAmbiguousWidth();
        }

        /// <summary>
        /// 从程序集元数据解析版本号。任何格式问题都回退为 0（不抛异常）：
        /// 版本号是展示与更新检查的附属信息，不应让程序启动失败。
        /// </summary>
        private static int[] ResolveVersion()
        {
            var parts = ResolveMetadata("AppVersion", "0.0.0").Split('.');
            var version = new int[3];

            for (int i = 0; i < version.Length && i < parts.Length; i++)
            {
                _ = int.TryParse(parts[i], out version[i]);
            }

            return version;
        }

        private static string ResolveMetadata(string key, string fallback)
        {
            string? value = typeof(Program).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == key)?
                .Value;

            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
