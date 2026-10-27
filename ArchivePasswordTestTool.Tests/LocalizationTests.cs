using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArchivePasswordTestTool.Properties;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 多语言资源一致性：5 套 resx（en 中性 + zh-CN/ja/fr/ru 卫星）必须键集一致、
/// 占位符一致，且能被 ResourceManager 正确解析出各自语言。
/// 任何缺失都会在运行时静默回退到英文——用户看到「半个中文界面」，
/// 而程序不报任何错，这类问题只能靠测试发现。
/// </summary>
public sealed class LocalizationTests
{
    /// <summary>
    /// 受支持语言，对应 Properties/Resources.*.resx 的卫星程序集。
    /// 新增语言时需同时补 resx 与这里。
    /// </summary>
    private static readonly string[] SupportedCultures = ["en", "zh-CN", "ja", "fr", "ru"];

    private static readonly string PropertiesDir =
        Path.Combine(TestPaths.RepoRoot, "ArchivePasswordTestTool", "Properties");

    private static string ResxPath(string culture) =>
        culture == "en"
            ? Path.Combine(PropertiesDir, "Resources.resx")
            : Path.Combine(PropertiesDir, $"Resources.{culture}.resx");

    /// <summary>键 → 值（XML 解析而非正则：资源文本里可能含尖括号与转义实体）</summary>
    private static Dictionary<string, string> LoadResx(string culture)
    {
        var document = XDocument.Load(ResxPath(culture));

        return document.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("ja")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void SatelliteResxHasSameKeysAsNeutral(string culture)
    {
        var neutral = LoadResx("en");
        var satellite = LoadResx(culture);

        var missing = neutral.Keys.Except(satellite.Keys).OrderBy(key => key).ToArray();
        var extra = satellite.Keys.Except(neutral.Keys).OrderBy(key => key).ToArray();

        Assert.True(missing.Length == 0, $"{culture} 缺少键：{string.Join(", ", missing)}");
        Assert.True(extra.Length == 0, $"{culture} 多出键：{string.Join(", ", extra)}");
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("ja")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void SatelliteResxPreservesFormatPlaceholders(string culture)
    {
        var neutral = LoadResx("en");
        var satellite = LoadResx(culture);
        var mismatches = new List<string>();

        foreach (var (key, englishValue) in neutral)
        {
            if (!satellite.TryGetValue(key, out var translated))
            {
                continue; // 缺键由另一个用例负责报告
            }

            // 占位符缺失会让 string.Format 丢掉参数（甚至抛 FormatException），
            // 这类错误在只有英文环境时完全暴露不出来
            var expected = Placeholders(englishValue);
            var actual = Placeholders(translated);

            if (!expected.SequenceEqual(actual))
            {
                mismatches.Add($"{key}: 英文 [{string.Join(",", expected)}] vs {culture} [{string.Join(",", actual)}]");
            }
        }

        Assert.True(mismatches.Count == 0, "占位符不一致：\n  " + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void NoValueIsEmpty()
    {
        var offenders = new List<string>();

        foreach (var culture in SupportedCultures)
        {
            foreach (var (key, value) in LoadResx(culture))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    offenders.Add($"{culture}:{key}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "以下资源为空值：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ResourceManagerResolvesEverySupportedCulture()
    {
        // 卫星程序集若未随测试输出一起部署，GetString 会静默回退到英文——
        // 用户在非中文系统上看到英文界面，而程序与日志都不报错。
        // 用一条文案在所有语言下的取值互不相同来证明卫星确实生效。
        var values = SupportedCultures
            .Select(culture => Resources.ResourceManager.GetString(
                "OnlineReportFailed", new CultureInfo(culture)))
            .ToArray();

        Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DesignerPropertiesCoverAllNeutralKeys()
    {
        // Resources.Designer.cs 是手工维护的（无 VS 设计器自动同步），
        // 新增 resx 键后若忘了加属性，代码里就无法引用，只能硬编码字符串
        var designerPath = Path.Combine(PropertiesDir, "Resources.Designer.cs");
        string designer = File.ReadAllText(designerPath);

        var properties = Regex.Matches(designer, @"public static string (\w+)\s*\r?\n")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = LoadResx("en").Keys
            .Where(key => !properties.Contains(key))
            .OrderBy(key => key)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "resx 中存在 Designer 未暴露的键（无法在代码中强类型引用）：\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// 提取 <c>{0}</c> 形式的格式占位符并排序比较。
    /// 用正则而非 Parse 字符串，避免花括号转义（{{ }}）被误判。
    /// </summary>
    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}")
            .Select(match => match.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
