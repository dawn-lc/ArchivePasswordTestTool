using Sentry;
using SevenZip;
using Spectre.Console;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using static ArchivePasswordTestTool.Utils;
using static ArchivePasswordTestTool.Utils.Util;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ArchivePasswordTestTool
{
    public class Program
    {
        public static readonly string AppName = "ArchivePasswordTestTool";
        public static readonly int[] Version = new int[] { 1, 6, 0 };
        public static readonly string VersionType = "Fixpush";
        public static readonly string AppHomePage = "https://www.bilibili.com/read/cv6101558";
        public static readonly string Developer = "dawn-lc";
        public static ConfigType? Config { get; set; }
        public class UpgradeData
        {
            public string? Directory { get; set; }
            public string? Name { get; set; }
            public string? Hash { get; set; }
            public string? DownloadUrl { get; set; }
            public bool Exists { get; set; }
        }
        public class ConfigType
        {
            public DateTime LastCheckUpgrade { get; set; } = new();
            [JsonIgnore]
            public bool NeedUpdate { get; set; } = false;
            public List<UpgradeData> Upgrade { get; set; } = new();
            public string Dictionary { get; set; } = "PasswordDictionary.txt";

        }

        public class LanguageData
        {
            private string? downloading;
            public string Downloading {
                get { return downloading ?? "正在下载"; }
                set
                {
                    downloading = string.IsNullOrEmpty(value) ? "正在下载" : value;
                }
            }
            private string? loadingConfig;
            public string LoadingConfig
            {
                get
                {
                    return loadingConfig ?? "正在加载配置";
                }
                set
                {
                    loadingConfig = string.IsNullOrEmpty(value) ? "正在加载配置" : value;
                }
            }
            private string? checkingUpgradeInfo;
            public string CheckingUpgradeInfo
            {
                get
                {
                    return checkingUpgradeInfo ?? "正在检查版本信息";
                }
                set
                {
                    checkingUpgradeInfo = string.IsNullOrEmpty(value) ? "正在检查版本信息" : value;
                }
            }
            private string? gettingLatestVersionInfo;
            public string GettingLatestVersionInfo
            {
                get
                {
                    return gettingLatestVersionInfo ?? "正在获取新版本信息";
                }
                set
                {
                    gettingLatestVersionInfo = string.IsNullOrEmpty(value) ? "正在获取新版本信息" : value;
                }
            }
            private string? savingConfig;
            public string SavingConfig
            {
                get
                {
                    return savingConfig ?? "正在保存配置";
                }
                set
                {
                    savingConfig = string.IsNullOrEmpty(value) ? "正在保存配置" : value;
                }
            }
            private string? checkingEnvironment;
            public string CheckingEnvironment
            {
                get
                {
                    return checkingEnvironment ?? "正在检查运行环境 ";
                }
                set
                {
                    checkingEnvironment = string.IsNullOrEmpty(value) ? "正在检查运行环境 " : value;
                }
            }
            private string? lastCheckUpgradeTime;
            public string LastCheckUpgradeTime
            {
                get
                {
                    return lastCheckUpgradeTime ?? "上次检查更新";
                }
                set
                {
                    lastCheckUpgradeTime = string.IsNullOrEmpty(value) ? "上次检查更新" : value;
                }
            }
            private string? checkUpdateFailed;
            public string CheckUpdateFailed
            {
                get
                {
                    return checkUpdateFailed ?? "检查更新失败";
                }
                set
                {
                    checkUpdateFailed = string.IsNullOrEmpty(value) ? "检查更新失败" : value;
                }
            }
            private string? checkNetworkStatus;
            public string CheckNetworkStatus
            {
                get
                {
                    return checkNetworkStatus ?? "检查您的网络情况";
                }
                set
                {
                    checkNetworkStatus = string.IsNullOrEmpty(value) ? "检查您的网络情况" : value;
                }
            }
            private string? downloadComplete;
            public string DownloadComplete
            {
                get
                {
                    return downloadComplete ?? "下载完成";
                }
                set
                {
                    downloadComplete = string.IsNullOrEmpty(value) ? "下载完成" : value;
                }
            }
            private string? downloadFailed;
            public string DownloadFailed
            {
                get
                {
                    return downloadFailed ?? "下载失败";
                }
                set
                {
                    downloadFailed = string.IsNullOrEmpty(value) ? "下载失败" : value;
                }
            }
            private string? consistency;
            public string Consistency
            {
                get
                {
                    return consistency ?? "校验成功";
                }
                set
                {
                    consistency = string.IsNullOrEmpty(value) ? "校验成功" : value;
                }
            }
            private string? inconsistency;
            public string Inconsistency
            {
                get
                {
                    return inconsistency ?? "未通过校验";
                }
                set
                {
                    inconsistency = string.IsNullOrEmpty(value) ? "未通过校验" : value;
                }
            }
            private string? mutex;
            public string Mutex
            {
                get
                {
                    return mutex ?? "当前目录中存在正在运行的本程序, 如需多开请将本程序复制至其他文件夹后运行";
                }
                set
                {
                    mutex = string.IsNullOrEmpty(value) ? "当前目录中存在正在运行的本程序, 如需多开请将本程序复制至其他文件夹后运行" : value;
                }
            }
            private string? nonLatestVersion;
            public string NonLatestVersion
            {
                get
                {
                    return nonLatestVersion ?? "当前版本不是最新的, 请前往下载最新版本";
                }
                set
                {
                    nonLatestVersion = string.IsNullOrEmpty(value) ? "当前版本不是最新的, 请前往下载最新版本" : value;
                }
            }

            private string? askDictionaryPath;
            public string AskDictionaryPath
            {
                get { return askDictionaryPath ?? "[yellow]将“密码本”拖至本窗口后，按回车键确认！[/]\r\n密码本位置:"; }
                set
                {
                    askDictionaryPath = string.IsNullOrEmpty(value) ? "[yellow]将“密码本”拖至本窗口后，按回车键确认！[/]\r\n密码本位置:" : value;
                }
            }
            private string? askDictionaryPathError;
            public string AskDictionaryPathError
            {
                get { return askDictionaryPathError ?? "[red]没有找到密码本，请重试！[/]"; }
                set
                {
                    askDictionaryPathError = string.IsNullOrEmpty(value) ? "[red]没有找到密码本，请重试！[/]" : value;
                }
            }
            private string? askArchivePath;
            public string AskArchivePath
            {
                get { return askArchivePath ?? "[yellow]将“压缩包”拖至本窗口后，按回车键确认！[/]\r\n压缩包位置:"; }
                set
                {
                    askArchivePath = string.IsNullOrEmpty(value) ? "[yellow]将“压缩包”拖至本窗口后，按回车键确认！[/]\r\n压缩包位置:" : value;
                }
            }
            private string? askArchivePathError;
            public string AskArchivePathError
            {
                get { return askArchivePathError ?? "[red]没有找到压缩包，请重试！[/]"; }
                set
                {
                    askArchivePathError = string.IsNullOrEmpty(value) ? "[red]没有找到压缩包，请重试！[/]" : value;
                }
            }
            private string? notEncryptedArchive;
            public string NotEncryptedArchive
            {
                get { return notEncryptedArchive ?? "并不是一个加密压缩包"; }
                set
                {
                    notEncryptedArchive = string.IsNullOrEmpty(value) ? "并不是一个加密压缩包" : value;
                }
            }
            private string? fullEncryptedArchive;
            public string FullEncryptedArchive
            {
                get { return fullEncryptedArchive ?? "无法读取加密压缩包内部结构数据，无法使用快速测试。"; }
                set
                {
                    fullEncryptedArchive = string.IsNullOrEmpty(value) ? "无法读取加密压缩包内部结构数据，无法使用快速测试。" : value;
                }
            }
            private string? testProgress;
            public string TestProgress
            {
                get { return testProgress ?? "测试进度"; }
                set
                {
                    testProgress = string.IsNullOrEmpty(value) ? "测试进度" : value;
                }
            }
            private string? correctPasswordNotFound;
            public string CorrectPasswordNotFound
            {
                get { return correctPasswordNotFound ?? "没有找到正确的解压密码"; }
                set
                {
                    correctPasswordNotFound = string.IsNullOrEmpty(value) ? "没有找到正确的解压密码" : value;
                }
            }
            private string? correctPasswordFound;
            public string CorrectPasswordFound
            {
                get { return correctPasswordFound ?? "已找到解压密码"; }
                set
                {
                    correctPasswordFound = string.IsNullOrEmpty(value) ? "已找到解压密码" : value;
                }
            }
            private string? save;
            public string Save
            {
                get { return save ?? "保存"; }
                set
                {
                    save = string.IsNullOrEmpty(value) ? "保存" : value;
                }
            }
            private string? testResult;
            public string TestResult
            {
                get { return testResult ?? "测试报告"; }
                set
                {
                    testResult = string.IsNullOrEmpty(value) ? "测试报告" : value;
                }
            }
            private string? totalTime;
            public string TotalTime
            {
                get { return totalTime ?? "总时长"; }
                set
                {
                    totalTime = string.IsNullOrEmpty(value) ? "总时长" : value;
                }
            }
            private string? archive;
            public string Archive
            {
                get { return archive ?? "压缩包"; }
                set
                {
                    archive = string.IsNullOrEmpty(value) ? "压缩包" : value;
                }
            }

            private string? initialize;
            public string Initialize
            {
                get
                {
                    return initialize ?? "初始化";
                }
                set
                {
                    initialize = string.IsNullOrEmpty(value) ? "初始化" : value;
                }
            }
            private string? press;
            public string Press
            {
                get
                {
                    return press ?? "按";
                }
                set
                {
                    press = string.IsNullOrEmpty(value) ? "按" : value;
                }
            }
            private string? open;
            public string Open
            {
                get
                {
                    return open ?? "打开";
                }
                set
                {
                    open = string.IsNullOrEmpty(value) ? "打开" : value;
                }
            }
            private string? publishPage;
            public string PublishPage
            {
                get
                {
                    return publishPage ?? "软件发布地址";
                }
                set
                {
                    publishPage = string.IsNullOrEmpty(value) ? "软件发布地址" : value;
                }
            }
            private string? anykey;
            public string Anykey
            {
                get
                {
                    return anykey ?? "任意键";
                }
                set
                {
                    anykey = string.IsNullOrEmpty(value) ? "任意键" : value;
                }
            }
            private string? quit;
            public string Quit
            {
                get
                {
                    return quit ?? "退出";
                }
                set
                {
                    quit = string.IsNullOrEmpty(value) ? "退出" : value;
                }
            }
            private string? whether;
            public string Whether
            {
                get
                {
                    return whether ?? "是否";
                }
                set
                {
                    whether = string.IsNullOrEmpty(value) ? "是否" : value;
                }
            }
            private string? existence;
            public string Existence
            {
                get
                {
                    return existence ?? "存在";
                }
                set
                {
                    existence = string.IsNullOrEmpty(value) ? "存在" : value;
                }
            }
            private string? file;
            public string File
            {
                get
                {
                    return file ?? "文件";
                }
                set
                {
                    file = string.IsNullOrEmpty(value) ? "文件" : value;
                }
            }
            private string? directory;
            public string Directory
            {
                get
                {
                    return directory ?? "目录";
                }
                set
                {
                    directory = string.IsNullOrEmpty(value) ? "目录" : value;
                }
            }
            private string? fix;
            public string Fix
            {
                get
                {
                    return fix ?? "修复";
                }
                set
                {
                    fix = string.IsNullOrEmpty(value) ? "修复" : value;
                }
            }
            private string? please;
            public string Please
            {
                get
                {
                    return please ?? "请";
                }
                set
                {
                    please = string.IsNullOrEmpty(value) ? "请" : value;
                }
            }
            private string? restart;
            public string Restart
            {
                get
                {
                    return restart ?? "重启";
                }
                set
                {
                    restart = string.IsNullOrEmpty(value) ? "重启" : value;
                }
            }
            private string? program;
            public string Program
            {
                get
                {
                    return program ?? "程序";
                }
                set
                {
                    program = string.IsNullOrEmpty(value) ? "程序" : value;
                }
            }
            private string? finish;
            public string Finish
            {
                get
                {
                    return finish ?? "完成";
                }
                set
                {
                    finish = string.IsNullOrEmpty(value) ? "完成" : value;
                }
            }
            private string? update;
            public string Update
            {
                get
                {
                    return update ?? "更新";
                }
                set
                {
                    update = string.IsNullOrEmpty(value) ? "更新" : value;
                }
            }
            private string? status;
            public string Status
            {
                get
                {
                    return status ?? "状态";
                }
                set
                {
                    status = string.IsNullOrEmpty(value) ? "状态" : value;
                }
            }
            private string? dictionary;
            public string Dictionary
            {
                get
                {
                    return dictionary ?? "字典";
                }
                set
                {
                    dictionary = string.IsNullOrEmpty(value) ? "字典" : value;
                }
            }
            private string? password;
            public string Password
            {
                get
                {
                    return password ?? "密码";
                }
                set
                {
                    password = string.IsNullOrEmpty(value) ? "密码" : value;
                }
            }
            private string? to;
            public string To
            {
                get
                {
                    return to ?? "以";
                }
                set
                {
                    to = string.IsNullOrEmpty(value) ? "以" : value;
                }
            }
            private string? or;
            public string Or
            {
                get
                {
                    return or ?? "或";
                }
                set
                {
                    or = string.IsNullOrEmpty(value) ? "或" : value;
                }
            }
            private string? period;
            /// <summary>
            /// 句号
            /// </summary>
            public string Period
            {
                get
                {
                    return period ?? "。";
                }
                set
                {
                    period = string.IsNullOrEmpty(value) ? "。" : value;
                }
            }
            private string? exclamation;
            /// <summary>
            /// 感叹号
            /// </summary>
            public string Exclamation
            {
                get
                {
                    return exclamation ?? "！";
                }
                set
                {
                    exclamation = string.IsNullOrEmpty(value) ? "！" : value;
                }
            }
            private string? comma;
            /// <summary>
            /// 逗号
            /// </summary>
            public string Comma
            {
                get
                {
                    return comma ?? "，";
                }
                set
                {
                    comma = string.IsNullOrEmpty(value) ? "，" : value;
                }
            }
            private string? ellipsis;
            /// <summary>
            /// 省略号
            /// </summary>
            public string Ellipsis
            {
                get
                {
                    return ellipsis ?? "...";
                }
                set
                {
                    ellipsis = string.IsNullOrEmpty(value) ? "..." : value;
                }
            }
            private string? colon;
            /// <summary>
            /// 冒号
            /// </summary>
            public string Colon
            {
                get
                {
                    return colon ?? "：";
                }
                set
                {
                    colon = string.IsNullOrEmpty(value) ? "：" : value;
                }
            }
            private string? questionMark;
            /// <summary>
            /// 问号
            /// </summary>
            public string QuestionMark
            {
                get
                {
                    return questionMark ?? "？";
                }
                set
                {
                    questionMark = string.IsNullOrEmpty(value) ? "？" : value;
                }
            }
            
        }

        private static async Task Initialization(StatusContext ctx)
        {
            ctx.Status("加载配置文件...");
            Config = await DeserializeJSONFileAsync<ConfigType>("config.json");
            Log($"上次检查更新:{Config.LastCheckUpgrade.ToLocalTime()}");
            ctx.Status("检查版本信息...");
            if (Config.LastCheckUpgrade < (DateTime.Now - new TimeSpan(1, 0, 0, 0)))
            if (!Directory.Exists("lang"))
            {
                Directory.CreateDirectory("lang");
            }
            if (File.Exists($"lang/{CultureInfo.CurrentCulture.Name}.json"))
            {
                Language = await DeserializeJSONFileAsync<LanguageData>($"lang/{CultureInfo.CurrentCulture.Name}.json");
            }
            else
            {
                File.WriteAllText($"lang/{CultureInfo.CurrentCulture.Name}.json", JsonSerializer.Serialize(Language));
            }
            ctx.Status($"{Language.LoadingConfig}{Language.Ellipsis}");
            Config = await DeserializeJSONFileAsync<ConfigData>("config.json");
            Log($"{Language.LastCheckUpgradeTime}{Language.Colon}{Config.CheckUpgrade.ToLocalTime()}");
            ctx.Status($"{Language.CheckingUpgradeInfo}{Language.Ellipsis}");
            if (Config.CheckUpgrade < (DateTime.Now - new TimeSpan(1, 0, 0, 0)))
            {
                ctx.Status($"{Language.GettingLatestVersionInfo}{Language.Ellipsis}");
                try
                {
                    HttpResponseMessage Info = await HTTP.GetAsync(new Uri($"https://api.github.com/repos/{Developer}/{AppName}/releases/latest"));
                    if (Info.StatusCode == HttpStatusCode.OK ? true : throw new HttpRequestException($"获取更新信息失败, 状态码 {Info.StatusCode}"))
                    {
                        Upgrade.ReleasesInfo LatestInfo = JsonSerializer.Deserialize<Upgrade.ReleasesInfo>(await Info.Content.ReadAsStringAsync()) ?? throw new ArgumentNullException(nameof(LatestInfo), $"无法解析的回应。{await Info.Content.ReadAsStringAsync()}");
                        if (Upgrade.CheckUpgrade(LatestInfo, Version, VersionType))
                        {
                            Config.NeedUpdate = true;
                            if (LatestInfo.Assets!.Any(a => a.Name == "Upgrade"))
                            {
                                Config.Upgrade.Clear();
                                HttpResponseMessage UpgradeInfo = await HTTP.GetAsync(new Uri(LatestInfo.Assets!.Find(a => a.Name == "Upgrade")!.DownloadUrl!));
                                if (UpgradeInfo.StatusCode == HttpStatusCode.OK ? true : throw new HttpRequestException($"获取更新所需文件信息失败, 状态码 {UpgradeInfo.StatusCode}"))
                                {
                                    Config.Upgrade = JsonSerializer.Deserialize<List<UpgradeData>>(await UpgradeInfo.Content.ReadAsStringAsync()) ?? throw new ArgumentNullException(nameof(UpgradeInfo), $"无法解析的回应。{await UpgradeInfo.Content.ReadAsStringAsync()}");
                                    foreach (var item in Config.Upgrade)
                                    {
                                        item.DownloadUrl = LatestInfo.Assets.Find(i => i.Name == item.Name)?.DownloadUrl;
                                    }
                                }
                            }
                            ctx.Status("正在保存配置文件...");
                            File.WriteAllText($"config.json", JsonSerializer.Serialize(Config));
                        }
                        else
                        {
                            Config.LastCheckUpgrade = DateTime.Now;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Config.LastCheckUpgrade < (DateTime.Now - new TimeSpan(30, 0, 0, 0)))
                    {
                        Error("检查更新失败！请检查您的网络情况。");
                        throw new Exception($"在检查更新过程中出现以下问题！\r\n {ex}");
                    }
                }
            }
            ctx.Status($"{Language.CheckingEnvironment}{Language.Ellipsis}");
            if (!Directory.Exists("lib"))
            {
                Directory.CreateDirectory("lib");
            }
            foreach (var item in Config.Upgrade)
            {
                if (Directory.Exists(item.Directory) && File.Exists($"{item.Directory}{item.Name}"))
                {
                    using var file = File.OpenRead($"{item.Directory}{item.Name}");
                    if (ComparisonFileHash(file, Convert.FromBase64String(item.Hash!)))
                    {
                        Log($"{item.Name} {Language.Consistency}");
                        item.Exists = true;
                        continue;
                    }
                }
                Warn($"{item.Name} {Language.Inconsistency}");
                item.Exists = false;
                Config.LastCheckUpgrade = new();
            }
            ctx.Status($"{Language.SavingConfig}{Language.Ellipsis}");
            File.WriteAllText($"config.json", JsonSerializer.Serialize(Config));
        }
        static async Task Main(string[] args)
        {
            if (Process.GetProcessesByName(AppName).Where(p => p.MainModule!.FileName != null && p.MainModule.FileName == Environment.ProcessPath).Count() > 1)
            {
                Warn($"{Language.Mutex}({Language.Press}{Language.Anykey}{Language.Quit}{Language.Ellipsis})");
                Console.ReadKey(true);
                Environment.Exit(0);
            }

            using (SentrySdk.Init(o =>
            {
                o.Dsn = "https://9361b53d22da420c95bdb43d1b78eb1e@o687854.ingest.sentry.io/5773141";
                o.DiagnosticLevel = SentryLevel.Debug;
                o.IsGlobalModeEnabled = true;
                o.TracesSampleRate = 1.0;
                o.Release = $"{string.Join(".", Version)}-{VersionType}";
                o.AutoSessionTracking = true;
            }))
            {
                //用户ID
                SentrySdk.ConfigureScope(s =>
                {
                    s.User = new()
                    {
                        Id = NetworkInterface.GetAllNetworkInterfaces().First(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet).GetPhysicalAddress().ToString(),
                        Username = Environment.UserName
                    };
                });

                bool IsEncryptedArchive = true;
                bool IsSupportQuickTest = false;
                long DictionaryCount = 0;
                uint FirstEncryptedFileIndex = 0;
                string? ArchiveFile = null;
                string? EncryptArchivePassword = null;
                Stopwatch sw = new();
                try
                {
                    await AnsiConsole.Status().StartAsync($"{Language.Initialize}{Language.Ellipsis}", async ctx =>
                    {
                        await Initialization(ctx);
                    });
                    SentrySdk.AddBreadcrumb(
                        message: $"{JsonSerializer.Serialize(Config)}",
                        category: "Info",
                        level: BreadcrumbLevel.Info
                    );

                    if (Config!.NeedUpdate)
                    {
                        Warn($"{Language.NonLatestVersion}");
                        if (AnsiConsole.Confirm($"{Language.Whether}{Language.Open}{Language.PublishPage}?", true))
                        {
                            Process.Start(new ProcessStartInfo(AppHomePage) { UseShellExecute = true });
                        }
                        Environment.Exit(0);
                    }
                    if (Config!.Upgrade.Any())
                    {
                        Warn($"{Language.Existence}{Language.File}{Language.Inconsistency}, {Language.Downloading}{Language.Fix}{Language.Ellipsis}");
                        await AnsiConsole.Progress().AutoClear(true).HideCompleted(true).Columns(new ProgressColumn[] {
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new RemainingTimeColumn()
                        }).StartAsync(async ctx =>
                        {
                            await Parallel.ForEachAsync(Config.Upgrade.Where(l => !l.Exists), async (item, f) =>
                            {
                                Log($"{item.Name} {Language.Downloading}");
                                await HTTP.DownloadAsync(await HTTP.GetStreamAsync(new Uri(item.DownloadUrl!)), $"lib/{item.Name}", ctx.AddTask($"{Language.Downloading} {item.Name}"), item.Name);
                            });
                        });
                        if (AnsiConsole.Confirm($"{Language.DownloadComplete}, {Language.Please}{Language.Restart}{Language.Program}{Language.To}{Language.Finish}{Language.Update}{Language.Or}{Language.Fix}{Language.Period}", true))
                        {
                            Process.Start(Environment.ProcessPath!);
                        }
                        Environment.Exit(0);
                    }
                    SevenZipBase.SetLibraryPath("lib/7z.dll");

                    AnsiConsole.Clear();
                    AnsiConsole.Write(Figgle.FiggleFonts.Standard.Render(AppName));
                    AnsiConsole.WriteLine("提示: 您可以按：“ Ctrl + C ” 强制退出本程序。");

                    if (StartupParametersCheck(args, "D") && File.Exists(GetParameter(args, "D", "PasswordDictionary.txt").Replace("\"", "")))
                    {
                        Config.Dictionary = GetParameter(args, "D", "PasswordDictionary.txt").Replace("\"", "");
                    }
                    else
                    {
                        if (Environment.OSVersion.Platform.ToString().ToLowerInvariant().Contains("win") && File.Exists("ArchivePasswordTestToolGUI.exe"))
                        {
                            Process.Start("ArchivePasswordTestToolGUI.exe");
                            Environment.Exit(0);
                        }
                        else
                        {
                            Config.Dictionary = AnsiConsole.Prompt(new TextPrompt<string>($"{Language.AskDictionaryPath}")
                            .PromptStyle("dodgerblue1")
                            .Validate(path =>
                            {
                                return File.Exists(path.Replace("\"", "")) ? ValidationResult.Success() : ValidationResult.Error($"{Language.AskDictionaryPathError}");
                            })).Replace("\"", "");
                        }
                    }

                    SentrySdk.AddBreadcrumb(
                        message: $"Dictionary {Config.Dictionary}",
                        category: "Info",
                        level: BreadcrumbLevel.Info
                    );

                    if (StartupParametersCheck(args, "F") && File.Exists(GetParameter(args, "F", "").Replace("\"", "")))
                    {
                        ArchiveFile = GetParameter(args, "F", "").Replace("\"", "");
                    }
                    else
                    {
                        if (Environment.OSVersion.Platform.ToString().ToLowerInvariant().Contains("win") && File.Exists("ArchivePasswordTestToolGUI.exe"))
                        {
                            Process.Start("ArchivePasswordTestToolGUI.exe");
                            Environment.Exit(0);
                        }
                        else
                        {
                            ArchiveFile = AnsiConsole.Prompt(new TextPrompt<string>($"{Language.AskArchivePath}")
                            .PromptStyle("dodgerblue1")
                            .Validate(path =>
                            {
                                return File.Exists(path.Replace("\"", "")) ? ValidationResult.Success() : ValidationResult.Error($"{Language.AskDictionaryPathError}");
                            })).Replace("\"", "");
                        }
                    }

                    do
                    {
                        ArchiveFile ??= AnsiConsole.Prompt(new TextPrompt<string>("[yellow]将“压缩包”拖至本窗口后，按回车键确认！[/]\r\n压缩包位置:")
                            .PromptStyle("dodgerblue1")
                            .ValidationErrorMessage("[red]这甚至不是一个字符串! 你是怎么做到的?[/]")
                            .Validate(path =>
                            {
                                return File.Exists(path.Replace("\"", "")) ? ValidationResult.Success() : ValidationResult.Error("[red]没有找到文件，请重新输入[/]");
                        })).Replace("\"", "");

                        using (SevenZipExtractor ExtractorFile = new(ArchiveFile))
                        {
                            if (ExtractorFile.Check())
                            {
                                AnsiConsole.WriteLine($"{ArchiveFile} 并不是一个加密压缩包。");
                                IsEncryptedArchive = false;
                            }
                            else
                            {
                                try
                                {
                                    if (!ExtractorFile.ArchiveFileData.Any(i => i.Encrypted))
                                    {
                                        throw new NotSupportedException("不能读取加密压缩包内部结构数据。");
                                    }
                                    FirstEncryptedFileIndex = Convert.ToUInt32(ExtractorFile.ArchiveFileData.First(i => i.Encrypted).Index);
                                    IsSupportQuickTest = true;
                                }
                                catch (Exception)
                                {
                                    AnsiConsole.WriteLine($"无法读取加密压缩包内部结构数据，无法使用快速测试。");
                                }
                            }
                        }
                        using (var file = File.OpenRead(ArchiveFile))
                        {
                            SentrySdk.AddBreadcrumb(
                               message: $"ArchiveFile {ArchiveFile}[{Convert.ToBase64String(FileHash(file))}]",
                               category: "Info",
                               level: BreadcrumbLevel.Info
                           );
                        }
                        if (IsEncryptedArchive)
                        {

                            Dictionary Dictionary = new(Config.Dictionary);
                            DictionaryCount = Dictionary.Count;

                            AnsiConsole.WriteLine($"字典内包含: {DictionaryCount} 条密码。");
                            SentrySdk.AddBreadcrumb(
                                message: $"DictionaryCount {DictionaryCount}",
                                category: "Info",
                                level: BreadcrumbLevel.Info
                            );
                            AnsiConsole.Progress().AutoClear(true).HideCompleted(true).Columns(new ProgressColumn[] {
                                new TaskDescriptionColumn(),
                                new ProgressBarColumn(),
                                new PercentageColumn(),
                                new RemainingTimeColumn()
                            }).Start(ctx =>
                            {
                                var TestProgressBar = ctx.AddTask($"测试进度");
                                sw.Restart();
                                Parallel.For(0, Dictionary.Count, (i, loopState) =>
                                {
                                    try
                                    {
                                        string Password = Dictionary.Pop();
                                        using SevenZipExtractor TestArchive = new(ArchiveFile, Password);
                                        if (IsSupportQuickTest)
                                        {
                                            if (TestArchive.Check(FirstEncryptedFileIndex))
                                            {
                                                sw.Stop();
                                                EncryptArchivePassword = Password;
                                                TestProgressBar.Increment((double)1 / DictionaryCount * 100);
                                                loopState.Break();
                                            }
                                            TestProgressBar.Increment((double)1 / DictionaryCount * 100);
                                        }
                                        else
                                        {
                                            if (TestArchive.Check())
                                            {
                                                sw.Stop();
                                                EncryptArchivePassword = Password;
                                                TestProgressBar.Increment((double)1 / DictionaryCount * 100);
                                                loopState.Break();
                                            }
                                            TestProgressBar.Increment((double)1 / DictionaryCount * 100);
                                        }
                                    }
                                    catch (Exception)
                                    {
                                    }
                                });
                                TestProgressBar.Increment(100);
                            });
                            AnsiConsole.WriteLine(EncryptArchivePassword != null ? $"已找到解压密码: {EncryptArchivePassword}" : "没有找到正确的解压密码！");
                            AnsiConsole.WriteLine($"测试耗时：{TimeSpanString(sw.Elapsed)}");
                        }
                        if (AnsiConsole.Confirm("是否保存测试结果?", true))
                        {
                            using (StreamWriter TestOut = new($"{ArchiveFile}[测试报告].txt", false))
                            {
                                TestOut.WriteLine("加密压缩包: " + ArchiveFile);
                                TestOut.WriteLine("字典: " + Config.Dictionary);
                                TestOut.WriteLine($"测试耗时：{TimeSpanString(sw.Elapsed)}");
                                TestOut.WriteLine(EncryptArchivePassword != null ? $"解压密码: {EncryptArchivePassword}" : "没有找到正确的解压密码！");
                            }

                            if (Environment.OSVersion.Platform.ToString().ToLowerInvariant().Contains("win") && AnsiConsole.Confirm("是否查看测试报告?", true))
                            {
                                Process.Start("explorer.exe", $"/select, \"{ArchiveFile}[测试报告].txt\"");
                            }
                        }
                        ArchiveFile = null;
                    }
                    while (AnsiConsole.Confirm("是否继续测试其他文件?", true));
                    
                }
                catch (Exception ex)
                {
                    SentrySdk.AddBreadcrumb(
                        message: $"DictionaryCount {DictionaryCount}",
                        category: "Info",
                        level: BreadcrumbLevel.Info
                    );
                    SentrySdk.AddBreadcrumb(
                        message: $"EncryptArchivePassword {EncryptArchivePassword ?? "NULL"}",
                        category: "Info",
                        level: BreadcrumbLevel.Info
                    );
                    SentrySdk.CaptureException(ex);
                    Error($"{ex.ToString().EscapeMarkup()}\r\n\r\n[red]遇到无法处理的错误！[/]\r\n\r\n[yellow]请检查运行位置是否为系统保护目录以及是否授予程序所需权限。\r\n如果在检查更新失败或下载运行库中出错，请检查您的网络环境。[/]\r\n\r\n错误日志已提交。(程序将在10秒后退出)");
                    await Task.Delay(10000);
                    throw;
                }
            }
        }
    }
}