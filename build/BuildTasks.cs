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

// 构建期的两个 MSBuild 内联任务（由 csproj 的 <UsingTask ... > 引用本文件）：
//   · EnsureUpxToolTask     —— 解析/下载 upx.exe
//   · EnsureSevenZipDllTask —— 下载 7-Zip 官方件并取出 7z.dll + License.txt
//
// 放在独立 .cs 文件而不是 csproj 里的 CDATA 块：
//   ① 有语法高亮/补全/重构，改错在编辑器里就能看出来；
//   ② 两个任务共用同一份网络与文本辅助，不再各写一遍（原先 CDATA 版本是两份近似副本）；
//   ③ 不受 XML 转义限制，也不用担心 ]]&gt; 意外截断。
//
// 注意：RoslynCodeTaskFactory 的编译目标框架较旧（无 .NET Core 的 ProcessStartInfo.ArgumentList、
// 无 string.Split 的某些重载）。此处只用该参考集里确定存在的 API。

/// <summary>两个任务共用的网络 / 文本辅助。</summary>
public static class BuildTaskHelpers
{
    /// <summary>下载与探测的重试次数。一次性失败会让构建静默产出「未内嵌」的二进制，代价不对称。</summary>
    public const int DownloadAttempts = 3;

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
    /// 而同一地址下一秒往往又能连通。
    /// </summary>
    public static byte[] GetBytesWithRetry(HttpClient client, string url)
    {
        return Retry(
            () => client.GetByteArrayAsync(url).GetAwaiter().GetResult(),
            $"下载失败（已重试 {DownloadAttempts} 次）：{url}");
    }

    /// <summary>发请求拿响应（常用于读取 302 的 Location），失败重试</summary>
    public static HttpResponseMessage GetResponseWithRetry(HttpClient client, string url)
    {
        return Retry(
            () => client.GetAsync(url).GetAwaiter().GetResult(),
            $"请求失败（已重试 {DownloadAttempts} 次）：{url}");
    }

    private static T Retry<T>(Func<T> action, string failureMessage)
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
                    Thread.Sleep(attempt * 1000);
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
    public static string ResolveLatestVersion(string releasesLatestUrl)
    {
        using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
        using (var probe = CreateClient(TimeSpan.FromSeconds(30)))
        {
            probe.DefaultRequestHeaders.Clear();
            probe.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using (var response = GetResponseWithRetry(
                       new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
                       releasesLatestUrl))
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
    /// 请求的是否为固定版本。<c>null</c> / 空 / "latest" 都视为「跟随最新」，
    /// 此时缓存一律视为有效（否则每次构建都要联网探测）。
    /// </summary>
    public static bool IsPinned(string requestedVersion, out string pinned)
    {
        pinned = requestedVersion;
        return !string.IsNullOrEmpty(requestedVersion)
               && !requestedVersion.Equals("latest", StringComparison.OrdinalIgnoreCase);
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
                string version = wantPinned ? pinned : BuildTaskHelpers.ResolveLatestVersion(ReleasesLatest);
                if (string.IsNullOrEmpty(version))
                {
                    Log.LogWarning("无法确定 UPX 最新版本，跳过压缩。可改用 -p:UpxVersion=x.y.z 固定版本。");
                    return true;
                }

                string zipUrl = $"https://github.com/upx/upx/releases/download/v{version}/upx-{version}-win64.zip";
                Log.LogMessage(MessageImportance.High, $"下载 UPX {version}：{zipUrl}");

                byte[] payload = BuildTaskHelpers.GetBytesWithRetry(http, zipUrl);

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
                string version = wantPinned ? pinned : BuildTaskHelpers.ResolveLatestVersion(ReleasesLatest);
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

    private static void Download(HttpClient http, string url, string destination)
    {
        File.WriteAllBytes(destination, BuildTaskHelpers.GetBytesWithRetry(http, url));
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
