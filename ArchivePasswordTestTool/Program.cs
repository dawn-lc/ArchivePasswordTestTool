using Sentry;
using SevenZip;
using Spectre.Console;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using static ArchivePasswordTestTool.Utils;
using static ArchivePasswordTestTool.Utils.Util;

namespace ArchivePasswordTestTool
{
    public class Program
    {
        public static readonly string AppName = "ArchivePasswordTestTool";
        public static readonly int[] Version = new int[] { 1, 5, 7 };
        public static readonly string VersionType = "Release";
        public static readonly string AppHomePage = "https://www.bilibili.com/read/cv6101558";
        public static readonly string Developer = "dawn-lc";
        public static Config config { get; set; }
        public class Lib
        {
            public string Name { get; set; }
            public string Hash { get; set; }
            public string DownloadUrl { get; set; }
            public bool Exists { get; set; }
        }

        public class Config
        {
            public DateTime CheckUpgrade { get; set; } = new();
            public bool IsLatestVersion { get; set; } = false;
            public List<Lib> Libs { get; set; } = new();
            public string Dictionary { get; set; } = "PasswordDictionary.txt";
        }

        private static async Task Initialization(StatusContext ctx)
        {
            ctx.Status("加载配置文件...");
            config = await DeserializeJSONFileAsync<Config>("config.json");
            Log($"上次检查更新:{config.CheckUpgrade.ToLocalTime()}");
            ctx.Status("检查版本信息...");
            if (config.CheckUpgrade < (DateTime.Now - new TimeSpan(1, 0, 0, 0)))
            {
                ctx.Status("正在获取最新版本信息...");
                try
                {
                    HttpResponseMessage Info = await HTTP.GetAsync(new Uri($"https://api.github.com/repos/{Developer}/{AppName}/releases/latest"));
                    if (Info.StatusCode == HttpStatusCode.OK)
                    {
                        Upgrade.ReleasesInfo? LatestInfo = JsonSerializer.Deserialize<Upgrade.ReleasesInfo>(await Info.Content.ReadAsStringAsync());
                        if (LatestInfo != null)
                        {
                            config.Libs.Clear();
                            if (Upgrade.CheckUpgrade(LatestInfo, Version, VersionType))
                            {
                                foreach (var item in (LatestInfo.Body ?? "").Split("\r\n"))
                                {
                                    if (!string.IsNullOrEmpty(item) && item[0..4] == "lib." && LatestInfo.Assets.Any(i => i.Name == item.Split(":")[0]))
                                    {
                                        LatestInfo.Assets.Find(i => i.Name == item.Split(":")[0])!.Label = item.Split(":")[1];
                                    }
                                }
                                foreach (var item in LatestInfo.Assets)
                                {
                                    if (item.Name.Contains("lib."))
                                    {
                                        config.Libs.Add(new() { Name = item.Name.Replace("lib.", ""), DownloadUrl = item.DownloadUrl, Hash = item.Label });
                                    }
                                }
                                config.CheckUpgrade = DateTime.Now;
                                config.IsLatestVersion = true;
                                ctx.Status("正在保存新版本配置文件...");
                                File.WriteAllText($"config.json", JsonSerializer.Serialize(config));
                            }
                        }
                    }
                    else
                    {
                        if (config.CheckUpgrade < (DateTime.Now - new TimeSpan(30, 0, 0, 0)))
                        {
                            Error("检查更新失败！请检查您的网络情况。");
                            throw new Exception($"检查更新失败！\r\n错误码 { Info.StatusCode }");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (config.CheckUpgrade < (DateTime.Now - new TimeSpan(30, 0, 0, 0)))
                    {
                        Error("检查更新失败！请检查您的网络情况。");
                        throw new Exception($"检查更新失败！\r\n {ex}");
                    }
                }
            }
            ctx.Status("正在检查运行环境...");
            if (!Directory.Exists("lib"))
            {
                Directory.CreateDirectory("lib");
            }
            foreach (var item in config.Libs)
            {
                if (File.Exists($"lib/{item.Name}"))
                {
                    using var file = File.OpenRead($"lib/{item.Name}");
                    if (ComparisonFileHash(file, Convert.FromBase64String(item.Hash)))
                    {
                        Log($"{item.Name} 校验成功 ");
                        item.Exists = true;
                        continue;
                    }
                }
                Warn($"{item.Name} 运行库缺少或损坏! ");
                item.Exists = false;
                config.CheckUpgrade = new();
            }
            ctx.Status("正在保存配置文件...");
            File.WriteAllText($"config.json", JsonSerializer.Serialize(config));
        }
        static async Task Main(string[] args)
        {
            if (Process.GetProcessesByName(AppName).Where(p => p.MainModule!.FileName != null && p.MainModule.FileName == Environment.ProcessPath).Count() > 1)
            {
                Warn("当前目录中存在正在运行的本程序，如需多开请将本程序复制至其他文件夹后运行。(按任意键退出...)");
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

                string? ArchiveFile = null;
                string? EncryptArchivePassword = null;
                long DictionaryCount = 0;
                try
                {
                    await AnsiConsole.Status().StartAsync("初始化...", async ctx =>
                    {
                        await Initialization(ctx);
                    });
                    if (!config.IsLatestVersion)
                    {
                        Warn("当前版本不是最新的，请前往下载最新版本。");
                        if (AnsiConsole.Confirm("是否打开软件发布页?", true))
                        {
                            Process.Start(new ProcessStartInfo(AppHomePage) { UseShellExecute = true });
                        }
                        Environment.Exit(0);
                    }
                    if (config.Libs.Any(i => !i.Exists))
                    {
                        await AnsiConsole.Progress().AutoClear(true).HideCompleted(true).Columns(new ProgressColumn[] {
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new RemainingTimeColumn()
                        }).StartAsync(async ctx =>
                        {
                            foreach (var item in config.Libs.Where(i => !i.Exists))
                            {
                                await HTTP.DownloadAsync(await HTTP.GetStreamAsync(new Uri(item.DownloadUrl)), $"lib/{item.Name}", ctx.AddTask($"下载 {item.Name}"), item.Name);
                            }
                            if (!File.Exists("PasswordDictionary.txt"))
                            {
                                Warn("没有找到默认字典 PasswordDictionary.txt 正在下载由 [link=https://github.com/baidusama]baidusama[/] 提供的 [link=https://github.com/baidusama/EroPassword]EroPassword[/]");
                                await HTTP.DownloadAsync(await HTTP.GetStreamAsync(new Uri("https://github.com/baidusama/EroPassword/raw/main/PasswordDictionary.txt")), "PasswordDictionary.txt", ctx.AddTask($"下载 PasswordDictionary.txt"), "PasswordDictionary.txt");
                            }
                        });
                        if (AnsiConsole.Confirm("下载完成，请重启软件以完成更新。", true))
                        {
                            Process.Start(Environment.ProcessPath!);
                        }
                        Environment.Exit(0);
                    }
                    AnsiConsole.Clear();
                    AnsiConsole.Write(Figgle.FiggleFonts.Standard.Render(AppName));
                    if (StartupParametersCheck(args, "D") && File.Exists(GetParameter(args, "D", "PasswordDictionary.txt").Replace("\"", "")))
                    {
                        config.Dictionary = GetParameter(args, "D", "PasswordDictionary.txt").Replace("\"", "");
                    }
                    else
                    {
                        config.Dictionary = AnsiConsole.Prompt(new TextPrompt<string>("请输入密码字典路径[[或将密码字典拖至本窗口后，按回车键确认]]:")
                        .PromptStyle("dodgerblue1")
                        .ValidationErrorMessage("[red]这甚至不是一个字符串! 你是怎么做到的?[/]")
                        .Validate(path =>
                        {
                            return File.Exists(path.Replace("\"", "")) ? ValidationResult.Success() : ValidationResult.Error("[red]没有找到文件，请重新输入[/]");
                        })).Replace("\"", "");
                    }
                    if (StartupParametersCheck(args, "F") && File.Exists(GetParameter(args, "F", "").Replace("\"", "")))
                    {
                        ArchiveFile = GetParameter(args, "F", "").Replace("\"", "");
                    }
                    else
                    {
                        ArchiveFile = AnsiConsole.Prompt(new TextPrompt<string>("请输入压缩包路径[[或将压缩包拖至本窗口后，按回车键确认]]:")
                        .PromptStyle("dodgerblue1")
                        .ValidationErrorMessage("[red]这甚至不是一个字符串! 你是怎么做到的?[/]")
                        .Validate(path =>
                        {
                            return File.Exists(path.Replace("\"", "")) ? ValidationResult.Success() : ValidationResult.Error("[red]没有找到文件，请重新输入[/]");
                        })).Replace("\"", "");
                    }


                    string[] Dictionary = File.ReadAllLines(config.Dictionary);
                    DictionaryCount = Dictionary.Length;
                    AnsiConsole.WriteLine($"字典内包含: {Dictionary.Length} 条密码。");
                    SevenZipBase.SetLibraryPath("lib/7z.dll");
                    await AnsiConsole.Progress().AutoClear(true).HideCompleted(true).Columns(new ProgressColumn[] {
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new RemainingTimeColumn()
                    }).StartAsync(async ctx => {
                        
                        var Test = ctx.AddTask($"测试进度");
                        Parallel.ForEach(Dictionary, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 }, (i, loopState) =>
                        {
                            try
                            {
                                using var temp = new SevenZipExtractor(ArchiveFile, i);
                                Test.Increment((double)1 / DictionaryCount * 100);
                                if (temp.Check())
                                {
                                    EncryptArchivePassword = i;
                                    loopState.Break();
                                }
                            }
                            catch (Exception)
                            {
                            }
                        });
                        Test.Increment(100);
                        Test.StopTask();
                    });
                    AnsiConsole.WriteLine(EncryptArchivePassword != null ? $"已找到解压密码: {EncryptArchivePassword}" : "没有找到正确的解压密码！");
                    if (AnsiConsole.Confirm("是否保存测试结果?", true))
                    {
                        using (StreamWriter file = new($"{ArchiveFile}[测试报告].txt", false))
                        {
                            file.WriteLine("加密压缩包: " + ArchiveFile);
                            file.WriteLine("字典: " + config.Dictionary);
                            if (EncryptArchivePassword != null)
                            {
                                file.WriteLine("解压密码: " + EncryptArchivePassword);
                            }
                            else
                            {
                                file.WriteLine("没有找到正确的解压密码！");
                            }
                            file.Close();
                        }
                        switch (Environment.OSVersion.Platform)
                        {
                            case PlatformID.Win32S:
                            case PlatformID.Win32Windows:
                            case PlatformID.Win32NT:
                            case PlatformID.WinCE:
                                Process.Start("explorer.exe", $"/select, \"{ArchiveFile}[测试报告].txt\"");
                                break;
                            default:
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    //ArchiveFile 可能会泄露您的个人隐私数据, 如有意见请直接注释这个函数
                    SentrySdk.AddBreadcrumb(
                        message: $"Config {JsonSerializer.Serialize(config)}\r\nArchiveFile {ArchiveFile ?? "NULL"}\r\nEncryptArchivePassword {EncryptArchivePassword ?? "NULL"}\r\nDictionaryCount {DictionaryCount}",
                        category: "Error",
                        level: BreadcrumbLevel.Error
                    );
                    SentrySdk.CaptureException(ex);
                }
            }
        }
    }
}