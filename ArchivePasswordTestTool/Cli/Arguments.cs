namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 命令行参数读取辅助。-Flag 形式的参数与其后的值成对出现。
    /// 消费方：Options（主流程参数解析）、MultiProcess（工作进程 -F/-lib）。
    /// </summary>
    internal static class Arguments
    {
        /// <summary>
        /// 检查 Flag 是否存在且具有非空值；取值过程的异常转为统一的启动参数错误
        /// </summary>
        public static bool StartupParametersCheck(List<string> Parameters, string Flag)
        {
            if (Parameters.Contains($"-{Flag}"))
            {
                try
                {
                    return !string.IsNullOrEmpty(GetParameter(Parameters, Flag, ""));
                }
                catch (Exception)
                {
                    throw new Exception($"Startup parameters error. Please check: {Flag}");
                }
            }

            return false;
        }

        public static bool StartupParametersCheck(string[] Parameters, string Flag)
        {
            return StartupParametersCheck(new List<string>(Parameters), Flag);
        }

        /// <summary>读取 Flag 的值；缺 Flag/缺值/转换失败时返回默认值</summary>
        public static T GetParameter<T>(List<string> Parameters, string Flag, T DefaultParameter)
        {
            try
            {
                if (!Parameters.Contains($"-{Flag}"))
                {
                    throw new ArgumentException(Flag);
                }

                if (Parameters.IndexOf($"-{Flag}") == Parameters.Count - 1)
                {
                    throw new ArgumentException(Flag);
                }

                return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T));
            }
            catch (Exception)
            {
                return DefaultParameter;
            }
        }

        public static T GetParameter<T>(string[] Parameters, string Flag, T DefaultParameter)
        {
            return GetParameter(new List<string>(Parameters), Flag, DefaultParameter);
        }

        /// <summary>读取 Flag 的值；缺 Flag/缺值/转换失败时返回类型默认值</summary>
        public static T? GetParameter<T>(List<string> Parameters, string Flag)
        {
            try
            {
                if (!Parameters.Contains($"-{Flag}"))
                {
                    throw new ArgumentException(Flag);
                }

                if (Parameters.IndexOf($"-{Flag}") == Parameters.Count - 1)
                {
                    throw new ArgumentException(Flag);
                }

                return (T)Convert.ChangeType(Parameters[Parameters.IndexOf($"-{Flag}") + 1], typeof(T));
            }
            catch (Exception)
            {
                return default;
            }
        }

        public static T? GetParameter<T>(string[] Parameters, string Flag)
        {
            return GetParameter<T>(new List<string>(Parameters), Flag);
        }
    }
}
