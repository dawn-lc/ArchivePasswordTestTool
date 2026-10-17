using System.Diagnostics;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 进程入口路由：工作进程 / GUI 委派，然后进入命令行主流程。
    /// 顺序语义（不可调换）：工作进程分支先于 GUI 委派
    /// （worker 与主进程同名同路径，且不得触发 GUI 委派）。
    /// </summary>
    internal static class Instance
    {
        /// <summary>
        /// 程序入口路由：工作进程分流、GUI 委派，返回主工作流退出码。
        /// 允许多开：每个实例独立测试，互不干扰
        /// </summary>
        public static int Execute(string[] args, Func<int> runMainWorkflow)
        {
            // 工作进程模式：无 UI，stdin/stdout 行式 JSON 协议，供多进程测密使用
            if (args.Contains(MultiProcess.WorkerFlag))
            {
                return MultiProcess.RunWorker(args);
            }

            // 目录内存在 GUI 启动器时转发给它（保持旧行为）
            if (Environment.OSVersion.Platform.ToString().Contains("win", StringComparison.InvariantCultureIgnoreCase)
                && File.Exists("ArchivePasswordTestToolGUI.exe"))
            {
                Process.Start("ArchivePasswordTestToolGUI.exe");
                return 0;
            }

            return runMainWorkflow();
        }
    }
}
