using ArchivePasswordTestTool.Properties;
using Spectre.Console;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchivePasswordTestTool
{
    /// <summary>GitHub Release 最新版信息（仅提取所需字段）</summary>
    internal sealed class UpgradeReleasesInfo
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<UpgradeAsset>? Assets { get; set; }

        /// <summary>首个可下载的 exe 资产（单文件发布的唯一产物）</summary>
        public UpgradeAsset? MainAsset =>
            Assets?.FirstOrDefault(asset =>
                asset.Name is not null
                && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));
    }

    /// <summary>Release 附件（GitHub 会自动提供 digest，用于校验下载完整性）</summary>
    internal sealed class UpgradeAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        /// <summary>形如 <c>sha256:abcdef...</c>；旧版 Release 可能为空</summary>
        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }

    /// <summary>更新检查本地配置（config.json）</summary>
    internal sealed class UpgradeConfig
    {
        /// <summary>上次发起检查的时间（用于请求节流）</summary>
        [JsonPropertyName("lastCheckUpgrade")]
        public DateTime LastCheckUpgrade { get; set; } = DateTime.MinValue;

        /// <summary>
        /// 上次已提示过的版本 tag。用于「每个版本只提示一次」——
        /// 有了它才能把请求节流缩短到 1 小时而不重复骚扰用户。
        /// </summary>
        [JsonPropertyName("lastNotifiedVersion")]
        public string? LastNotifiedVersion { get; set; }
    }

    // 源生成序列化：IL 裁剪后反射式 JsonSerializer 被禁用
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(UpgradeConfig))]
    [JsonSerializable(typeof(UpgradeReleasesInfo))]
    internal partial class UpgradeJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// 待应用更新的落盘位置与命名约定。
    /// <para>
    /// 与主程序同目录（而非临时目录）：Windows 只允许「同卷」重命名，
    /// 跨盘 MoveFile 会退化成复制+删除，替换过程不再是原子的。
    /// </para>
    /// </summary>
    internal static class PendingUpdate
    {
        /// <summary>下载中的临时后缀（下载完成才改为正式待应用名）</summary>
        public const string DownloadingSuffix = ".new.downloading";
        /// <summary>已下载并校验通过、等待下次启动应用</summary>
        public const string ReadySuffix = ".new";

        /// <summary>应用期间备份的旧版本，用于失败回滚与重启后清理</summary>
        public const string BackupSuffix = ".old";

        /// <summary>主程序自身路径（单文件发布下即 exe 本体）</summary>
        public static string ExecutablePath =>
            Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定主程序路径");

        public static string DownloadingPath => ExecutablePath + DownloadingSuffix;

        public static string ReadyPath => ExecutablePath + ReadySuffix;

        public static string BackupPath => ExecutablePath + BackupSuffix;

        /// <summary>静默删除（文件可能被占用或不存在，失败不影响主流程）</summary>
        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 残留文件会在下次下载/应用时被覆盖
            }
        }

        /// <summary>本次启动是否刚刚完成自我替换（用于提示用户）</summary>
        public static bool JustUpdated { get; private set; }

        /// <summary>
        /// 启动最早时机调用：若存在待应用的更新就替换自身并重启新进程。
        /// 返回 <see langword="true"/> 表示当前进程应立刻退出（新进程已接管）。
        /// <para>
        /// 替换手法：Windows 允许**重命名正在运行的 exe**，因此
        /// 自身 → .old、.new → 自身，再启动新 exe，最后由新进程删除 .old。
        /// 这样不需要额外的 updater 进程，也不需要等待父进程退出。
        /// </para>
        /// 任何异常都回滚到旧二进制度，绝不把用户留在「两个文件都不完整」的状态。
        /// </summary>
        public static bool TryApply(string[] args)
        {
            string executable = PendingUpdate.ExecutablePath;
            string ready = PendingUpdate.ReadyPath;
            string backup = PendingUpdate.BackupPath;

            // 上一次更新成功后留下的旧版本：本次启动顺手清理，并标记「刚更新过」
            if (File.Exists(backup) && File.Exists(executable))
            {
                PendingUpdate.TryDelete(backup);
                JustUpdated = !File.Exists(backup);
            }

            if (!File.Exists(ready))
            {
                return false;
            }

            try
            {
                File.Move(executable, backup, overwrite: true);
            }
            catch
            {
                // 无法重命名自身（只读目录、被安全软件锁定等）：放弃本次更新，
                // 保留 .new 等待下次，不打扰用户
                return false;
            }

            try
            {
                File.Move(ready, executable);

                // 新进程从当前 exe 启动，参数原样透传（保持用户此次的调用意图）
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory,
                };
                foreach (string arg in args)
                {
                    startInfo.ArgumentList.Add(arg);
                }

                Process.Start(startInfo);
                return true;
            }
            catch
            {
                // 替换或启动失败：回滚旧版本，避免用户无程序可用
                try
                {
                    if (!File.Exists(executable) && File.Exists(backup))
                    {
                        File.Move(backup, executable);
                    }
                }
                catch
                {
                    // 回滚也失败只能放弃；.old 仍在原处，用户可手动改回
                }

                return false;
            }
        }
    }

    /// <summary>
    /// 版本更新检查：通过 GitHub Releases API 获取最新版本（tag 格式 "x.y.z-Type"），
    /// 比当前版本新时提示并可选打开发布页。
    /// <para>
    /// 两个独立状态，各管一件事：
    /// <list type="bullet">
    /// <item><c>lastCheckUpgrade</c> —— 网络请求节流，1 小时一次</item>
    /// <item><c>lastNotifiedVersion</c> —— 已提示过的版本，保证同一版本只提示一次</item>
    /// </list>
    /// 早期实现只用「24 小时节流」一个状态，导致发布紧跟在上次检查之后时，
    /// 用户要整整一天才可能看到提示（实测被用户撞到）。缩短节流 + 每版本只提示一次，
    /// 才能既及时又不骚扰。
    /// </para>
    /// </summary>
    internal static class Upgrade
    {
        private const string RepoUrl = "https://api.github.com/repos/dawn-lc/ArchivePasswordTestTool/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/dawn-lc/ArchivePasswordTestTool/releases/latest";
        private const string ConfigFolderName = "ArchivePasswordTestTool";
        private const string ConfigFileName = "config.json";

        /// <summary>禁用启动检查的环境变量（设为 1 即完全跳过）</summary>
        private const string DisableEnvVar = "APTT_NO_UPGRADE_CHECK";

        /// <summary>
        /// 网络请求节流间隔。取 1 小时而非 24 小时：GitHub 未认证配额是 60 次/小时，
        /// 一小时一次远在限额内，同时保证新版本发布后最多 1 小时就能被发现。
        /// </summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// 后台检查的等待上限。后台请求需要 1~2 秒，而快速测试（前几个密码就命中）
        /// 往往几百毫秒就结束；不等待会导致更新提示几乎永远来不及展示。
        /// </summary>
        private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(3);

        /// <summary>后台进行中的检查（null 表示本次启动无需检查）</summary>
        private static Task<UpgradeReleasesInfo?>? pendingCheck;

        /// <summary>
        /// 配置文件放在用户本地应用数据目录：原先用相对路径写在当前工作目录，
        /// 从不同目录启动就会到处生成 config.json。
        /// </summary>
        private static string ConfigFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ConfigFolderName,
            ConfigFileName);

        /// <summary>
        /// 是否到该发起网络检查的时间（纯函数，便于单测）。
        /// </summary>
        internal static bool IsCheckDue(DateTime lastCheck, DateTime now) =>
            now - lastCheck >= CheckInterval;

        /// <summary>
        /// 是否应当向用户提示更新（纯函数，便于单测）：
        /// 线上 tag 确实更新，且与上次已提示过的版本不同。
        /// </summary>
        internal static bool ShouldPrompt(
            string? latestTag,
            int[] currentVersion,
            string currentVersionType,
            string? lastNotifiedVersion)
        {
            if (string.IsNullOrWhiteSpace(latestTag))
            {
                return false;
            }

            if (string.Equals(latestTag, lastNotifiedVersion, StringComparison.OrdinalIgnoreCase))
            {
                // 同一版本已提示过：不重复骚扰（这是把请求节流缩短到 1 小时的前提）
                return false;
            }

            return IsNewerVersion(latestTag, currentVersion, currentVersionType);
        }

        /// <summary>
        /// 启动时调用：距上次**尝试**满 1 小时才在后台发起检查（不阻塞、不打印）。
        /// 时间戳在本次尝试时就写入（无论成败），用于约束重试频率——
        /// 否则离线用户每次启动都要白等一个等待窗口。
        /// </summary>
        public static void StartBackgroundCheck()
        {
            try
            {
                if (Environment.GetEnvironmentVariable(DisableEnvVar) == "1")
                {
                    return;
                }

                // 0.0.0 是未注入版本号的本地开发构建（见 Program.Version）：
                // 它必然「旧于」任何线上发布版，检查只会产生无意义的更新提示
                if (IsDevelopmentBuild())
                {
                    return;
                }

                var config = LoadConfig();
                if (!IsCheckDue(config.LastCheckUpgrade, DateTime.Now))
                {
                    return;
                }

                // 记录本次尝试（保留已提示版本），失败也会被节流 1 小时
                config.LastCheckUpgrade = DateTime.Now;
                SaveConfig(config);

                pendingCheck = Task.Run(FetchLatestRelease);
            }
            catch
            {
                // 尽力而为：读配置失败等一律跳过
                pendingCheck = null;
            }
        }

        /// <summary>
        /// 测试结束后调用：消费后台结果并（视情况）提示更新。
        /// <para>
        /// 后台请求需要 1~2 秒，而快速测试（前几个密码就命中）往往几百毫秒就结束——
        /// 若「未完成就放弃」，更新提示几乎永远不会出现。
        /// 因此给一个有上限的等待窗口：此时测试结果面板已输出完毕，用户正在阅读结果，
        /// 短暂等待无感。重定向场景不等待（无人值守，纯属浪费）。
        /// </para>
        /// </summary>
        public static void CompleteBackgroundCheck()
        {
            var task = pendingCheck;
            pendingCheck = null;

            if (task is null)
            {
                return;
            }

            if (!task.IsCompleted && !Console.IsInputRedirected)
            {
                try
                {
                    task.Wait(CompletionGrace);
                }
                catch
                {
                    // 任务内异常由下面的 IsCompletedSuccessfully 统一处理；
                    // 此处若让异常逃逸会中断调用方的 finally，连带跳过"保存测试结果"确认
                }
            }

            if (!task.IsCompletedSuccessfully)
            {
                // 未完成或失败：不写 lastNotifiedVersion，下次（1 小时后）重试
                return;
            }

            try
            {
                var info = task.Result;
                if (info is null)
                {
                    // 请求失败（网络故障、或 GitHub API 限流 403）
                    return;
                }

                var config = LoadConfig();
                if (!ShouldPrompt(info.TagName, Program.Version, Program.VersionType, config.LastNotifiedVersion))
                {
                    return;
                }

                // 无人值守环境不下新版、也不提示：下载 12MB 无人消费，纯属浪费
                if (Console.IsInputRedirected)
                {
                    return;
                }

                Ui.WriteWarning(string.Format(Resources.UpgradeAvailable, info.TagName));

                // 「已提示」标记只在本次确实把事办完时落下：下载失败（目录只读、断网、
                // 校验不过）要留待 1 小时后重试，否则该版本会被永久跳过。
                if (DownloadPendingUpdate(info))
                {
                    config.LastNotifiedVersion = info.TagName;
                    SaveConfig(config);
                }
            }
            catch
            {
                // 尽力而为：任何失败静默跳过
            }
        }

        /// <summary>
        /// 下载新版本到主程序旁，等待下次启动时替换。
        /// 单文件发布下 exe 运行期间无法自我覆盖，故必须分两次启动完成更新。
        /// 全程尽力而为：目录不可写、校验失败等只提示不阻断退出。
        /// </summary>
        /// <returns>
        /// 是否已「处置完」该版本（下载成功，或无 exe 资产已引导到发布页）。
        /// 返回 <see langword="false"/> 时调用方不记已提示标记，下个周期会重试。
        /// </returns>
        private static bool DownloadPendingUpdate(UpgradeReleasesInfo info)
        {
            UpgradeAsset? asset = info.MainAsset;
            if (asset?.BrowserDownloadUrl is null)
            {
                // 该 Release 没有可下载 exe（例如手工创建的 Release）：退回发布页引导
                if (InteractivePrompts.ConfirmSingleKey(Resources.UpgradeOpenReleasesPrompt, defaultYes: true))
                {
                    OpenReleasesPage(info.HtmlUrl ?? ReleasesPageUrl);
                }

                // 用户已被告知且已引导到发布页，不再重复烦他
                return true;
            }

            try
            {
                // 先下到临时名，校验通过才改成 .new：避免半截文件在下个启动被当成有效更新
                string downloading = PendingUpdate.DownloadingPath;
                PendingUpdate.TryDelete(downloading);

                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Resources.UpgradeDownloading)}[/]");

                using (var http = HttpTransport.CreateStandardClient(TimeSpan.FromMinutes(10)))
                using (var response = http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead)
                           .GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();

                    using var source = response.Content.ReadAsStream();
                    using var target = File.Create(downloading);
                    source.CopyTo(target);
                }

                VerifyDownload(downloading, asset);

                PendingUpdate.TryDelete(PendingUpdate.ReadyPath);
                File.Move(downloading, PendingUpdate.ReadyPath);
                Ui.WriteWarning(Resources.UpgradeReadyForNextLaunch);

                return true;
            }
            catch (Exception ex)
            {
                // 目录只读（如装在 Program Files）、磁盘满、网络中断、校验失败等：提示后正常退出
                PendingUpdate.TryDelete(PendingUpdate.DownloadingPath);
                Ui.WriteWarning(string.Format(Resources.UpgradeDownloadFailed, ex.Message));

                return false;
            }
        }

        /// <summary>
        /// 校验下载文件：优先比对 GitHub 提供的 digest（sha256），
        /// 缺失 digest 时退化为大小校验（老版本 Release 可能没有该字段）。
        /// 不通过即抛异常——失败原因直接进入提示文案，无需另设返回值。
        /// </summary>
        internal static void VerifyDownload(string filePath, UpgradeAsset asset)
        {
            var file = new FileInfo(filePath);
            if (!file.Exists || file.Length == 0)
            {
                throw new InvalidDataException("downloaded file is empty");
            }

            if (asset.Size > 0 && file.Length != asset.Size)
            {
                throw new InvalidDataException($"size mismatch: expected {asset.Size}, got {file.Length}");
            }

            const string Sha256Prefix = "sha256:";
            if (asset.Digest is { } digest
                && digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(filePath);
                string actual = Convert.ToHexString(SHA256.HashData(stream));
                string expected = digest[Sha256Prefix.Length..];

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("sha256 mismatch");
                }
            }
            // 没有 digest 只能信大小；体积相同即认为可用（失败会在下个启动被 TryApply 回滚）
        }

        /// <summary>
        /// 是否为本地开发构建（未注入版本号，见 Program.VersionType）。
        /// 这类构建必然「旧于」任何线上发布版，检查只会产生无意义的更新提示。
        /// 判定依据是显式的版本类型标记而非「版本号是否为 0」：后者在版本元数据
        /// 意外缺失时会把发布版误判成开发版，从而静默关闭更新检查
        /// （CI 的发布产物实测会断言版本号，元数据丢失在流水线即暴露）。
        /// </summary>
        private static bool IsDevelopmentBuild() =>
            string.Equals(Program.VersionType, Program.DevelopmentVersionType, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 获取最新 Release：优先空 SNI（规避按域名嗅探阻断的中间设备），
        /// 失败则回退普通 TLS 连接（个别网络可能对无 SNI 更严格）
        /// </summary>
        private static UpgradeReleasesInfo? FetchLatestRelease()
        {
            try
            {
                return FetchLatestRelease(useEmptySni: true);
            }
            catch
            {
                // 空 SNI 失败（DNS/连接/证书链问题）→ 回退标准握手
            }

            try
            {
                return FetchLatestRelease(useEmptySni: false);
            }
            catch
            {
                return null;
            }
        }

        private static UpgradeReleasesInfo? FetchLatestRelease(bool useEmptySni)
        {
            using var http = useEmptySni
                ? HttpTransport.CreateClient(TimeSpan.FromSeconds(10))
                : HttpTransport.CreateStandardClient(TimeSpan.FromSeconds(10));

            using var response = http.GetAsync(RepoUrl).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonSerializer.Deserialize(body, UpgradeJsonContext.Default.UpgradeReleasesInfo);
        }

        /// <summary>用系统默认浏览器打开发布页；失败仅提示</summary>
        private static void OpenReleasesPage(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Ui.WriteError(string.Format(Resources.OpenBrowserFailed, ex.Message));
            }
        }

        private static UpgradeConfig LoadConfig()
        {
            try
            {
                string path = ConfigFilePath;
                if (File.Exists(path))
                {
                    var config = JsonSerializer.Deserialize(File.ReadAllText(path), UpgradeJsonContext.Default.UpgradeConfig);
                    if (config is not null)
                    {
                        return config;
                    }
                }
            }
            catch
            {
                // 配置损坏视为首次运行
            }

            return new UpgradeConfig();
        }

        private static void SaveConfig(UpgradeConfig config)
        {
            try
            {
                string path = ConfigFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(config, UpgradeJsonContext.Default.UpgradeConfig));
            }
            catch
            {
                // 只读/无权限等场景静默（下次启动会重查一次，可接受）
            }
        }

        // ---------- 版本比较（移植自旧版 Utils.Upgrade） ----------

        /// <summary>
        /// 判断 tag 版本是否比当前版本新。tag 格式 "x.y.z-Type"（Type 可省略）。
        /// 数值段逐位比较；同版本号时按版本类型等级比较
        /// </summary>
        internal static bool IsNewerVersion(string tagName, int[] currentVersion, string currentVersionType)
        {
            try
            {
                // 剥离常见前缀 v/V
                string core = tagName.Trim();
                if (core.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    core = core[1..];
                }

                var parts = core.Split('-', 2);
                var numbers = parts[0].Split('.').Select(int.Parse).ToArray();
                string tagType = parts.Length > 1 ? parts[1] : "Release";

                if (numbers.Length != currentVersion.Length)
                {
                    // 版本号段数不同无法比较，视为不更新（保守策略：不打扰用户）
                    return false;
                }

                int cmp = CompareVersion(currentVersion, numbers);
                if (cmp != 0)
                {
                    return cmp < 0;
                }

                return CompareVersionType(currentVersionType, tagType) < 0;
            }
            catch
            {
                // 无法解析的 tag 视为不更新
                return false;
            }
        }

        /// <returns>目标较高返回负数（升序比较器语义），相等返回 0，当前较高返回正数</returns>
        private static int CompareVersion(int[] current, int[] target)
        {
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] != target[i])
                {
                    return current[i].CompareTo(target[i]);
                }
            }

            return 0;
        }

        /// <summary>
        /// 版本类型等级（大 = 更稳定）：fixpush 为热修最高，final/full 次之，
        /// 依次 release → preview → beta → alpha → free/demo/test。
        /// 未识别的类型按最低档（有新版就提示，宁可信其有）
        /// </summary>
        private static readonly string[][] VersionTypeRank =
        [
            ["free", "demo", "test"],      // 最低
            ["alpha"],
            ["beta"],
            ["preview"],
            ["release", "release", "candidate", "rc"],
            ["final", "full", "version", "enhance", "standard"],
            ["fixpush"],                    // 最高（热修）
        ];

        /// <returns>目标等级更高返回负数，相等 0，当前更高正数</returns>
        private static int CompareVersionType(string currentType, string targetType)
        {
            int targetRank = RankOf(targetType);
            if (targetRank < 0)
            {
                // 未识别的目标类型：视为比任何已知类型都新（宁可信其有）
                return -1;
            }

            int currentRank = RankOf(currentType);
            if (currentRank < 0)
            {
                // 当前类型未识别而目标识别：按保守处理（不提示）
                return 1;
            }

            return currentRank.CompareTo(targetRank);
        }

        private static int RankOf(string type)
        {
            for (int i = 0; i < VersionTypeRank.Length; i++)
            {
                if (VersionTypeRank[i].Contains(type.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
