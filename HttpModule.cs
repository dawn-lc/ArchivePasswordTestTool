using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text;

namespace module.dawnlc.me
{
    /// <summary>
    /// HTTP 模块
    /// </summary>
    class Http : IDisposable
    {
        private bool isDisposed = false;
        /// <summary>
        /// HTTP 请求方法
        /// </summary>
        public enum Method
        {
            /// <summary>
            /// 请求指定的页面信息，并返回实体主体。
            /// </summary>
            GET,
            /// <summary>
            /// 向指定资源提交数据进行处理请求（例如提交表单或者上传文件）。数据被包含在请求体中。POST 请求可能会导致新的资源的建立和/或已有资源的修改。
            /// </summary>
            POST,
            /// <summary>
            /// 类似于 GET 请求，只不过返回的响应中没有具体的内容，用于获取报头。
            /// </summary>
            HEAD,
            /// <summary>
            /// 允许客户端查看服务器的性能。
            /// </summary>
            OPTIONS,
            /// <summary>
            /// 请求指定的页面信息，并返回实体主体。
            /// </summary>
            PUT,
            /// <summary>
            /// 从客户端向服务器传送的数据取代指定的文档的内容。
            /// </summary>
            PATCH,
            /// <summary>
            /// 是对 PUT 方法的补充，用来对已知资源进行局部更新。
            /// </summary>
            DELETE,
            /// <summary>
            /// 回显服务器收到的请求，主要用于测试或诊断。
            /// </summary>
            TRACE,
            /// <summary>
            /// HTTP/1.1 协议中预留给能够将连接改为管道方式的代理服务器。
            /// </summary>
            CONNECT
        }
        /// <summary>
        /// HTTP 请求
        /// </summary>
        public HttpWebRequest Request { get; set; }
        /// <summary>
        /// HTTP 响应
        /// </summary>
        public HttpWebResponse Response { get; set; }
        /// <summary>
        /// 创建 HTTP 请求
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="method">请求方法</param>
        /// <param name="head">请求头</param>
        /// <param name="timeout">请求超时</param>
        public Http(Uri url, Method method, WebHeaderCollection head, int timeout)
        {
            InitializeRequest(url, method, head, timeout);
            Response = Request.GetResponse() as HttpWebResponse;
        }
        /// <summary>
        /// 创建 HTTP 请求
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="method">请求方法</param>
        /// <param name="head">请求头</param>
        /// <param name="timeout">请求超时</param>
        /// <param name="proxy">请求代理</param>
        public Http(Uri url, Method method, WebHeaderCollection head, int timeout, IWebProxy proxy)
        {
            InitializeRequest(url, method, head, timeout);
            Request.Proxy = proxy;
            Response = Request.GetResponse() as HttpWebResponse;
        }
        /// <summary>
        /// 创建 HTTP 请求
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="method">请求方法</param>
        /// <param name="head">请求头</param>
        /// <param name="timeout">请求超时</param>
        /// <param name="formdata">请求附加数据</param>
        public Http(Uri url, Method method, WebHeaderCollection head, int timeout, byte[] formdata)
        {
            InitializeRequest(url, method, head, timeout);
            SeedData(formdata);
            Response = Request.GetResponse() as HttpWebResponse;
        }
        /// <summary>
        /// 创建 HTTP 请求
        /// </summary>
        /// <param name="url">请求地址</param>
        /// <param name="method">请求方法</param>
        /// <param name="head">请求头</param>
        /// <param name="timeout">请求超时</param>
        /// <param name="formdata">请求附加数据</param>
        /// <param name="proxy">请求代理</param>
        public Http(Uri url, Method method, WebHeaderCollection head, int timeout, byte[] formdata, IWebProxy proxy)
        {
            InitializeRequest(url, method, head, timeout);
            Request.Proxy = proxy;
            SeedData(formdata);
            Response = Request.GetResponse() as HttpWebResponse;
        }
        public static WebHeaderCollection CreateHeaders(Dictionary<string, string> HeadersData)
        {
            WebHeaderCollection Headers = new WebHeaderCollection();
            foreach (var Header in HeadersData)
            {
                Headers.Add(Header.Key, Header.Value);
            }
            return Headers;
        }
        public void InitializeRequest(Uri url, Method method, WebHeaderCollection head, int timeout)
        {
            if (url.Scheme.ToLower() == "https")
            {
                ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback((object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate, System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors errors) =>
                {
                    return true; //总是接受  
                });
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | (SecurityProtocolType)192 | (SecurityProtocolType)768 | (SecurityProtocolType)3072;
            }
            Request = WebRequest.Create(url) as HttpWebRequest;
            Request.AllowAutoRedirect = true;
            Request.KeepAlive = false;
            Request.Method = method.ToString();
            if (head.AllKeys.Contains("user-agent")) 
            {
                Request.UserAgent = head.Get("user-agent");
                head.Remove("user-agent");
            }
            foreach (var item in head.AllKeys)
            {
                Request.Headers.Add(item, head.Get(item));
            }
            Request.ContentType = "charset=UTF-8;";
            Request.Timeout = timeout;
        }
        public void SeedData(byte[] formdata)
        {
            Request.GetRequestStream().Write(formdata, 0, formdata.Length);
        }
        public HttpStatusCode GetResponseStatusCode()
        {
            return Response.StatusCode;
        }
        public MemoryStream GetResponseStream()
        {
            using (Stream ResponseStream = Response.GetResponseStream())
            {
                if (Response.ContentEncoding != null && Response.ContentEncoding.ToLower().Contains("gzip"))
                {
                    using (new GZipStream(ResponseStream, CompressionMode.Decompress))
                    {
                        MemoryStream cache = new MemoryStream();
                        ResponseStream.CopyTo(cache);
                        return cache;
                    }
                }
                else
                {
                    MemoryStream cache = new MemoryStream();
                    ResponseStream.CopyTo(cache);
                    return cache;
                }
            }
        }
        public string GetResponseString()
        {
            return GetResponseString(Encoding.UTF8);
        }
        public string GetResponseString(Encoding encoding)
        {
            using (MemoryStream cache = GetResponseStream())
            {
                cache.Seek(0, SeekOrigin.Begin);
                using (StreamReader streamReader = new StreamReader(cache, encoding))
                {
                    return streamReader.ReadToEnd();
                }
            }
            
        }
        public void Close()
        {
            Dispose(true);
        }
        ~Http()
        {
            Dispose(false);
        }
        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed)
            {
                if (disposing)
                {
                    Request.Abort();
                    Response.Close();
                }
            }
            isDisposed = true; // 标识此对象已释放
        }
    }
}
