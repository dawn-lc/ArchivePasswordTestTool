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
                && (!string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) || !string.IsNullOrWhiteSpace(asset.Url)));
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

        /// <summary>
        /// API 形式的资产地址（<c>api.github.com/.../releases/assets/{id}</c>）。
        /// 带上 <c>Accept: application/octet-stream</c> 时它直接 302 到下载 CDN，
        /// 是 <see cref="BrowserDownloadUrl"/> 走不通时的可行替代（见 <c>Upgrade.DownloadFile</c>）。
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

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
            ?? throw new InvalidOperationException(Resources.ProcessPathUnavailable);

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

        /// <summary>是否存在已下载并校验通过、可以立刻应用的更新</summary>
        public static bool IsReady => File.Exists(ReadyPath);

        /// <summary>
        /// 「替换后重启」的标记参数。带此参数的进程只报告新版本号就退出，不会重新跑一遍测密
        /// （重放用户的命令意味着白测一次，纯属浪费）。与 <c>-Worker</c> 一样是内部参数。
        /// </summary>
        public const string RestartedFlag = "-UpdateRestarted";

        /// <summary>
        /// 启动最早时机调用：若存在待应用的更新就替换自身并重启新进程。
        /// 返回 <see langword="true"/> 表示当前进程应立刻退出（新进程已接管）。
        /// </summary>
        public static bool TryApply(string[] args)
        {
            // 上一次更新成功后留下的旧版本：本次启动顺手清理，并标记「刚更新过」
            if (File.Exists(BackupPath) && File.Exists(ExecutablePath))
            {
                TryDelete(BackupPath);
                JustUpdated = !File.Exists(BackupPath);
            }

            return Replace(args);
        }

        /// <summary>
        /// 测密结束（正常结束、出错或被中断）后调用：把已校验的新版本替换上去并
        /// **立刻重启自身一次**，让更新当场生效，不必等用户下次启动。
        /// <para>
        /// 重启的子进程带 <see cref="RestartedFlag"/>，只报告版本后退出。
        /// 没有待应用更新时什么也不做。
        /// </para>
        /// </summary>
        /// <returns>是否已完成替换并拉起新进程</returns>
        public static bool TryApplyAndRestart(string[] args)
        {
            if (!IsReady)
            {
                return false;
            }

            // 标记放在最前：即使后面还跟着用户的原始参数，Main 也会先看到它并跳过测密
            var relaunchArgs = new string[args.Length + 1];
            relaunchArgs[0] = RestartedFlag;
            args.CopyTo(relaunchArgs, 1);

            return Replace(relaunchArgs);
        }

        /// <summary>报告本次启动是「更新后重启」（由 Main 在入口处调用）</summary>
        public static void ReportRestarted()
        {
            // 只打一行：上一次的输出在退出备用屏幕时已被整体移除，这里没有上下文可依赖
            Ui.WriteWarning(string.Format(Resources.UpgradeApplied, Program.VersionText));
        }

        /// <summary>
        /// 执行替换并拉起新进程。
        /// <para>
        /// 替换手法：Windows 允许**重命名正在运行的 exe**，因此
        /// 自身 → .old、.new → 自身，再启动新 exe，最后由新进程删除 .old。
        /// 这样不需要额外的 updater 进程，也不需要等待父进程退出。
        /// </para>
        /// 任何异常都回滚到旧二进制度，绝不把用户留在「两个文件都不完整」的状态。
        /// </summary>
        private static bool Replace(string[] relaunchArgs)
        {
            string executable = ExecutablePath;
            string ready = ReadyPath;
            string backup = BackupPath;

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

                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory,
                };
                foreach (string arg in relaunchArgs)
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
        /// 结束时等待后台**检查**阶段的上限。检查请求需要 1~2 秒，而快速测试
        /// （前几个密码就命中）往往几百毫秒就结束；不等待会导致更新提示几乎永远来不及展示。
        /// 已进入下载阶段时不受此限制（见 <c>WaitForBackgroundTask</c>）。
        /// </summary>
        private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(3);

        /// <summary>
        /// 下载单文件版本的超时。12MB 在慢速网络上可能要几分钟，
        /// 与检查请求的 10 秒不可共用同一个值。
        /// </summary>
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 下载的建连/握手超时。候选择地址最多试 4 次，若沿用 10 分钟，
        /// 一个静默丢包的地址能把退出卡住半小时——建连必须快速失败。
        /// </summary>
        private static readonly TimeSpan DownloadConnectTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 后台任务：一次做完「检查 + 下载」。
        /// <para>
        /// 下载在发现新版本时就地开始，与用户输入、测密全程重叠——等到测试结束才动手，
        /// 短测试会让用户白等一整个下载窗口，长测试则把传输整个堆到退出前。
        /// null 表示本次启动无需检查（已节流/开发构建/被环境变量禁用）。
        /// </para>
        /// </summary>
        private static Task<UpdateOutcome?>? pendingUpdate;

        /// <summary>
        /// 后台任务的当前阶段。结束时据此决定要不要等下载收尾——
        /// <c>Task.IsCompleted</c> 只能说明「整个任务是否结束」，区分不了「还在查」与「正在下载」。
        /// </summary>
        private static volatile UpdatePhase backgroundPhase;

        /// <summary>正在下载的版本 tag（仅 <see cref="UpdatePhase.Downloading"/> 阶段有效）</summary>
        private static volatile string? downloadingTag;

        /// <summary>后台更新的阶段</summary>
        private enum UpdatePhase
        {
            /// <summary>没有进行中的后台任务</summary>
            Idle,

            /// <summary>正在请求 Releases API</summary>
            Checking,

            /// <summary>已发现新版本，正在下载并校验</summary>
            Downloading,
        }

        /// <summary>
        /// 后台任务的产出。null 表示没有新版本、检查失败或无需处理（无人值守）。
        /// </summary>
        private sealed class UpdateOutcome
        {
            /// <summary>线上新版本的 tag</summary>
            public required string Tag { get; init; }

            /// <summary>Release 页面地址（无 exe 资产时用于引导用户手动下载）</summary>
            public string? HtmlUrl { get; init; }

            /// <summary>该 Release 是否带可下载的 exe 资产</summary>
            public bool HasAsset { get; init; } = true;

            /// <summary>已下载并校验通过，<c>.new</c> 已落盘待下次启动应用</summary>
            public bool Ready { get; init; }

            /// <summary>下载失败原因（<see cref="HasAsset"/> 为 true 且 <see cref="Ready"/> 为 false 时有效）</summary>
            public string? Error { get; init; }
        }
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
        /// 启动时调用（越早越好）：距上次**尝试**满 1 小时才在后台发起检查，
        /// 发现新版本就在同一个后台任务里立刻下载（不阻塞、不打印）。
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

                // 先置阶段再起任务：任务可能在赋值前就跑到下载阶段
                backgroundPhase = UpdatePhase.Checking;
                pendingUpdate = Task.Run(CheckAndDownload);
            }
            catch
            {
                // 尽力而为：读配置失败等一律跳过
                backgroundPhase = UpdatePhase.Idle;
                pendingUpdate = null;
            }
        }

        /// <summary>
        /// 测试结束后调用：消费后台结果并（视情况）提示更新。
        /// <para>
        /// 检查与下载在启动时就已发起，正常情况下这里直接拿结果；只有测试结束得比下载还快
        /// 才会等待（见 <see cref="WaitForBackgroundTask"/>）。重定向场景不等待也不下载
        /// （无人值守，纯属浪费）。
        /// </para>
        /// </summary>
        public static void CompleteBackgroundCheck()
        {
            var task = pendingUpdate;
            pendingUpdate = null;

            if (task is null)
            {
                return;
            }

            bool announced = !task.IsCompleted
                && !Console.IsInputRedirected
                && WaitForBackgroundTask(task);

            if (!task.IsCompletedSuccessfully)
            {
                // 未完成或失败：不写 lastNotifiedVersion，下次（1 小时后）重试
                return;
            }

            try
            {
                AnnounceAndRecord(task.Result, announced);
            }
            catch
            {
                // 尽力而为：任何失败静默跳过
            }
        }

        /// <summary>
        /// 结束时等后台任务收尾，返回是否已经提示过「发现新版本」。
        /// <para>
        /// 只给检查一个有上限的等待窗口：检查 1~2 秒即可完成，而快速测试（前几个密码就命中）
        /// 往往几百毫秒就结束，不等待就永远看不到提示。此时测试结果面板已输出完毕，
        /// 用户正在阅读结果，短暂等待无感。
        /// </para>
        /// <para>
        /// 一旦进入下载阶段则提示后等它收尾（下载自带连接/总超时兜底）：
        /// 否则进程退出会中断传输，留下的半截文件只能等下次启动重下。
        /// </para>
        /// </summary>
        private static bool WaitForBackgroundTask(Task<UpdateOutcome?> task)
        {
            bool completed;
            try
            {
                completed = task.Wait(CompletionGrace);
            }
            catch
            {
                // 任务内异常由调用方的 IsCompletedSuccessfully 统一处理；
                // 此处若让异常逃逸会中断调用方的 finally，连带跳过"保存测试结果"确认
                return false;
            }

            if (completed || backgroundPhase != UpdatePhase.Downloading)
            {
                return false;
            }

            AnnounceDownloading();

            try
            {
                task.Wait();
            }
            catch
            {
                // 同上：最终结果由调用方的 IsCompletedSuccessfully 判定
            }

            return true;
        }

        /// <summary>
        /// 后台线程：查询最新版，发现新版就立刻下载（<b>全程不输出</b>——控制台只归主线程，
        /// 后台写屏会打断正在渲染的进度条）。结果交给 <see cref="CompleteBackgroundCheck"/> 呈现。
        /// </summary>
        private static UpdateOutcome? CheckAndDownload()
        {
            try
            {
                var info = FetchLatestRelease();
                string? tag = info?.TagName;

                if (string.IsNullOrWhiteSpace(tag))
                {
                    // 请求失败（网络故障、或 GitHub API 限流 403）
                    return null;
                }

                // 无人值守（管道/无输入）：下载了也没人消费，还白占带宽
                if (Console.IsInputRedirected)
                {
                    return null;
                }

                // 已提示并处置过的版本不再重复下载
                if (!ShouldPrompt(tag, Program.Version, Program.VersionType, LoadConfig().LastNotifiedVersion))
                {
                    return null;
                }

                UpgradeAsset? asset = info!.MainAsset;
                if (asset is null)
                {
                    // 该 Release 没有可下载 exe（例如手工创建的 Release）：结束时引导到发布页
                    return new UpdateOutcome { Tag = tag, HtmlUrl = info.HtmlUrl, HasAsset = false };
                }

                downloadingTag = tag;
                backgroundPhase = UpdatePhase.Downloading;

                try
                {
                    // 先下到临时名，校验通过才改成 .new：避免半截文件在下个启动被当成有效更新
                    string downloading = PendingUpdate.DownloadingPath;
                    PendingUpdate.TryDelete(downloading);

                    DownloadFile(asset, downloading);
                    VerifyDownload(downloading, asset);

                    PendingUpdate.TryDelete(PendingUpdate.ReadyPath);
                    File.Move(downloading, PendingUpdate.ReadyPath);

                    return new UpdateOutcome { Tag = tag, HtmlUrl = info.HtmlUrl, Ready = true };
                }
                catch (Exception ex)
                {
                    // 目录只读（如装在 Program Files）、磁盘满、网络中断、校验失败等：
                    // 收走半截文件，失败原因留到结束时提示
                    PendingUpdate.TryDelete(PendingUpdate.DownloadingPath);

                    return new UpdateOutcome { Tag = tag, HtmlUrl = info.HtmlUrl, Error = ex.Message };
                }
                finally
                {
                    downloadingTag = null;
                    backgroundPhase = UpdatePhase.Idle;
                }
            }
            catch
            {
                // 尽力而为：任何失败都按「没有更新」处理，下个周期重试
                return null;
            }
        }

        /// <summary>提示「发现新版本 + 正在下载」（只允许主线程调用）</summary>
        private static void AnnounceDownloading()
        {
            if (downloadingTag is { } tag)
            {
                Ui.WriteWarning(string.Format(Resources.UpgradeAvailable, tag));
            }

            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Resources.UpgradeDownloading)}[/]");
        }

        /// <summary>
        /// 主线程收尾：把后台结果翻译成用户可见的提示，并记录已处置的版本。
        /// <paramref name="announced"/> 为 true 表示「发现新版本」那句已经打印过，不再重复。
        /// </summary>
        private static void AnnounceAndRecord(UpdateOutcome? outcome, bool announced)
        {
            if (outcome is null)
            {
                return;
            }

            if (!announced)
            {
                Ui.WriteWarning(string.Format(Resources.UpgradeAvailable, outcome.Tag));
            }

            if (!outcome.HasAsset)
            {
                // 无 exe 资产：退回发布页引导。用户已被告知且已引导，不再重复烦他
                if (InteractivePrompts.ConfirmSingleKey(Resources.UpgradeOpenReleasesPrompt, defaultYes: true))
                {
                    OpenReleasesPage(outcome.HtmlUrl ?? ReleasesPageUrl);
                }

                RecordNotified(outcome.Tag);
                return;
            }

            if (outcome.Ready)
            {
                Ui.WriteWarning(Resources.UpgradeReadyForNextLaunch);
                RecordNotified(outcome.Tag);
                return;
            }

            // 下载失败：只提示不记已提示标记，1 小时后重试，否则该版本会被永久跳过
            Ui.WriteWarning(string.Format(Resources.UpgradeDownloadFailed, outcome.Error ?? string.Empty));
        }

        /// <summary>记下已处置的版本（每版本只提示/下载一次的前提）</summary>
        private static void RecordNotified(string tag)
        {
            var config = LoadConfig();
            config.LastNotifiedVersion = tag;
            SaveConfig(config);
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

        /// <summary>
        /// 下载资产到指定路径：多个候选地址 × 两种 TLS 模式的兜底矩阵，任一成功即返回。
        /// <para>
        /// 候选一：API 资产地址（<c>api.github.com/.../releases/assets/{id}</c> + 
        /// <c>Accept: application/octet-stream</c>，会 302 到下载 CDN）。它比
        /// <c>browser_download_url</c> 更可靠：部分网络环境只阻断 <c>github.com</c>
        /// 而 <c>api.github.com</c> 可用（实测：能查到新版本、却卡在下载握手）。
        /// </para>
        /// <para>
        /// 两种 TLS 模式：空 SNI 优先（防按域名嗅探），失败回退标准握手——
        /// 与 <see cref="FetchLatestRelease"/> 对称，不对称就会出现「发现得了却下不下来」。
        /// 空 SNI 下主机名不匹配无从察觉，但内容由 <see cref="VerifyDownload"/> 的
        /// sha256 digest 兜底，比 API 响应本身更安全。
        /// </para>
        /// </summary>
        private static void DownloadFile(UpgradeAsset asset, string destination)
        {
            Exception? lastError = null;

            foreach ((string? Url, bool AcceptOctetStream) candidate in DownloadCandidates(asset))
            {
                foreach (bool useEmptySni in new[] { true, false })
                {
                    try
                    {
                        DownloadFile(candidate.Url!, destination, useEmptySni, candidate.AcceptOctetStream);
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 半截文件必须清掉：下一次尝试会重新 File.Create 覆盖，
                        // 但残留文件可能在全部失败后被误认为下载成功
                        lastError = ex;
                        PendingUpdate.TryDelete(destination);
                    }
                }
            }

            throw lastError ?? new InvalidOperationException("no download url in release asset");
        }

        /// <summary>候选下载地址，按可靠性降序（API 地址在前）</summary>
        private static IEnumerable<(string? Url, bool AcceptOctetStream)> DownloadCandidates(UpgradeAsset asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.Url))
            {
                yield return (asset.Url, true);
            }

            if (!string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                yield return (asset.BrowserDownloadUrl, false);
            }
        }

        private static void DownloadFile(string url, string destination, bool useEmptySni, bool acceptOctetStream)
        {
            using var http = useEmptySni
                ? HttpTransport.CreateClient(DownloadTimeout, DownloadConnectTimeout)
                : HttpTransport.CreateStandardClient(DownloadTimeout, DownloadConnectTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (acceptOctetStream)
            {
                // 必须显式声明：否则 api.github.com 会把资产当元数据返回 JSON（200 却下错东西）
                request.Headers.Accept.ParseAdd("application/octet-stream");
            }

            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var source = response.Content.ReadAsStream();
            using var target = File.Create(destination);
            source.CopyTo(target);
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
