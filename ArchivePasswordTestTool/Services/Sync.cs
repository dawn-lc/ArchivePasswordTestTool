using ArchivePasswordTestTool.Properties;
using Spectre.Console;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 在线密码共享交互：查询（哈希→共享密码，置于队列优先测试）与回传（找到的密码共享给服务端）。
    /// 两个功能均为尽力而为：网络/服务端错误只给黄色提示，绝不阻断本地测试流程。
    /// 从 Runner 拆分，隔离在线服务交互职责。
    /// </summary>
    internal static class Sync
    {
        /// <summary>
        /// 在线服务是否已配置真实地址：默认值是占位地址（本机保留端口），
        /// 视为"未部署服务"。调用方用它决定是否值得为哈希去读整个压缩包。
        /// </summary>
        public static bool IsConfigured(string url)
        {
            return url != OnlineService.DefaultServiceUrl;
        }

        /// <summary>
        /// 向服务器查询哈希对应的共享密码（一个哈希对应一个密码）：
        /// 无记录返回 null；网络/服务端错误打印黄色提示后返回 null，绝不阻断本地测试流程
        /// </summary>
        public static string? RequestOnlinePassword(string url, string hashHex)
        {
            if (!IsConfigured(url))
            {
                // 默认地址是占位：明确提示未配置并跳过，避免对占位地址做无谓的网络等待
                AnsiConsole.MarkupLine($"[grey]{Resources.OnlineNotConfigured}[/]");
                return null;
            }

            try
            {
                AnsiConsole.MarkupLine(string.Format(Resources.OnlineQueryRequesting, hashHex));

                var password = OnlineService.QueryPassword(url, hashHex);

                if (string.IsNullOrEmpty(password))
                {
                    AnsiConsole.MarkupLine($"[grey]{Resources.OnlineQueryEmpty}[/]");
                    return null;
                }

                AnsiConsole.MarkupLine(Resources.OnlineQueryReceived);
                return password;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]{string.Format(Resources.OnlineQueryFailed, Ui.Escape(ex.Message))}[/]");
                return null;
            }
        }

        /// <summary>回传找到的 哈希+密码（尽力而为）：网络/服务端错误只打印黄色提示</summary>
        public static void ReportFoundPassword(string url, string hashHex, string password)
        {
            if (!IsConfigured(url))
            {
                return;
            }

            try
            {
                AnsiConsole.MarkupLine(Resources.OnlineReportSending);
                OnlineService.ReportPassword(url, hashHex, password);
                AnsiConsole.MarkupLine(Resources.OnlineReportDone);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]{string.Format(Resources.OnlineReportFailed, Ui.Escape(ex.Message))}[/]");
            }
        }
    }
}
