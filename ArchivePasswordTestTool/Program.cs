using Sentry;
using SevenZip;
using Spectre.Console;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using static ArchivePasswordTestTool.Utils;
using static ArchivePasswordTestTool.Utils.Util;

namespace ArchivePasswordTestTool
{
    public class Program
    {
        public static readonly string AppName = "ArchivePasswordTestTool";
        public static readonly int[] Version = new int[] { 1, 5, 9 };
        public static readonly string VersionType = "Release";
        public static readonly string AppHomePage = "https://www.bilibili.com/read/cv6101558";
        public static readonly string Developer = "dawn-lc";
        public static ConfigType? Config { get; set; }
        public class Lib
        {
            public string? Name { get; set; }
            public string? Hash { get; set; }
            public string? DownloadUrl { get; set; }
            public bool Exists { get; set; }
        }

        public class ConfigType
        {
            public DateTime CheckUpgrade { get; set; } = new();
            [JsonIgnore]
            public bool IsLatestVersion { get; set; } = false;
            public List<Lib> Libs { get; set; } = new();
            public string Dictionary { get; set; } = "PasswordDictionary.txt";
        }

        private static async Task Initialization(StatusContext ctx)
        {
            ctx.Status("加载配置文件...");
            Config = await DeserializeJSONFileAsync<ConfigType>("config.json");
            Log($"上次检查更新:{Config.CheckUpgrade.ToLocalTime()}");
            ctx.Status("检查版本信息...");
            if (Config.CheckUpgrade < (DateTime.Now - new TimeSpan(1, 0, 0, 0)))
            {
                ctx.Status("正在获取最新版本信息...");
                try
                {
                    HttpResponseMessage Info = await HTTP.GetAsync(new Uri($"https://api.github.com/repos/{Developer}/{AppName}/releases/latest"));
                    if (Info.StatusCode == HttpStatusCode.OK)
                    {
                        Upgrade.ReleasesInfo LatestInfo = JsonSerializer.Deserialize<Upgrade.ReleasesInfo>(await Info.Content.ReadAsStringAsync()) ?? throw new ArgumentNullException("LatestInfo",$"无法解析的回应。{await Info.Content.ReadAsStringAsync()}");
                        if (Upgrade.CheckUpgrade(LatestInfo, Version, VersionType))
                        {
                            Config.Libs.Clear();
                            foreach (var item in (LatestInfo.Body ?? "").Split("\r\n"))
                            {
                                if (!string.IsNullOrEmpty(item) && item[0..4] == "lib." && LatestInfo.Assets!.Any(i => i.Name == item.Split(":")[0]))
                                {
                                    LatestInfo.Assets!.Find(i => i.Name == item.Split(":")[0])!.Label = item.Split(":")[1];
                                }
                            }
                            foreach (var item in LatestInfo.Assets!)
                            {
                                if (item.Name!.Contains("lib."))
                                {
                                    Config.Libs.Add(new() { Name = item.Name.Replace("lib.", ""), DownloadUrl = item.DownloadUrl, Hash = item.Label });
                                }
                            }
                            Config.CheckUpgrade = DateTime.Now;
                            Config.IsLatestVersion = true;
                            ctx.Status("正在保存新版本配置文件...");
                            File.WriteAllText($"config.json", JsonSerializer.Serialize(Config));
                        }
                    }
                    else
                    {
                        throw new HttpRequestException($"状态码 {Info.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    if (Config.CheckUpgrade < (DateTime.Now - new TimeSpan(30, 0, 0, 0)))
                    {
                        Error("检查更新失败！请检查您的网络情况。");
                        throw new Exception($"检查更新失败！\r\n {ex}");
                    }
                }
            }
            else
            {
                Config.IsLatestVersion = true;
            }
            ctx.Status("正在检查运行环境...");
            if (!Directory.Exists("lib"))
            {
                Directory.CreateDirectory("lib");
            }
            foreach (var item in Config.Libs)
            {
                if (File.Exists($"lib/{item.Name}"))
                {
                    using var file = File.OpenRead($"lib/{item.Name}");
                    if (ComparisonFileHash(file, Convert.FromBase64String(item.Hash!)))
                    {
                        Log($"{item.Name} 校验成功 ");
                        item.Exists = true;
                        continue;
                    }
                }
                Warn($"{item.Name} 运行库缺少或损坏! ");
                item.Exists = false;
                Config.CheckUpgrade = new();
            }
            ctx.Status("正在保存配置文件...");
            File.WriteAllText($"config.json", JsonSerializer.Serialize(Config));
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
                    await AnsiConsole.Status().StartAsync("初始化...", async ctx =>
                    {
                        await Initialization(ctx);
                    });
                    SentrySdk.AddBreadcrumb(
                        message: $"{JsonSerializer.Serialize(Config)}",
                        category: "Info",
                        level: BreadcrumbLevel.Info
                    );
                    if (!Config!.IsLatestVersion)
                    {
                        Warn("当前版本不是最新的，请前往下载最新版本。");
                        if (AnsiConsole.Confirm("是否打开软件发布页?", true))
                        {
                            Process.Start(new ProcessStartInfo(AppHomePage) { UseShellExecute = true });
                        }
                        Environment.Exit(0);
                    }
                    if (Config!.Libs.Any(i => !i.Exists))
                    {
                        Warn("存在文件缺少或损坏，开始下载修复...");
                        await AnsiConsole.Progress().AutoClear(true).HideCompleted(true).Columns(new ProgressColumn[] {
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn(),
                            new PercentageColumn(),
                            new RemainingTimeColumn()
                        }).StartAsync(async ctx =>
                        {
                            await Parallel.ForEachAsync(Config.Libs.Where(l => !l.Exists), async (item, f) =>
                            {
                                Log($"{item.Name} 开始下载");
                                await HTTP.DownloadAsync(await HTTP.GetStreamAsync(new Uri(item.DownloadUrl!)), $"lib/{item.Name}", ctx.AddTask($"下载 {item.Name}"), item.Name);
                            });
                        });
                        if (AnsiConsole.Confirm("下载完成，请重启软件以完成更新或修复。", true))
                        {
                            Process.Start(Environment.ProcessPath!);
                        }
                        Environment.Exit(0);
                    }
                    AnsiConsole.Clear();
                    AnsiConsole.Write(Figgle.FiggleFonts.Standard.Render(AppName));

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
                            Config.Dictionary = AnsiConsole.Prompt(new TextPrompt<string>("请输入密码字典路径[[或将密码字典拖至本窗口后，按回车键确认]]:")
                            .PromptStyle("dodgerblue1")
                            .ValidationErrorMessage("[red]这甚至不是一个字符串! 你是怎么做到的?[/]")
                            .Validate(path =>
                            {
                                return File.Exists(path.Replace("\"", "")) ? ValidationResult.Success() : ValidationResult.Error("[red]没有找到文件，请重新输入[/]");
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
                            ArchiveFile = AnsiConsole.Prompt(new TextPrompt<string>("请输入压缩包路径[[或将压缩包拖至本窗口后，按回车键确认]]:")
                            .PromptStyle("dodgerblue1")
                            .ValidationErrorMessage("[red]这甚至不是一个字符串! 你是怎么做到的?[/]")
                            .Validate(path =>
                            {
                                return File.Exists(path.Replace("\"", "")) ? ValidationResult.Success() : ValidationResult.Error("[red]没有找到文件，请重新输入[/]");
                            })).Replace("\"", "");
                        }
                    }


                    SevenZipBase.SetLibraryPath("lib/7z.dll");
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
                            Parallel.For(0,Dictionary.Count, (i, loopState) =>
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
                        if (Environment.OSVersion.Platform.ToString().ToLowerInvariant().Contains("win"))
                        {
                            Process.Start("explorer.exe", $"/select, \"{ArchiveFile}[测试报告].txt\"");
                        }
                    }
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
                    Error($"{ex.ToString().EscapeMarkup()}\r\n[red]未被处理的错误。[/]\r\n错误日志已提交，请等待开发者修复。(程序将在10秒后退出)");
                    await Task.Delay(10000);
                    throw;
                }
            }
        }
    }
}