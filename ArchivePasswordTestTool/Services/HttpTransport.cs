using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// HTTP 传输工厂：创建启用空 SNI 的 HttpClient。
    /// 部分网络环境的中间设备按 TLS SNI 域名嗅探并阻断/重置发往 github.com 的连接，
    /// 清空 SNI 后握手不再携带域名，可回退到按 IP 路由，从而恢复 api.github.com 访问。
    /// 代价：服务器在无 SNI 时返回默认站点证书，主机名必然不匹配——
    /// 因此仅豁免名称不匹配错误，证书链有效性/有效期仍然校验（比无条件放行更安全）。
    /// </summary>
    internal static class HttpTransport
    {
        /// <summary>创建空 SNI 客户端；生产路径始终校验证书链</summary>
        public static HttpClient CreateClient(TimeSpan timeout) => CreateClient(timeout, verifyChain: true);

        /// <summary>创建标准 TLS 客户端（带 SNI），作为空 SNI 失败时的回退</summary>
        public static HttpClient CreateStandardClient(TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                ConnectTimeout = timeout,
            };

            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ArchivePasswordTestTool {string.Join('.', Program.Version)}-{Program.VersionType}");
            return client;
        }

        /// <summary>
        /// 创建空 SNI 客户端。
        /// <paramref name="verifyChain"/> 仅供测试用自签证书验证 SNI 行为；
        /// 生产路径始终为 <see langword="true"/>。
        /// </summary>
        internal static HttpClient CreateClient(TimeSpan timeout, bool verifyChain)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                ConnectTimeout = timeout,
                SslOptions = new SslClientAuthenticationOptions
                {
                    // 空 TargetHost：SslStream 客户端不再发送 SNI 扩展（防按域名嗅探阻断）
                    TargetHost = string.Empty,
                    RemoteCertificateValidationCallback = (_, _, _, errors) =>
                        !verifyChain
                        || (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None,
                },
            };

            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ArchivePasswordTestTool {string.Join('.', Program.Version)}-{Program.VersionType}");
            return client;
        }
    }
}
