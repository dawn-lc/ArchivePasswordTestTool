using ArchivePasswordTestTool.Properties;
using Microsoft.Win32;
using Spectre.Console;
using System.Diagnostics;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 本机 7-Zip 环境的定位与 7z.dll 初始化
    /// </summary>
    internal static class SevenZip
    {
        /// <summary>
        /// 查找已安装的 7-Zip（Program Files 标准路径 → 注册表 HKLM\SOFTWARE\7-Zip）
        /// </summary>
        public static string? LocateInstalledLibrary()
        {
            string[] possiblePaths =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.dll"),
                "C:\\Program Files\\7-Zip\\7z.dll",
                "C:\\Program Files (x86)\\7-Zip\\7z.dll",
            ];

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
                if (key != null)
                {
                    var installPath = key.GetValue("Path") as string;
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        string dllPath = Path.Combine(installPath, "7z.dll");
                        if (File.Exists(dllPath))
                        {
                            return dllPath;
                        }

                        dllPath = Path.Combine(installPath, "7z64.dll");
                        if (File.Exists(dllPath))
                        {
                            return dllPath;
                        }
                    }
                }
            }
            catch
            {
                // 忽略注册表访问错误
            }

            return null;
        }

        /// <summary>
        /// 初始化 7z.dll 并记录当前使用的路径（供工作进程继承）。
        /// 失败时抛 InvalidOperationException（异常含完整 ToString 便于诊断发布产物部署问题）。
        /// </summary>
        public static void Initialize(string dllPath)
        {
            try
            {
                NativeMethods.Initialize(dllPath);
            }
            catch (Exception ex)
            {
                // ToString 而非 Message：保留内部异常类型与堆栈，发布产物部署问题更可诊断
                throw new InvalidOperationException(string.Format(Resources.SevenZipDllInitializationFailed, ex.ToString()), ex);
            }
        }
    }

    /// <summary>
    /// 7-Zip 缺失时的安装引导：显示官方下载链接，询问并打开浏览器。
    /// 仅当"退出后错误信息不可见"（专属控制台，典型于双击启动）时才询问开浏览器；
    /// 终端启动（共享控制台）直接显示链接不询问，管道/自动化静默跳过。
    /// </summary>
    internal static class InstallerGuide
    {
        /// <summary>7-Zip 官方下载页</summary>
        public const string DownloadUrl = "https://www.7-zip.org/download.html";

        /// <summary>
        /// 显示缺失 7z.dll 的错误、官方下载提示，并询问是否打开浏览器。
        /// 确认打开时启动默认浏览器（失败仅提示不阻断）；随后按需等待确认退出。
        /// </summary>
        public static void ShowMissingDllAndOfferInstall(string errorMessage)
        {
            Ui.WriteError(errorMessage);
            AnsiConsole.MarkupLine(string.Format(Resources.SevenZipDownloadHint, $"[link={DownloadUrl}]{DownloadUrl}[/]"));

            if (InteractivePrompts.NeedConfirmBeforeExit()
                && InteractivePrompts.ConfirmSingleKey(Resources.SevenZipOpenBrowserPrompt, defaultYes: true))
            {
                OpenBrowser();
            }

            InteractivePrompts.WaitBeforeExit();
        }

        /// <summary>用系统默认浏览器打开 7-Zip 下载页；失败仅提示不抛出</summary>
        private static void OpenBrowser()
        {
            try
            {
                Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Ui.WriteError(string.Format(Resources.OpenBrowserFailed, ex.Message));
            }
        }
    }
}
