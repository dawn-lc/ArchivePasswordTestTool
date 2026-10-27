using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// HTTP 传输统一工厂：所有出站请求的 HttpClient 都从这里构造，保证带同一份版本标识。
    /// <para>
    /// 两种客户端：<see cref="CreateClient(TimeSpan)"/> 清空空 SNI——部分网络环境的中间设备
    /// 按 TLS SNI 域名嗅探并阻断发往 github.com 的连接，清空后握手不再携带域名，
    /// 可回退到按 IP 路由从而恢复 api.github.com 访问；代价是服务器在无 SNI 时返回默认站点证书，
    /// 主机名必然不匹配，故仅豁免名称不匹配错误，证书链有效性/有效期仍严格校验。
    /// </para>
    /// <para>
    /// <see cref="CreateStandardClient(TimeSpan)"/> 保留正常 SNI 与完整证书校验，
    /// 用于地址可由用户指定（<c>-Api</c>）或不需要规避嗅探的场景。
    /// </para>
    /// </summary>
    internal static class HttpTransport
    {
        /// <summary>
        /// 出站请求的统一标识（<c>ArchivePasswordTestTool x.y.z-Type</c>）。
        /// 所有 HTTP 客户端都必须带上，服务端可据此区分客户端版本。
        /// </summary>
        internal static string UserAgent =>
            $"ArchivePasswordTestTool {string.Join('.', Program.Version)}-{Program.VersionType}";

        /// <summary>
        /// 创建空 SNI 客户端：握手不携带域名，绕过按 SNI 嗅探的中间设备。
        /// 代价是服务器返回默认站点证书导致主机名必然不匹配——仅豁免该错误，
        /// 证书链有效性与有效期仍严格校验。
        /// </summary>
        /// <param name="timeout">整体超时（含传输）</param>
        /// <param name="connectTimeout">建连/握手超时；不指定则与 <paramref name="timeout"/> 相同</param>
        public static HttpClient CreateClient(TimeSpan timeout, TimeSpan? connectTimeout = null)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                ConnectTimeout = connectTimeout ?? timeout,
                SslOptions = new SslClientAuthenticationOptions
                {
                    // 空 TargetHost：SslStream 客户端不再发送 SNI 扩展（防按域名嗅探阻断）
                    TargetHost = string.Empty,
                    RemoteCertificateValidationCallback = (_, _, _, errors) =>
                        (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None,
                },
            };

            return CreateClient(handler, timeout);
        }

        /// <summary>创建标准 TLS 客户端（带 SNI，完整证书校验），作为空 SNI 失败时的回退</summary>
        public static HttpClient CreateStandardClient(TimeSpan timeout, TimeSpan? connectTimeout = null)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                ConnectTimeout = connectTimeout ?? timeout,
            };

            return CreateClient(handler, timeout);
        }

        /// <summary>统一装配：所有出站客户端都必须带版本标识，避免各调用点各写一份（漏写即丢失版本信息）</summary>
        private static HttpClient CreateClient(SocketsHttpHandler handler, TimeSpan timeout)
        {
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }
    }
}
