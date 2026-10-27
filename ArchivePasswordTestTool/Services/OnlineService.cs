using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 在线密码共享服务客户端。数据模型：一个压缩包哈希对应一个密码。
    /// 功能 1（默认开启）：压缩包哈希计算完成后立即向服务端查询该哈希对应的共享密码
    /// （服务端历史破解记录），存在则置于密码队列最前优先测试，不匹配再退回本地字典；
    /// 功能 2（默认开启）：成功找到密码后，把 哈希+密码 回传给服务端供他人共享。
    /// 两个功能均为尽力而为：失败（网络/超时/服务端错误）只给出黄色提示，
    /// 不影响本地测试流程；可用 -NoQuery / -NoReport 参数分别关闭，
    /// 地址可用 -Api 参数覆盖，默认地址在 DefaultServiceUrl 一处修改。
    /// 服务端契约：
    /// - 查询  GET {url}?hash={sha256hex} → 200 + {"password":"..."}（404/空=无记录）
    /// - 回传  POST {url} body {"hash":"{sha256hex}","password":"..."} → 2xx
    /// </summary>
    internal static class OnlineService
    {
        /// <summary>
        /// 在线服务默认地址。部署实际服务后在此一处修改；
        /// 当前占位地址指向本机保留端口（连接立即失败），保证离线时快速回退到本地字典。
        /// </summary>
        public const string DefaultServiceUrl = "http://127.0.0.1:1";

        /// <summary>请求超时：两个功能都是尽力而为，宁可快速放弃也不要拖慢测密</summary>
        private const int RequestTimeoutSeconds = 5;

        /// <summary>
        /// 统一由 <see cref="HttpTransport"/> 构造（出站请求都带版本标识的 User-Agent）。
        /// 这里必须用标准 TLS 客户端而不能复用更新检查那条「空 SNI」路径：
        /// 本服务地址可由 -Api 指向任意端点，空 SNI 会豁免证书域名不匹配，
        /// 等于对任意地址放宽校验。
        /// </summary>
        private static readonly HttpClient Http =
            HttpTransport.CreateStandardClient(TimeSpan.FromSeconds(RequestTimeoutSeconds));

        /// <summary>
        /// 查询哈希对应的共享密码（一个哈希对应一个密码）；无记录（404/空）返回 null，
        /// 服务端/网络错误抛异常
        /// </summary>
        public static string? QueryPassword(string url, string hashHex)
        {
            var queryUrl = $"{url.TrimEnd('/')}?hash={Uri.EscapeDataString(hashHex)}";
            using var response = Http.GetAsync(queryUrl).GetAwaiter().GetResult();

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var reply = JsonSerializer.Deserialize(body, OnlineJsonContext.Default.OnlinePasswordReply);
            return string.IsNullOrEmpty(reply?.Password) ? null : reply.Password;
        }

        /// <summary>回传找到的 哈希+密码；非 2xx 抛异常</summary>
        public static void ReportPassword(string url, string hashHex, string password)
        {
            var json = JsonSerializer.Serialize(
                new OnlineReportRequest { Hash = hashHex, Password = password },
                OnlineJsonContext.Default.OnlineReportRequest);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = Http.PostAsync(url, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>查询响应：该哈希对应的共享密码</summary>
    internal sealed class OnlinePasswordReply
    {
        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>回传请求：哈希 + 找到的密码</summary>
    internal sealed class OnlineReportRequest
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// JSON 源生成：管道/网络序列化不依赖反射，IL 裁剪后可用
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(OnlinePasswordReply))]
    [JsonSerializable(typeof(OnlineReportRequest))]
    internal partial class OnlineJsonContext : JsonSerializerContext
    {
    }
}
