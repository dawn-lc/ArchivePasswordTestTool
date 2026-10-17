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
    /// 检查间隔 24h（config.json 记录上次检查时间），失败静默跳过不阻断主流程。
    /// </summary>
    internal static class Upgrade
    {
        private const string RepoUrl = "https://api.github.com/repos/dawn-lc/ArchivePasswordTestTool/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/dawn-lc/ArchivePasswordTestTool/releases/latest";
        private const string ConfigFileName = "config.json";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

        /// <summary>
        /// 启动时检查更新（尽力而为）：距上次检查不足 24h 直接跳过；
        /// 网络/API 失败只提示不抛出。发现新版本时前往发布页询问
        /// </summary>
        public static void CheckForUpdate()
        {
            try
            {
                var config = LoadConfig();
                if (DateTime.Now - config.LastCheckUpgrade < CheckInterval)
                {
                    return;
                }

                config.LastCheckUpgrade = DateTime.Now;
                SaveConfig(config);

                var info = FetchLatestRelease();
                if (info?.TagName is null)
                {
                    return;
                }

                if (!IsNewerVersion(info.TagName, Program.Version, Program.VersionType)
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
                // 尽力而为：任何失败（网络/解析/IO）静默跳过
            }
        }

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
                if (File.Exists(ConfigFileName))
                {
                    var config = JsonSerializer.Deserialize(File.ReadAllText(ConfigFileName), UpgradeJsonContext.Default.UpgradeConfig);
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
                File.WriteAllText(ConfigFileName, JsonSerializer.Serialize(config, UpgradeJsonContext.Default.UpgradeConfig));
            }
            catch
            {
                // 只读目录等场景静默（每次启动都会重查一次，可接受）
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
