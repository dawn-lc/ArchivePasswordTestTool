using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// 构建期的三个 MSBuild 内联任务（由 csproj 的 <UsingTask ... > 引用本文件）：
//   · EnsureResvgToolTask   —— 解析/下载 resvg.exe（SVG 栅格化，图标必需）
//   · EnsureUpxToolTask     —— 解析/下载 upx.exe
//   · EnsureSevenZipDllTask —— 下载 7-Zip 官方件并取出 7z.dll + License.txt
//
// 放在独立 .cs 文件而不是 csproj 里的 CDATA 块：
//   ① 有语法高亮/补全/重构，改错在编辑器里就能看出来；
//   ② 三个任务共用同一份网络与文本辅助，不再各写一遍（原先 CDATA 版本是两份近似副本）；
//   ③ 不受 XML 转义限制，也不用担心 ]]&gt; 意外截断。
//
// 注意：RoslynCodeTaskFactory 的编译目标框架较旧（无 .NET Core 的 ProcessStartInfo.ArgumentList、
// 无 string.Split 的某些重载）。此处只用该参考集里确定存在的 API。

/// <summary>gh api 查到的发布信息：tag 与每个资产的下载地址（顺序同 GitHub 返回）。</summary>
public sealed class GitHubRelease
{
    public string Tag;
    public string[] AssetUrls;
}

/// <summary>两个任务共用的网络 / 文本辅助。</summary>
public static class BuildTaskHelpers
{    /// <summary>
     /// 下载与探测的重试次数。一次性失败会让构建静默产出「未内嵌」的二进制，代价不对称。
     /// <para>
     /// 为什么是 4 次：GitHub 的 TLS 握手会被间歇性掐断，实测持续过 <b>139 秒</b>（整个构建窗口）。
     /// 原先 3 次 ×（1s + 2s）只有 3 秒重试窗口，对那种抖动毫无作用。现在总退避
     /// 2 + 4 + 8 = 14 秒，加上每次尝试自身的连接超时，容错窗口约 3 分钟。
     /// </para>
     /// </summary>
    public const int DownloadAttempts = 4;

    public const string UserAgent = "ArchivePasswordTestTool-build";

    /// <summary>
    /// 强制启用 TLS 1.2。GitHub 只接受 TLS 1.2+，而较旧宿主默认协议可能是 TLS 1.0，
    /// 表现为 “SSL connection could not be established”。
    /// 用数值 3072 而非枚举名，避免依赖参考程序集是否定义了该成员。
    /// </summary>
    public static void EnsureTls12()
    {
        try
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
        }
        catch
        {
            // 受限环境不允许改全局协议：交给后续请求自行报错
        }
    }

    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// 下载字节并重试。网络会间歇性掐断发往 github.com 的 TLS 握手
    /// （“SSL connection could not be established” / “unexpected EOF”），
    /// 而同一地址往往过几秒又能连通。
    /// </summary>
    public static byte[] GetBytesWithRetry(HttpClient client, string url, Action<string> log)
    {
        return Retry(
            () => client.GetByteArrayAsync(url).GetAwaiter().GetResult(),
            $"下载失败（已重试 {DownloadAttempts} 次）：{url}",
            log);
    }

    /// <summary>发请求拿响应（常用于读取 302 的 Location），失败重试</summary>
    public static HttpResponseMessage GetResponseWithRetry(HttpClient client, string url, Action<string> log)
    {
        return Retry(
            () => client.GetAsync(url).GetAwaiter().GetResult(),
            $"请求失败（已重试 {DownloadAttempts} 次）：{url}",
            log);
    }

    /// <summary>
    /// 下载字节，但 404 时**不重试**、直接返回 null。
    /// <para>
    /// 用于「同一个发布下试几个候选资产名」：404 是确定性答案（名字不对），
    /// 重试 4 次只会让用户白等 14 秒。其余失败（TLS 中断、超时）仍按退避重试。
    /// </para>
    /// </summary>
    public static byte[] GetBytesOrNullOnNotFound(HttpClient client, string url, Action<string> log)
    {
        Exception last = null;

        for (var attempt = 1; attempt <= DownloadAttempts; attempt++)
        {
            try
            {
                using (var response = client.GetAsync(url).GetAwaiter().GetResult())
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < DownloadAttempts)
                {
                    int delaySeconds = 1 << attempt;
                    if (log != null)
                    {
                        log($"第 {attempt}/{DownloadAttempts} 次尝试失败（{Describe(ex)}），{delaySeconds} 秒后重试…");
                    }

                    Thread.Sleep(delaySeconds * 1000);
                }
            }
        }

        throw new InvalidOperationException($"下载失败（已重试 {DownloadAttempts} 次）：{url}", last);
    }

    /// <summary>
    /// 重试包装。<paramref name="log"/> 每次重试前报告原因与等待时长 ——
    /// 否则用户只会看到一个没有任何输出的构建（实测卡了 139 秒才报错）。
    /// </summary>
    private static T Retry<T>(Func<T> action, string failureMessage, Action<string> log)
    {
        Exception last = null;
        for (var attempt = 1; attempt <= DownloadAttempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < DownloadAttempts)
                {
                    // 指数退避：2、4、8 秒。瞬时抖动常持续数十秒，固定短间隔重试无意义。
                    int delaySeconds = 1 << attempt;
                    if (log != null)
                    {
                        log($"第 {attempt}/{DownloadAttempts} 次尝试失败（{Describe(ex)}），{delaySeconds} 秒后重试…");
                    }

                    Thread.Sleep(delaySeconds * 1000);
                }
            }
        }

        throw new InvalidOperationException(failureMessage, last);
    }

    /// <summary>
    /// 展开完整异常链。HttpRequestException 的 Message 只有一句
    /// “The SSL connection could not be established, see inner exception.”，
    /// 真正原因（证书 / 代理 / 协议版本）全在 InnerException 里，不展开等于没报错。
    /// </summary>
    public static string Describe(Exception ex)
    {
        var builder = new StringBuilder();
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (builder.Length > 0)
            {
                builder.Append(" <- ");
            }

            builder.Append(current.GetType().Name).Append(": ").Append(current.Message);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 跟随 releases/latest 的 302 重定向读取 tag，避免消耗 GitHub API 配额
    /// （未认证 60 次/小时，本地反复构建很容易耗尽）。读不到返回 null。
    /// </summary>
    public static string ResolveLatestVersion(string releasesLatestUrl, Action<string> log)
    {
        using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
        using (var probe = CreateClient(TimeSpan.FromSeconds(30)))
        {
            probe.DefaultRequestHeaders.Clear();
            probe.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using (var response = GetResponseWithRetry(
                       new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
                       releasesLatestUrl,
                       log))
            {
                var location = response.Headers.Location;
                if (location == null)
                {
                    return null;
                }

                // .../releases/tag/v5.2.1 → 5.2.1
                string tag = location.Segments.LastOrDefault();
                return string.IsNullOrEmpty(tag) ? null : tag.TrimStart('v', 'V');
            }
        }
    }

    /// <summary>读取 .version 标记（不存在时返回 null）</summary>
    public static string ReadVersionMarker(string versionFile)
    {
        return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;
    }

    /// <summary>
    /// 用 gh CLI 读发布资产列表（tag + 每个资产的下载地址）。
    /// <para>
    /// 为什么优先这条路：文件名是上游随时能改的东西（resvg 就把 resvg-win64.zip 改成了
    /// resvg-win-x86_64.zip），而资产列表是**事实**——直接问 API 就不用猜，
    /// 也不必维护一张候选名清单。顺带还能把真实 tag 拿回来。
    /// </para>
    /// <para>
    /// 失败一律返回 <c>null</c> 让调用方回退，不是错误：gh 没装、未登录、
    /// 404、限流都属正常情况（本地开发机、未注入 token 的 CI 步骤）。
    /// 用 <c>--jq</c> 让 gh 直接吐纯文本（首行 tag，其余每行一个 URL），
    /// 避免在旧参考集里手写 JSON 解析。
    /// </para>
    /// </summary>
    /// <param name="repoSlug">形如 owner/repo</param>
    /// <param name="tag">为空表示 latest</param>
    public static GitHubRelease QueryReleaseViaGh(string repoSlug, string tag, Action<string> log)
    {
        string endpoint = "repos/" + repoSlug + "/releases/" + (string.IsNullOrEmpty(tag) ? "latest" : "tags/" + tag);

        // jq 表达式不含空格：即使外层引号被某层 shell 吃掉，也不会被拆成多个参数
        const string Jq = ".tag_name,(.assets[].browser_download_url)";

        try
        {
            var psi = new ProcessStartInfo("gh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = "api " + endpoint + " --jq " + Quote(Jq),
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    return null;
                }

                // 先异步读 stdout 再同步读 stderr：两个管道各 4KB+，顺序同步读在
                // stderr 写满时会互相等死锁（.NET 老生常谈）。本任务的输出很短，
                // 但这是构建基础设施，不赌运气。
                System.Threading.Tasks.Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                string stdout = stdoutTask.Result;

                if (process.ExitCode != 0)
                {
                    if (log != null)
                    {
                        log($"gh api {endpoint} 失败（exit {process.ExitCode}）：{FirstLine(stderr)}");
                    }

                    return null;
                }

                string[] lines = stdout.Replace("\r\n", "\n").Split('\n');
                var urls = new System.Collections.Generic.List<string>();
                string resolvedTag = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (resolvedTag == null)
                    {
                        resolvedTag = line;
                    }
                    else
                    {
                        urls.Add(line);
                    }
                }

                if (string.IsNullOrEmpty(resolvedTag) || urls.Count == 0)
                {
                    if (log != null)
                    {
                        log($"gh api {endpoint} 返回内容为空。");
                    }

                    return null;
                }

                var release = new GitHubRelease();
                release.Tag = resolvedTag;
                release.AssetUrls = urls.ToArray();
                return release;
            }
        }
        catch (Exception ex)
        {
            // 最常见的是 Win32Exception（PATH 里没有 gh.exe），其次是权限/超时
            if (log != null)
            {
                log("无法调用 gh CLI（" + Describe(ex) + "），回退到不依赖 API 的下载方式。");
            }

            return null;
        }
    }

    /// <summary>
    /// 从资产 URL 列表里挑出「Windows x64 的 zip」。
    /// <para>
    /// 只按 URL 里的文件名判断，不联网。优先级：
    /// ① 以 <paramref name="preferredPrefix"/> 开头（resvg 仓库同时发布 usvg，不排除会挑错）；
    /// ② 文件名含 win 与 x64；③ 排除 aarch64 / arm64。
    /// 实在没有首选前缀的候选时，退而使用其余匹配项（比如将来改了命名前缀）。
    /// </para>
    /// </summary>
    public static string PickWindowsX64Zip(string[] assetUrls, string preferredPrefix)
    {
        if (assetUrls == null)
        {
            return null;
        }

        string fallback = null;

        for (int i = 0; i < assetUrls.Length; i++)
        {
            string url = assetUrls[i];
            string name = FileNameOf(url).ToLowerInvariant();

            if (!name.EndsWith(".zip", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.Contains("aarch64") || name.Contains("arm64"))
            {
                continue;
            }

            bool isWindows = name.Contains("-win") || name.Contains("win64") || name.Contains("windows");
            bool isX64 = name.Contains("x86_64") || name.Contains("x64") || name.Contains("win64");
            if (!isWindows || !isX64)
            {
                continue;
            }

            if (string.IsNullOrEmpty(preferredPrefix)
                || name.StartsWith(preferredPrefix.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return url;
            }

            // 前缀不符：先记下，等整轮没有更好的再考虑
            if (fallback == null)
            {
                fallback = url;
            }
        }

        return fallback;
    }

    /// <summary>取 URL 最后一段（不做解码：GitHub 资产名里不会有需要转义的字符）</summary>
    private static string FileNameOf(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        int slash = url.LastIndexOf('/');
        return slash >= 0 && slash < url.Length - 1 ? url.Substring(slash + 1) : url;
    }

    /// <summary>取多行文本的第一行非空内容（错误摘要用）</summary>
    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(无输出)";
        }

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return "(无输出)";
    }

    /// <summary>
    /// 请求的是否为固定版本。<c>null</c> / 空 / "latest" 都视为「跟随最新」，
    /// 此时缓存一律视为有效（否则每次构建都要联网探测）。
    /// <para>
    /// <paramref name="pinned"/> 会归一化掉 <c>v</c> 前缀：缓存里 <c>.version</c> 存的永远是
    /// 不带 v 的形式（解析 tag 时统一 TrimStart），不归一化就会让 <c>-p:XxxVersion=v1.2.3</c>
    /// 每次构建都判定缓存过期、重复下载。
    /// </para>
    /// </summary>
    public static bool IsPinned(string requestedVersion, out string pinned)
    {
        pinned = string.IsNullOrEmpty(requestedVersion)
            ? requestedVersion
            : requestedVersion.Trim().TrimStart('v', 'V');

        return !string.IsNullOrEmpty(pinned)
               && !pinned.Equals("latest", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>给路径加引号（内联任务没有 ProcessStartInfo.ArgumentList，只能手工拼）</summary>
    public static string Quote(string value)
    {
        return "\"" + value + "\"";
    }

    /// <summary>从 zip 中解出第一个匹配的文件；找不到返回 false</summary>
    public static bool TryExtractFromZip(byte[] zipBytes, string fileName, string destination)
    {
        using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            var entry = archive.Entries.FirstOrDefault(
                e => string.Equals(Path.GetFileName(e.FullName), fileName, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
            {
                return false;
            }

            entry.ExtractToFile(destination, overwrite: true);
            return true;
        }
    }
}

/// <summary>
/// 解析 resvg.exe：显式路径 → 约定位置缓存 → 自动下载 win-x86_64 版。
/// <para>
/// 与 UPX / 7z.dll 那两个任务不同，**找不到 resvg 会让构建失败**：
/// 图标是产物的一部分，不像压缩率那样可以退让。少一个图标或不报错地画错图标，
/// 都比构建失败难发现得多。
/// </para>
/// <para>
/// resvg（github.com/linebender/resvg）是单个静态链接的 Rust 可执行文件，无需任何运行库，
/// 且它是完整的 SVG 渲染器（贝塞尔曲线、transform、渐变、mask、text 都支持），
/// 因此图标源可以随便用标准 SVG，不必迁就自制渲染器的能力边界。
/// </para>
/// </summary>
public class EnsureResvgToolTask : Task
{
    public string ExplicitPath { get; set; }
    public string ToolDirectory { get; set; }
    public string RequestedVersion { get; set; }
    public bool AutoDownload { get; set; }
    public bool ForceUpdate { get; set; }

    [Output] public string ResultPath { get; set; }
    [Output] public string ResolvedVersion { get; set; }
    [Output] public bool WasDownloaded { get; set; }

    private const string ReleasesLatest = "https://github.com/linebender/resvg/releases/latest";
    private const string ReleaseBase = "https://github.com/linebender/resvg/releases/download/";

    /// <summary>gh api 的仓库标识（owner/repo）。只在 gh 回退路径里用得到。</summary>
    private const string RepoSlug = "linebender/resvg";

    /// <summary>
    /// win-x86_64 资产的候选文件名，按顺序尝试。
    /// <para>
    /// resvg 改过命名：v0.44 及更早是 <c>resvg-win64.zip</c>，v0.49 起改成 <c>resvg-win-x86_64.zip</c>。
    /// 不跟随 API 枚举资产（未认证配额 60 次/小时，跟 UPX 那边同样的理由），
    /// 而是直接按候选名试——404 立即换下一个，不浪费时间重试。
    /// </para>
    /// </summary>
    private static readonly string[] Win64AssetNames = { "resvg-win-x86_64.zip", "resvg-win64.zip" };

    /// <summary>重试时报告原因与等待时长（理由同 EnsureUpxToolTask.RetryLog）</summary>
    private void RetryLog(string message)
    {
        Console.Error.WriteLine(message);
    }

    public override bool Execute()
    {
        WasDownloaded = false;

        if (!string.IsNullOrEmpty(ToolDirectory))
        {
            ToolDirectory = Path.GetFullPath(ToolDirectory);
        }

        // 1) 显式指定优先
        if (!string.IsNullOrEmpty(ExplicitPath))
        {
            string explicitFull = Path.GetFullPath(ExplicitPath);
            if (File.Exists(explicitFull))
            {
                ResultPath = explicitFull;
                Log.LogMessage(MessageImportance.High, "使用指定的 resvg：" + explicitFull);
                return true;
            }

            Log.LogError("ResvgPath 指向的文件不存在：" + explicitFull);
            return false;
        }

        string cachedExe = Path.Combine(ToolDirectory, "resvg.exe");
        string versionFile = Path.Combine(ToolDirectory, ".version");
        string cachedVersion = BuildTaskHelpers.ReadVersionMarker(versionFile);
        bool wantPinned = BuildTaskHelpers.IsPinned(RequestedVersion, out string pinned);

        // 2) 缓存命中（跟随最新时视缓存为最新；固定版本时须一致）
        if (!ForceUpdate && File.Exists(cachedExe))
        {
            if (!wantPinned || string.Equals(cachedVersion, pinned, StringComparison.OrdinalIgnoreCase))
            {
                ResultPath = cachedExe;
                ResolvedVersion = cachedVersion;
                Log.LogMessage(MessageImportance.High, $"使用已缓存的 resvg {cachedVersion ?? "(版本未知)"}：{cachedExe}");
                return true;
            }

            Log.LogMessage(MessageImportance.High, $"缓存版本 {cachedVersion ?? "?"} 不符合请求的 {pinned}，重新获取。");
        }

        if (!AutoDownload)
        {
            Log.LogError("未找到 resvg.exe 且已禁用自动下载（ResvgAutoDownload=false）。"
                         + " 请把 resvg.exe 放到 " + ToolDirectory + "，或用 -p:ResvgPath=... 指定路径。");
            return false;
        }

        // 3) 下载
        try
        {
            BuildTaskHelpers.EnsureTls12();

            using (var http = BuildTaskHelpers.CreateClient(TimeSpan.FromMinutes(5)))
            {
                string version;
                string assetName;
                byte[] payload;

                // 3a) 优先 gh api：直接读发布里真实的资产列表，按模式挑，不猜文件名。
                //     resvg 改过一次资产命名（resvg-win64.zip → resvg-win-x86_64.zip），
                //     猜名字的写法只能靠候选清单兜底，而清单永远可能落后于上游。
                //     诊断走 MSBuild 日志而不是 RetryLog：gh 缺席是正常回退，不是错误，
                //     写 stderr 会让 PowerShell 把成功构建误判成 NativeCommandError。
                //
                //     tag 候选：上游一直用 vX.Y.Z；pinned 已被 IsPinned 归一化掉 v 前缀，
                //     而 API 的 tags/<ref> 不做前缀容错（v0.44.0 ✓ / 0.44.0 ✗ 404），故两个都试。
                string[] tagCandidates = wantPinned
                    ? new string[] { "v" + pinned, pinned }
                    : new string[] { null };

                GitHubRelease release = null;
                for (int i = 0; i < tagCandidates.Length && release == null; i++)
                {
                    release = BuildTaskHelpers.QueryReleaseViaGh(
                        RepoSlug,
                        tagCandidates[i],
                        message => Log.LogMessage(MessageImportance.High, message));
                }

                if (release != null)
                {
                    version = release.Tag.TrimStart('v', 'V');
                    string assetUrl = BuildTaskHelpers.PickWindowsX64Zip(release.AssetUrls, "resvg");

                    if (assetUrl == null)
                    {
                        Log.LogError(
                            "resvg " + release.Tag + " 的资产里没有 Windows x64 的 zip。可用资产："
                            + string.Join("；", release.AssetUrls));
                        return false;
                    }

                    assetName = assetUrl.Substring(assetUrl.LastIndexOf('/') + 1);
                    Log.LogMessage(MessageImportance.High, $"gh api 解析到 resvg {version}：{assetName}");

                    payload = BuildTaskHelpers.GetBytesWithRetry(http, assetUrl, RetryLog);
                }
                else
                {
                    // 3b) 回退：跟随 releases/latest 的 302 拿 tag，再按候选文件名试。
                    //     适用于 gh 没装 / 未登录 / 限流的场景（本地开发机、未注入 token 的 CI）。
                    Log.LogMessage(MessageImportance.High, "gh CLI 不可用，回退到重定向 + 候选文件名。");

                    version = wantPinned ? pinned : BuildTaskHelpers.ResolveLatestVersion(ReleasesLatest, RetryLog);
                    if (string.IsNullOrEmpty(version))
                    {
                        Log.LogError("无法确定 resvg 版本。请用 -p:ResvgVersion=x.y.z 固定版本，或手工放置 resvg.exe。");
                        return false;
                    }

                    string tag = "v" + version;
                    string releaseUrl = ReleaseBase + tag + "/";

                    // 候选名循环：命中的那个就是最终产物，404 不产生响应体，不会浪费带宽
                    payload = null;
                    assetName = null;
                    for (int i = 0; i < Win64AssetNames.Length; i++)
                    {
                        string candidate = Win64AssetNames[i];
                        Log.LogMessage(MessageImportance.High, $"尝试 resvg 资产：{releaseUrl}{candidate}");

                        payload = BuildTaskHelpers.GetBytesOrNullOnNotFound(http, releaseUrl + candidate, RetryLog);
                        if (payload != null)
                        {
                            assetName = candidate;
                            break;
                        }

                        Log.LogMessage(MessageImportance.High, $"资产 {candidate} 不存在（404），尝试下一个命名。");
                    }

                    if (payload == null)
                    {
                        Log.LogError(
                            "resvg " + version + " 的 win-x64 资产都不存在（已试："
                            + string.Join("、", Win64AssetNames) + "）。"
                            + " 请换一个版本（-p:ResvgVersion=...），或手工放置 resvg.exe 到 " + ToolDirectory + "。");
                        return false;
                    }
                }

                Directory.CreateDirectory(ToolDirectory);

                if (!BuildTaskHelpers.TryExtractFromZip(payload, "resvg.exe", cachedExe))
                {
                    Log.LogError("resvg 压缩包内未找到 resvg.exe（下载的是 " + assetName + "）。");
                    return false;
                }

                File.WriteAllText(versionFile, version);

                ResultPath = cachedExe;
                ResolvedVersion = version;
                WasDownloaded = true;
                Log.LogMessage(MessageImportance.High, $"resvg {version} 已就绪：{cachedExe}");
                return true;
            }
        }
        catch (Exception ex)
        {
            // 与前两个任务不同：这里不降级。图标不能缺，
            // 明确报出失败原因，并给出「手工放置」这条离线出路。
            Log.LogError("自动获取 resvg 失败（" + BuildTaskHelpers.Describe(ex) + "）。"
                         + " 可手工下载 resvg 的 win-x64 压缩包、解压出 resvg.exe 放到 " + ToolDirectory
                         + "（或用 -p:ResvgPath=... 指定），然后重试。");
            return false;
        }
    }
}

/// <summary>
/// 解析 upx.exe：显式路径 → 约定位置缓存 → 自动下载最新 win64 版。
/// 找不到不算构建失败，只警告（UPX 只是体积优化）。
/// </summary>
public class EnsureUpxToolTask : Task
{
    public string ExplicitPath { get; set; }
    public string ToolDirectory { get; set; }
    public string RequestedVersion { get; set; }
    public bool AutoDownload { get; set; }
    public bool ForceUpdate { get; set; }

    [Output] public string ResultPath { get; set; }
    [Output] public string ResolvedVersion { get; set; }
    [Output] public bool WasDownloaded { get; set; }

    private const string ReleasesLatest = "https://github.com/upx/upx/releases/latest";

    /// <summary>
    /// 重试时报告原因与等待时长。
    /// <para>
    /// 直接用 <c>Console.Error</c> 而不是 <c>Log.LogMessage</c>：实测 <c>dotnet build</c> 的默认
    /// minimal verbosity 不显示任务里发的 High / Normal 消息（只有 error / warning 会出），
    /// 而重试不属于错误，不该用 LogError/LogWarning 抬高警告计数。
    /// 不写出来就是一次没有任何输出的长时间等待——实测卡过 139 秒才报错，无法判断是卡死还是在重试。
    /// </para>
    /// </summary>
    private void RetryLog(string message)
    {
        Console.Error.WriteLine(message);
    }

    public override bool Execute()
    {
        WasDownloaded = false;

        // 规范化路径：MSBuild 传来的可能含 .. 段
        if (!string.IsNullOrEmpty(ToolDirectory))
        {
            ToolDirectory = Path.GetFullPath(ToolDirectory);
        }

        // 1) 显式指定优先
        if (!string.IsNullOrEmpty(ExplicitPath))
        {
            string explicitFull = Path.GetFullPath(ExplicitPath);
            if (File.Exists(explicitFull))
            {
                ResultPath = explicitFull;
                Log.LogMessage(MessageImportance.High, $"使用指定的 UPX：{explicitFull}");
                return true;
            }

            Log.LogError($"UpxPath 指向的文件不存在：{explicitFull}");
            return false;
        }

        string cachedExe = Path.Combine(ToolDirectory, "upx.exe");
        string versionFile = Path.Combine(ToolDirectory, ".version");
        string cachedVersion = BuildTaskHelpers.ReadVersionMarker(versionFile);
        bool wantPinned = BuildTaskHelpers.IsPinned(RequestedVersion, out string pinned);

        // 2) 缓存命中（跟随最新时视缓存为最新；固定版本时须一致）
        if (!ForceUpdate && File.Exists(cachedExe))
        {
            if (!wantPinned || string.Equals(cachedVersion, pinned, StringComparison.OrdinalIgnoreCase))
            {
                ResultPath = cachedExe;
                ResolvedVersion = cachedVersion;
                Log.LogMessage(MessageImportance.High, $"使用已缓存的 UPX {cachedVersion ?? "(版本未知)"}：{cachedExe}");
                return true;
            }

            Log.LogMessage(MessageImportance.High, $"缓存版本 {cachedVersion ?? "?"} 不符合请求的 {pinned}，重新获取。");
        }

        if (!AutoDownload)
        {
            Log.LogWarning("未找到 upx.exe 且已禁用自动下载（UpxAutoDownload=false），跳过 UPX 压缩。");
            return true;
        }

        // 3) 下载
        try
        {
            BuildTaskHelpers.EnsureTls12();

            using (var http = BuildTaskHelpers.CreateClient(TimeSpan.FromMinutes(5)))
            {
                string version = wantPinned ? pinned : BuildTaskHelpers.ResolveLatestVersion(ReleasesLatest, RetryLog);
                if (string.IsNullOrEmpty(version))
                {
                    Log.LogWarning("无法确定 UPX 最新版本，跳过压缩。可改用 -p:UpxVersion=x.y.z 固定版本。");
                    return true;
                }

                string zipUrl = $"https://github.com/upx/upx/releases/download/v{version}/upx-{version}-win64.zip";
                Log.LogMessage(MessageImportance.High, $"下载 UPX {version}：{zipUrl}");

                byte[] payload = BuildTaskHelpers.GetBytesWithRetry(http, zipUrl, RetryLog);

                Directory.CreateDirectory(ToolDirectory);

                if (!BuildTaskHelpers.TryExtractFromZip(payload, "upx.exe", cachedExe))
                {
                    Log.LogWarning("UPX 压缩包内未找到 upx.exe，跳过压缩。");
                    return true;
                }

                File.WriteAllText(versionFile, version);

                ResultPath = cachedExe;
                ResolvedVersion = version;
                WasDownloaded = true;
                Log.LogMessage(MessageImportance.High, $"UPX {version} 已就绪：{cachedExe}");
                return true;
            }
        }
        catch (Exception ex)
        {
            // 网络失败不应让发布失败：UPX 只是体积优化，没有它产物依然可运行
            Log.LogWarning($"自动获取 UPX 失败（{BuildTaskHelpers.Describe(ex)}），跳过压缩。"
                           + " 产物未压缩；可手工把 upx.exe 放到 tools\\upx\\ 后重试。");
            return true;
        }
    }
}

/// <summary>
/// 获取 7-Zip 官方件并取出 7z.dll + License.txt。
/// <para>
/// 7-Zip 官方**不提供**独立的 7z.dll 下载，只有安装包，因此走「官方件自举」：
/// 下载 7zr.exe（602KB 官方独立解包器）+ 7zNNNN-x64.exe（官方安装包，本质是 7z SFX），
/// 用 7zr 解包取件。两者都来自 github.com/ip7z/7zip —— 7-zip.org 下载页指向的官方发布点。
/// </para>
/// <para>
/// 许可：7z.dll 是 GNU LGPL 2.1+（含 unRAR 限制），官方 license.txt 要求二进制再分发
/// 必须附带许可证信息。因此缺 License.txt 时宁可放弃内嵌也不能产出不合规的包。
/// </para>
/// </summary>
public class EnsureSevenZipDllTask : Task
{
    public string ToolDirectory { get; set; }
    public string RequestedVersion { get; set; }
    public bool AutoDownload { get; set; }
    public bool ForceUpdate { get; set; }

    /// <summary>
    /// 拿不到可内嵌的 7z.dll 时是否直接失败。
    /// <para>
    /// 本地开发保持 <c>false</c>：缺网也要能构建，产物退回「需自行安装 7-Zip」的老行为。
    /// CI / 发布必须设 <c>true</c>：否则下载失败只会产生一条 warning，而 smoke test 因为
    /// runner 上装了 7-Zip 会走「已安装」分支照样通过 —— 于是不自包含的产物静默上传。
    /// </para>
    /// </summary>
    public bool RequireEmbeddedSevenZip { get; set; }

    [Output] public string DllPath { get; set; }
    [Output] public string LicensePath { get; set; }
    [Output] public string ResolvedVersion { get; set; }
    [Output] public bool WasDownloaded { get; set; }

    private const string ReleasesLatest = "https://github.com/ip7z/7zip/releases/latest";
    private const string ReleaseBase = "https://github.com/ip7z/7zip/releases/download/";

    /// <summary>
    /// 重试时报告原因与等待时长。
    /// <para>
    /// 直接用 <c>Console.Error</c> 而不是 <c>Log.LogMessage</c>：实测 <c>dotnet build</c> 的默认
    /// minimal verbosity 不显示任务里发的 High / Normal 消息（只有 error / warning 会出），
    /// 而重试不属于错误，不该用 LogError/LogWarning 抬高警告计数。
    /// 不写出来就是一次没有任何输出的长时间等待——实测卡过 139 秒才报错，无法判断是卡死还是在重试。
    /// </para>
    /// </summary>
    private void RetryLog(string message)
    {
        Console.Error.WriteLine(message);
    }

    /// <summary>
    /// 内嵌失败的唯一处置点。严格模式下报错并让 target 失败，宽松模式下只警告。
    /// 返回值即 <see cref="Execute"/> 的返回值，调用处直接 <c>return FailOrWarn(...)</c>。
    /// </summary>
    private bool FailOrWarn(string message)
    {
        if (RequireEmbeddedSevenZip)
        {
            Log.LogError(message);
            return false;
        }

        Log.LogWarning(message);
        return true;
    }

    public override bool Execute()
    {
        WasDownloaded = false;
        ToolDirectory = Path.GetFullPath(ToolDirectory);

        string cachedDll = Path.Combine(ToolDirectory, "7z.dll");
        string cachedLicense = Path.Combine(ToolDirectory, "License.txt");
        string versionFile = Path.Combine(ToolDirectory, ".version");
        string cachedVersion = BuildTaskHelpers.ReadVersionMarker(versionFile);
        bool wantPinned = BuildTaskHelpers.IsPinned(RequestedVersion, out string pinned);

        // 1) 缓存命中
        if (!ForceUpdate && File.Exists(cachedDll))
        {
            if (!wantPinned || string.Equals(cachedVersion, pinned, StringComparison.OrdinalIgnoreCase))
            {
                AcceptCache(cachedDll, cachedLicense, cachedVersion,
                    $"使用已缓存的 7z.dll{(cachedVersion != null ? $"（7-Zip {cachedVersion}）" : string.Empty)}：{cachedDll}");
                return true;
            }

            Log.LogMessage(MessageImportance.High, $"缓存版本 {cachedVersion ?? "?"} 不符合请求的 {pinned}，重新获取。");
        }

        if (!AutoDownload)
        {
            if (File.Exists(cachedDll))
            {
                AcceptCache(cachedDll, cachedLicense, cachedVersion, $"使用本地已有的 7z.dll：{cachedDll}");
                return true;
            }

            return FailOrWarn("未内嵌 7z.dll（SevenZipAutoDownload=false 且 tools\\7zip\\ 为空）。若这个产物要对外发布，请先恢复下载或手工放入 7z.dll。");
        }

        // 2) 下载 + 解包
        string tempDir = Path.Combine(Path.GetTempPath(), $"aptt-7z-{Guid.NewGuid():N}");
        try
        {
            BuildTaskHelpers.EnsureTls12();

            using (var http = BuildTaskHelpers.CreateClient(TimeSpan.FromMinutes(5)))
            {
                string version = wantPinned ? pinned : BuildTaskHelpers.ResolveLatestVersion(ReleasesLatest, RetryLog);
                if (string.IsNullOrEmpty(version))
                {
                    if (File.Exists(cachedDll))
                    {
                        AcceptCache(cachedDll, cachedLicense, cachedVersion, "无法确定 7-Zip 最新版本，改用本地缓存的 7z.dll。");
                        return true;
                    }

                    return FailOrWarn("无法确定 7-Zip 最新版本，本次不内嵌 7z.dll。可用 -p:SevenZipVersion=x.xx 固定版本。");
                }

                Directory.CreateDirectory(tempDir);

                string sevenZr = Path.Combine(tempDir, "7zr.exe");
                string installer = Path.Combine(tempDir, "7z-installer.exe");
                string extractDir = Path.Combine(tempDir, "out");

                Log.LogMessage(MessageImportance.High, $"下载 7-Zip {version} 官方件…");

                Download(http, $"{ReleaseBase}{version}/7zr.exe", sevenZr);
                Download(http, $"{ReleaseBase}{version}/7z{version.Replace(".", string.Empty)}-x64.exe", installer);

                if (!TryExtractInstaller(sevenZr, installer, extractDir, tempDir, out string extractError))
                {
                    return FailOrWarn(extractError);
                }

                string extractedDll = Path.Combine(extractDir, "7z.dll");
                if (!File.Exists(extractedDll))
                {
                    return FailOrWarn("解包结果中没有 7z.dll，本次不内嵌。");
                }

                string extractedLicense = Path.Combine(extractDir, "License.txt");
                if (!File.Exists(extractedLicense))
                {
                    return FailOrWarn("解包结果中没有 License.txt。7-Zip 采用 LGPL，再分发必须附带许可证，故本次不内嵌 7z.dll。");
                }

                Directory.CreateDirectory(ToolDirectory);
                File.Copy(extractedDll, cachedDll, overwrite: true);
                File.Copy(extractedLicense, cachedLicense, overwrite: true);
                File.WriteAllText(versionFile, version);

                DllPath = cachedDll;
                LicensePath = cachedLicense;
                ResolvedVersion = version;
                WasDownloaded = true;
                Log.LogMessage(MessageImportance.High,
                    $"7z.dll {version} 已就绪：{cachedDll}（{new FileInfo(cachedDll).Length} 字节）");
                return true;
            }
        }
        catch (Exception ex)
        {
            // 本地开发：下载失败不阻止构建，程序会退回到「需安装 7-Zip」的原行为。
            // CI / 发布（RequireEmbeddedSevenZip=true）：必须失败，否则会静默上传不自包含的产物。
            return FailOrWarn($"自动获取 7z.dll 失败（{BuildTaskHelpers.Describe(ex)}），本次不内嵌。"
                              + " 可手工把 7z.dll 与 License.txt 放到 tools\\7zip\\ 后重新构建。");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private void AcceptCache(string dll, string license, string version, string message)
    {
        DllPath = dll;
        LicensePath = File.Exists(license) ? license : null;
        ResolvedVersion = version;
        Log.LogMessage(MessageImportance.High, message);
    }

    /// <summary>下载到文件。实例方法（非 static）是为了能访问 <see cref="RetryLog"/>。</summary>
    private void Download(HttpClient http, string url, string destination)
    {
        File.WriteAllBytes(destination, BuildTaskHelpers.GetBytesWithRetry(http, url, RetryLog));
    }

    /// <summary>
    /// 用官方 7zr 解包官方安装包（SFX）。不在此记录日志：失败原因通过 <paramref name="error"/> 交给调用方，
    /// 由 <see cref="FailOrWarn"/> 统一决定警告还是失败。
    /// </summary>
    private bool TryExtractInstaller(string sevenZr, string installer, string extractDir, string workingDirectory, out string error)
    {
        error = null;

        // 内联任务没有 ProcessStartInfo.ArgumentList，只能手工拼（并自行加引号）
        var psi = new ProcessStartInfo(sevenZr)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            Arguments = $"x {BuildTaskHelpers.Quote(installer)} -o{BuildTaskHelpers.Quote(extractDir)} -y",
        };

        using (var proc = Process.Start(psi))
        {
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                error = $"7zr 解包失败（exit {proc.ExitCode}）：{stderr}{stdout} 本次不内嵌 7z.dll。";
                return false;
            }

            return true;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败无关紧要
        }
    }
}
