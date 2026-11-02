namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 命令行参数读取辅助。-Flag 形式的参数与其后的值成对出现。
    /// 消费方：Options（主流程参数解析）、MultiProcess（工作进程 -F/-lib）。
    /// </summary>
    internal static class Arguments
    {
        /// <summary>
        /// 剥离资源管理器拖放带上的引号并去掉首尾空白。
        /// 拖入窗口或粘贴的路径常带双引号（含空格路径的默认形式），
        /// 所有接收路径的入口（-D/-F/-lib/-Api、交互引导、工作进程参数）都走这一处。
        /// </summary>
        public static string Unquote(string value) => value.Replace("\"", "").Trim();

        /// <summary>Flag 是否存在且具有非空值</summary>
        public static bool StartupParametersCheck(string[] args, string flag) =>
            !string.IsNullOrEmpty(GetParameter(args, flag, string.Empty));

        /// <summary>读取 Flag 的值；缺 Flag/缺值/转换失败时返回 <paramref name="defaultValue" /></summary>
        public static T GetParameter<T>(string[] args, string flag, T defaultValue)
        {
            int index = Array.IndexOf(args, "-" + flag);
            if (index < 0 || index + 1 >= args.Length || args[index + 1].Length == 0)
            {
                return defaultValue;
            }

            try
            {
                return (T)Convert.ChangeType(args[index + 1], typeof(T));
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }
    }
}
