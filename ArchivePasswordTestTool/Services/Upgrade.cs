using ArchivePasswordTestTool.Properties;
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

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }

    /// <summary>更新检查本地配置（config.json：上次检查时间）</summary>
    internal sealed class UpgradeConfig
    {
        [JsonPropertyName("lastCheckUpgrade")]
        public DateTime LastCheckUpgrade { get; set; } = DateTime.MinValue;
    }

    // 源生成序列化：IL 裁剪后反射式 JsonSerializer 被禁用
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(UpgradeConfig))]
    [JsonSerializable(typeof(UpgradeReleasesInfo))]
    internal partial class UpgradeJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// 版本更新检查（能力移植自旧版 Utils.Upgrade，重构为按场景收敛）：
    /// 通过 GitHub Releases API 获取最新版本（tag 格式 "x.y.z-Type"），
    /// 与当前版本比较后提示更新并可选打开发布页。
    /// 检查间隔 24h（配置文件记录上次**成功**检查的时间），失败静默跳过不阻断主流程。
    /// 网络请求在后台发起（不阻塞首屏与引导），测试结束后再消费结果——
    /// 除首次运行外用户不会感到丝毫等待。
    /// </summary>
    internal static class Upgrade
    {
        private const string RepoUrl = "https://api.github.com/repos/dawn-lc/ArchivePasswordTestTool/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/dawn-lc/ArchivePasswordTestTool/releases/latest";
        private const string ConfigFolderName = "ArchivePasswordTestTool";
        private const string ConfigFileName = "config.json";

        /// <summary>禁用启动检查的环境变量（设为 1 即完全跳过）</summary>
        private const string DisableEnvVar = "APTT_NO_UPGRADE_CHECK";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

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
        /// 启动时调用：满足 24h 间隔才在后台发起检查（不阻塞、不打印）。
        /// 时间戳由 <see cref="CompleteBackgroundCheck"/> 在真正消费结果后才写入——
        /// 若写在请求内部，一旦本次来不及展示提示（后台请求未完成），
        /// 用户就要再等一个 24h 周期才能看到，等于把提示静默吞掉。
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
                if (DateTime.Now - config.LastCheckUpgrade < CheckInterval)
                {
                    return;
                }

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
        /// 若像早年实现那样「未完成就放弃」，更新提示几乎永远不会出现。
        /// 因此这里给一个有上限的等待窗口：此时测试结果面板已输出完毕，
        /// 用户正在阅读结果或多等一个确认，短暂等待无感。重定向场景不等待（无人值守，纯属浪费）。
        /// </para>
        /// 只有真正拿到响应才写时间戳；放弃时不写，下次启动立即重试。
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
                // 仍未完成或已失败：不写时间戳，下次启动会立即重试（而非等满 24h）
                return;
            }

            try
            {
                var info = task.Result;
                if (info is null)
                {
                    // 请求失败（网络故障、或 GitHub API 限流 403）：
                    // 不写时间戳，下次启动立即重试
                    return;
                }

                SaveConfig(new UpgradeConfig { LastCheckUpgrade = DateTime.Now });

                if (info.TagName is null
                    || !IsNewerVersion(info.TagName, Program.Version, Program.VersionType)
                    || Console.IsInputRedirected)
                {
                    return;
                }

                Ui.WriteWarning(string.Format(Resources.UpgradeAvailable, info.TagName));
                if (InteractivePrompts.ConfirmSingleKey(Resources.UpgradeOpenReleasesPrompt, defaultYes: true))
                {
                    OpenReleasesPage(info.HtmlUrl ?? ReleasesPageUrl);
                }
            }
            catch
            {
                // 尽力而为：任何失败静默跳过
            }
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
