using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary> Fake 服务端：GET 返回共享密码（null → 404 无记录），POST 记录请求体。返回 (端口, 请求数, 抓到的 POST 请求体, 停止) </summary>
    private static (int Port, Func<int> RequestCount, Func<List<string>> CapturedBodies, Action Stop) StartFakeServer(string? onlinePassword)
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

                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = 2;
                        context.Response.OutputStream.Write("{}"u8.ToArray());
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
        var (port, requestCount, capturedBodies, stop) = StartFakeServer(TestDataFixture.CorrectPassword);
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

                // 回传必须成功结束：输出不得出现任何语言版本的失败提示
                Assert.DoesNotContain("回传失败", output);
                Assert.DoesNotContain("report failed", output, StringComparison.OrdinalIgnoreCase);

                var expectedHash = ComputeHashHex(fx.Archive("Secret.7z"));

                // GET 1 次（查询），POST 1 次（回传）
                Assert.Equal(2, requestCount());

                var body = Assert.Single(capturedBodies());
                Assert.Contains(TestDataFixture.CorrectPassword, body);
                Assert.Contains(expectedHash, body);

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
        var (port, requestCount, capturedBodies, stop) = StartFakeServer(null);
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

    private static (int ExitCode, string Output) RunWithApi(string arguments)
    {
        var startInfo = new ProcessStartInfo(TestPaths.MainExePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
