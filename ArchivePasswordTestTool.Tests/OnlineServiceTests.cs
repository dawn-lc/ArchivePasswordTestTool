using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArchivePasswordTestTool.Properties;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 在线服务功能端到端测试：本地 HttpListener 模拟服务端。
/// 契约（一个哈希对应一个密码）：GET {url}?hash={sha256hex} → {"password":"..."}（无记录 404）；
/// POST {url} body {"hash":...,"password":...}。
/// 验证：在线下发的共享密码被优先测试（本地字典不含正确密码也能破出）；
/// 找到密码后自动回传；-NoQuery / -NoReport 关闭后零网络请求。
/// </summary>
[Collection(TestDataCollection.Name)]
public sealed class OnlineServiceTests(TestDataFixture fx)
{
    private static string ComputeHashHex(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    /// <summary> Fake 服务端：GET 返回共享密码（null → 404 无记录），POST 记录请求体。
    /// <paramref name="reportStatus"/> 决定回传响应码（非 2xx 触发客户端失败提示，用于正向对照测试）。
    /// 返回 (端口, 请求数, 抓到的 POST 请求体, 抓到的 User-Agent, 停止) </summary>
    private static (int Port, Func<int> RequestCount, Func<List<string>> CapturedBodies, Func<List<string>> CapturedUserAgents, Action Stop) StartFakeServer(string? onlinePassword, int reportStatus = 200)
    {
        // 用 TcpListener 抢一个空闲端口，再让 HttpListener 绑定同一端口
        int port;
        var listener = new HttpListener();

        while (true)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                // 端口竞争，换一个重试
            }
        }

        var requestCount = 0;
        var capturedBodies = new List<string>();
        var capturedUserAgents = new List<string>();

        var serverTask = Task.Run(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    Interlocked.Increment(ref requestCount);

                    // 出站请求必须带版本标识（HttpTransport 统一装配，漏写即丢失版本信息）
                    lock (capturedUserAgents)
                    {
                        capturedUserAgents.Add(context.Request.UserAgent ?? string.Empty);
                    }

                    if (context.Request.HttpMethod == "GET")
                    {
                        context.Response.StatusCode = onlinePassword is null
                            ? (int)HttpStatusCode.NotFound
                            : (int)HttpStatusCode.OK;

                        var json = "{\"password\":" + System.Text.Json.JsonSerializer.Serialize(onlinePassword) + "}";
                        var buffer = Encoding.UTF8.GetBytes(json);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = buffer.Length;
                        context.Response.OutputStream.Write(buffer);
                    }
                    else
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        lock (capturedBodies)
                        {
                            capturedBodies.Add(reader.ReadToEnd());
                        }

                        context.Response.StatusCode = reportStatus;
                        if (reportStatus == (int)HttpStatusCode.OK)
                        {
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = 2;
                            context.Response.OutputStream.Write("{}"u8.ToArray());
                        }
                        else
                        {
                            context.Response.ContentLength64 = 0;
                        }
                    }

                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // 客户端提前断开等，忽略单个请求的写回错误
                }
            }
        });

        return (
            port,
            () => Interlocked.CompareExchange(ref requestCount, 0, 0),
            () => capturedBodies,
            () => capturedUserAgents,
            () =>
            {
                try
                {
                    listener.Stop();
                    listener.Close();
                }
                catch
                {
                }

                try
                {
                    serverTask.Wait(2000);
                }
                catch
                {
                }
            }
        );
    }

    [Fact]
    public void OnlinePasswordWinsAndPasswordIsReported()
    {
        var (port, requestCount, capturedBodies, capturedUserAgents, stop) = StartFakeServer(TestDataFixture.CorrectPassword);
        try
        {
            // 本地字典只有错误密码：破出正确密码必然来自在线下发的共享密码
            string localDictPath = Path.Combine(Path.GetTempPath(), $"ApttOnline{Guid.NewGuid():N}.txt");
            File.WriteAllLines(localDictPath, ["LocalWrong1", "LocalWrong2"]);

            try
            {
                var (exitCode, output) = RunWithApi(
                    $"-D \"{localDictPath}\" -F \"{fx.Archive("Secret.7z")}\" -Api http://127.0.0.1:{port}");

                Assert.Equal(0, exitCode);
                Assert.Contains(TestDataFixture.CorrectPassword, output);

                // 回传必须成功结束：输出不得出现任何语言版本的失败提示。
                // 文案直接取自各语言资源，新增语言时自动纳入覆盖——
                // 写死少数几种语言会在其它 UI 语言下漏检（曾经的实现只查中文+英文，
                // 在日/法/俄系统上这条断言等于失效，失败被 2xx 之外的错误静默吞掉）。
                AssertNoLocalizedText(output, "OnlineReportFailed");

                var expectedHash = ComputeHashHex(fx.Archive("Secret.7z"));

                // GET 1 次（查询），POST 1 次（回传）
                Assert.Equal(2, requestCount());

                var body = Assert.Single(capturedBodies());
                Assert.Contains(TestDataFixture.CorrectPassword, body);
                Assert.Contains(expectedHash, body);

                // 查询与回传都必须带统一版本标识：HttpTransport 是唯一装配点，
                // 任何绕过工厂自建 HttpClient 的写法都会在这里暴露（历史上 OnlineService 就漏了 UA）
                string expectedUserAgent =
                    $"ArchivePasswordTestTool {string.Join('.', Program.Version)}-{Program.VersionType}";
                var userAgents = capturedUserAgents();
                Assert.Equal(2, userAgents.Count);
                Assert.All(userAgents, userAgent => Assert.Equal(expectedUserAgent, userAgent));

                File.Delete(localDictPath);
            }
            finally
            {
                File.Delete(localDictPath);
            }
        }
        finally
        {
            stop();
        }
    }

    [Fact]
    public void NoQueryAndNoReportFlagsDisableServerCalls()
    {
        var (port, requestCount, capturedBodies, capturedUserAgents, stop) = StartFakeServer(null);
        try
        {
            // -NoQuery -NoReport：全程不应有任何网络请求（本地字典全错 → 未找到，exit 0）
            var (exitCode, output) = RunWithApi(
                $"-D \"{fx.Dict("Dict7.txt")}\" -F \"{fx.Archive("Secret.7z")}\" -NoQuery -NoReport -Api http://127.0.0.1:{port}");

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(TestDataFixture.CorrectPassword, output);
            Assert.Equal(0, requestCount());
            Assert.Empty(capturedBodies());
        }
        finally
        {
            stop();
        }
    }

    /// <summary>
    /// 正向对照：服务端对回传返回 500 时，输出**必须**出现失败提示。
    /// 这条用例证明上一条「不含失败提示」的断言真的有牙齿——否则文案变更、
    /// 语言切换导致的永不命中会变成假绿，而回传失败就此被静默吞掉。
    /// </summary>
    [Fact]
    public void ReportFailureProducesLocalizedWarning()
    {
        var (port, _, _, _, stop) = StartFakeServer(TestDataFixture.CorrectPassword, reportStatus: 500);
        try
        {
            string localDictPath = Path.Combine(Path.GetTempPath(), $"ApttOnlineFail{Guid.NewGuid():N}.txt");
            File.WriteAllLines(localDictPath, ["LocalWrong1", "LocalWrong2"]);

            try
            {
                var (exitCode, output) = RunWithApi(
                    $"-D \"{localDictPath}\" -F \"{fx.Archive("Secret.7z")}\" -Api http://127.0.0.1:{port}");

                // 尽力而为设计：回传失败不影响测密结果与退出码
                Assert.Equal(0, exitCode);
                Assert.Contains(TestDataFixture.CorrectPassword, output);

                string normalized = NormalizeWhitespace(output);
                Assert.Contains(
                    LocalizedPrefixes("OnlineReportFailed"),
                    prefix => normalized.Contains(prefix, StringComparison.Ordinal));
            }
            finally
            {
                File.Delete(localDictPath);
            }
        }
        finally
        {
            stop();
        }
    }

    /// <summary>
    /// 受支持语言，对应 Properties/Resources.*.resx 的卫星程序集（en 为中性资源）。
    /// 新增语言时必须同步这里，否则「不含某提示」的断言会在新语言下漏检。
    /// </summary>
    private static readonly string[] SupportedCultures = ["en", "zh-CN", "ja", "fr", "ru"];

    /// <summary>
    /// 取资源键在所有受支持语言下的文案主体（<c>{0}</c> 占位符之前的部分）。
    /// </summary>
    private static string[] LocalizedPrefixes(string resourceKey)
    {
        return SupportedCultures
            .Select(culture => Resources.ResourceManager.GetString(resourceKey, new CultureInfo(culture)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!.Split('{')[0].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 断言输出不含指定资源键的**任意**语言版本。
    /// 先归一化空白：Spectre 按终端宽度折行（重定向时宽度固定），
    /// 折行位置不同会把整段文案切成多行，直接 Contains 会漏检。
    /// </summary>
    private static void AssertNoLocalizedText(string output, string resourceKey)
    {
        string normalized = NormalizeWhitespace(output);

        foreach (string prefix in LocalizedPrefixes(resourceKey))
        {
            Assert.DoesNotContain(prefix, normalized, StringComparison.Ordinal);
        }
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (int ExitCode, string Output) RunWithApi(string arguments)
    {
        var startInfo = new ProcessStartInfo(TestPaths.MainExePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 主程序在输出重定向时把 stdout/stderr 改为 UTF-8（见 Program.Main）
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        Assert.True(process.WaitForExit(60_000), $"主程序执行超时：{arguments}");
        var output = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();

        return (process.ExitCode, output);
    }
}
