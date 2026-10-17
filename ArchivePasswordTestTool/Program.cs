namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 程序入口：仅做启动模式路由（工作进程/单实例/GUI 委派）并转发到测试主流程。
    /// 职责拆分见 Instance（路由）、Options（参数）、Environment（环境）、
    /// Runner（主流程）、MultiProcess（多进程引擎）、Ui（展示）。
    /// </summary>
    public class Program
    {
        public static readonly int[] Version = [2, 0, 0];

        /// <summary>版本类型，与 GitHub Release tag 的 "x.y.z-Type" 后缀对应（更新检查语义见 Upgrade）</summary>
        public const string VersionType = "Release";

        static int Main(string[] args)
        {
            return Instance.Execute(args, () =>
            {
                new Runner(args).Run();
                return 0;
            });
        }
    }
}
