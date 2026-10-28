using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace ArchivePasswordTestTool.Properties
{
    /// <summary>
    /// 从**主程序集的内嵌资源**里解析本地化字符串，取代 <see cref="ResourceManager"/>。
    /// <para>
    /// 为什么不用 <see cref="ResourceManager"/>：它只在**卫星程序集**（<c>zh-CN\App.resources.dll</c>）
    /// 里查找 culture 资源，不会去主程序集找。而 Native AOT **无法把卫星程序集打进 exe**，
    /// 于是 AOT 产物必然是「exe + 若干语言目录」——与「单文件下载」的发布形态冲突，
    /// 自更新也无法安全替换多个文件。
    /// </para>
    /// <para>
    /// 做法：把各语言的 <c>.resources</c> 用 <c>LogicalName</c> 直接编进主程序集，
    /// 资源名约定为 <c>{BaseName}.{culture}.resources</c>（中性为 <c>{BaseName}.resources</c>），
    /// 由本类按 culture 链自行查找。这样无论常规构建还是 AOT，产物都只有一个 exe。
    /// </para>
    /// </summary>
    internal static class EmbeddedResourceManager
    {
        /// <summary>中性资源名（与 Designer 里的 <c>ResourceManager("...")</c> 参数一致）</summary>
        private const string BaseName = "ArchivePasswordTestTool.Properties.Resources";

        /// <summary>本程序支持的中性语言：找不到任何文化资源时回退到它</summary>
        private static readonly CultureInfo NeutralCulture = CultureInfo.InvariantCulture;

        /// <summary>
        /// 简体中文别名归一。系统可能报 <c>zh-Hans-CN</c> / <c>zh-CHS</c> / <c>zh-Hans</c>，
        /// 而资源文件只有 <c>zh-CN</c> 一套——不归一就会静默回退成英文界面。
        /// 繁体用户也落到简体（比看英文更可用）。
        /// </summary>
        private static readonly Dictionary<string, string> CultureAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["zh"] = "zh-CN",
            ["zh-Hans"] = "zh-CN",
            ["zh-Hans-CN"] = "zh-CN",
            ["zh-CHS"] = "zh-CN",
            ["zh-SG"] = "zh-CN",
            ["zh-Hant"] = "zh-CN",
            ["zh-Hant-TW"] = "zh-CN",
            ["zh-Hant-HK"] = "zh-CN",
            ["zh-TW"] = "zh-CN",
            ["zh-HK"] = "zh-CN",
            ["zh-CHT"] = "zh-CN",
        };

        /// <summary>已加载的资源集缓存（键为 culture 名，中性为空串）。线程安全</summary>
        private static readonly ConcurrentDictionary<string, ResourceSet?> Cache = new(StringComparer.Ordinal);

        private static readonly Assembly OwnerAssembly = typeof(EmbeddedResourceManager).Assembly;

        /// <summary>
        /// 读取本地化字符串。按 culture → Parent → … → 中性 的顺序查找，
        /// 全部缺失时返回 null（调用方会看到 null，便于测试发现漏键）。
        /// <para>
        /// <paramref name="culture"/> 为 null 时用 <see cref="CultureInfo.CurrentUICulture"/>，
        /// 与原 <c>ResourceManager.GetString(name, culture)</c> 的语义一致——
        /// 少了这一步，Designer 传 null（resourceCulture 默认值）就会永远只出英文。
        /// </para>
        /// </summary>
        internal static string? GetString(string name, CultureInfo? culture)
        {
            CultureInfo effective = culture ?? CultureInfo.CurrentUICulture;

            foreach (string candidate in EnumerateCandidates(effective))
            {
                ResourceSet? set = Cache.GetOrAdd(candidate, Load);

                if (set?.GetString(name) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// 按优先级列出要尝试的 culture 名：先原名，再别名，然后逐级 Parent，最后中性。
        /// 不用 <c>CultureInfo.Parent</c> 直接遍历是因为 InvariantCulture.Parent 指向自身，
        /// 会死循环；这里的列表天然终止且去重。
        /// </summary>
        private static List<string> EnumerateCandidates(CultureInfo? culture)
        {
            var candidates = new List<string>();

            void Add(string? name)
            {
                string key = string.IsNullOrEmpty(name) ? string.Empty : name!;

                if (!candidates.Contains(key, StringComparer.Ordinal))
                {
                    candidates.Add(key);
                }
            }

            if (culture is not null && !culture.Equals(NeutralCulture))
            {
                Add(culture.Name);

                if (CultureAliases.TryGetValue(culture.Name, out var alias))
                {
                    Add(alias);
                }

                // zh-Hans-CN -> zh-Hans -> zh：逐级归一，再走别名表
                for (CultureInfo? parent = culture.Parent;
                     parent is not null && !parent.Equals(NeutralCulture);
                     parent = parent.Parent)
                {
                    Add(parent.Name);

                    if (CultureAliases.TryGetValue(parent.Name, out var parentAlias))
                    {
                        Add(parentAlias);
                    }
                }
            }

            Add(string.Empty);   // 中性兜底
            return candidates;
        }

        private static ResourceSet? Load(string cultureName)
        {
            string resourceName = cultureName.Length == 0
                ? $"{BaseName}.resources"
                : $"{BaseName}.{cultureName}.resources";

            using Stream? stream = OwnerAssembly.GetManifestResourceStream(resourceName);
            return stream is null ? null : new ResourceSet(stream);
        }
    }
}
