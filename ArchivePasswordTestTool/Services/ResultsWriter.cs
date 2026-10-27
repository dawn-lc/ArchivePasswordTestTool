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
        /// <param name="archiveEncrypted">
        /// 归档是否加密。未加密时密码行写「未加密」而不是「未找到密码」——
        /// 后者会误导（不是找不到，是压根没有密码）。
        /// </param>
        public static void WriteIfRequested(
            string archivePath,
            string dictionaryPath,
            string? password,
            bool archiveEncrypted,
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
                testOut.WriteLine(BuildPasswordLine(password, archiveEncrypted));
            }

            if (InteractivePrompts.ConfirmSingleKey(Resources.ViewTestReportPrompt, true))
            {
                Process.Start("explorer.exe", $"/select, \"{resultPath}\"");
            }
        }

        /// <summary>
        /// 结果文件的密码行，三态：找到密码 / 加密但未找到 / 归档未加密。
        /// <para>
        /// 未加密必须单列一态：它的 password 是空串，若按「空即未找到」处理会写成
        /// 「未找到正确的解压密码」——不是找不到，是压根没有密码，会误导用户。
        /// 旧实现用 <c>password != null</c> 判定，未加密包甚至会留下一行空密码。
        /// </para>
        /// </summary>
        internal static string BuildPasswordLine(string? password, bool archiveEncrypted)
        {
            if (!archiveEncrypted)
            {
                return Resources.TestResultArchiveNotEncrypted;
            }

            return string.IsNullOrEmpty(password)
                ? Resources.TestResultPasswordNotFound
                : string.Format(Resources.TestResultDecompressionPassword, password);
        }
    }
}
