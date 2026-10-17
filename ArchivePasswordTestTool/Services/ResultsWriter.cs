using ArchivePasswordTestTool.Properties;
using System.Diagnostics;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 测试结果落盘（交互式确认后写出 .txt 并可选打开资源管理器定位）
    /// </summary>
    internal static class ResultsWriter
    {
        public static void WriteIfRequested(string archivePath, string dictionaryPath, string archiveHash, string? password, TimeSpan elapsed)
        {
            if (!AnsiConsole.Profile.Out.IsTerminal || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                // 非交互环境（输出重定向/无人值守）下 Console 读取会抛异常，跳过交互式确认
                return;
            }

            if (!InteractivePrompts.ConfirmSingleKey(Resources.SaveTestResultsPrompt, true))
            {
                return;
            }

            using (StreamWriter testOut = new($"{archivePath}[results].txt", false))
            {
                testOut.WriteLine(string.Format(Resources.TestResultArchive, archivePath));
                testOut.WriteLine(string.Format(Resources.TestResultDictionary, dictionaryPath));
                testOut.WriteLine(string.Format(Resources.TestResultElapsedTime, elapsed));
                testOut.WriteLine(string.Format(Resources.TestResultArchiveHash, archiveHash));
                testOut.WriteLine(password != null ? string.Format(Resources.TestResultDecompressionPassword, password) : Resources.TestResultPasswordNotFound);
            }

            if (Environment.OSVersion.Platform.ToString().Contains("win", StringComparison.InvariantCultureIgnoreCase)
                && InteractivePrompts.ConfirmSingleKey(Resources.ViewTestReportPrompt, true))
            {
                Process.Start("explorer.exe", $"/select, \"{archivePath}[results].txt\"");
            }
        }
    }
}
