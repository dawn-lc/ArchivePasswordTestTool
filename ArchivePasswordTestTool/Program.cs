using System.Reflection;

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
            // 自我替换必须是启动动作的第一步：早于 GUI 委派、早于主流程。
            // 但工作进程要排除在外——它是主进程拉起的临时子进程，抢着替换会让
            // 主进程的多进程测密凭空少一个 worker，还会把替换后的新进程当成
            // worker 去跑 JSON 协议。多开场景下别人的 worker 同理。
            if (!args.Contains(MultiProcess.WorkerFlag) && PendingUpdate.TryApply(args))
            {
                return 0;
            }

            return Instance.Execute(args, () =>
            {
                new Runner(args).Run();
                return 0;
            });
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
                int.TryParse(parts[i], out version[i]);
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
