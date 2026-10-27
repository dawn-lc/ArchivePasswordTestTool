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
        /// <summary>
        /// 询问并写出结果文件。<paramref name="getArchiveHash"/> 只在用户确认保存后才调用，
        /// 使"整文件读一遍算哈希"不会为一个可能被拒绝的确认白做。
        /// </summary>
        public static void WriteIfRequested(
            string archivePath,
            string dictionaryPath,
            string? password,
            TimeSpan elapsed,
            Func<string> getArchiveHash)
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

            string archiveHash = getArchiveHash();
            string resultPath = $"{archivePath}[results].txt";

            using (StreamWriter testOut = new(resultPath, false))
            {
                testOut.WriteLine(string.Format(Resources.TestResultArchive, archivePath));
                testOut.WriteLine(string.Format(Resources.TestResultDictionary, dictionaryPath));
                testOut.WriteLine(string.Format(Resources.TestResultElapsedTime, elapsed));
                testOut.WriteLine(string.Format(Resources.TestResultArchiveHash, archiveHash));
                // 只有找到密码时才写密码行；未找到写"未找到"。
                // （原实现用 password != null 判定，未加密包传入的空字符串会被写成空密码行）
                testOut.WriteLine(!string.IsNullOrEmpty(password)
                    ? string.Format(Resources.TestResultDecompressionPassword, password)
                    : Resources.TestResultPasswordNotFound);
            }

            if (InteractivePrompts.ConfirmSingleKey(Resources.ViewTestReportPrompt, true))
            {
                Process.Start("explorer.exe", $"/select, \"{resultPath}\"");
            }
        }
    }
}
