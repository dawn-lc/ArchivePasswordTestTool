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
