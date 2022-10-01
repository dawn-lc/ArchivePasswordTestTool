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
        public static readonly int[] Version = new int[] { 1, 5, 1 };
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
            public List<Lib> Libs { get; set; } = new();
            public string Dictionary { get; set; } = "PasswordDictionary.txt";
        }

        private static async Task Initialization(StatusContext ctx)
        {
            ctx.Status("加载配置文件...");
            config = await DeserializeJSONFileAsync<Config>("config.json");
            Log($"上次检查更新:{config.CheckUpgrade.ToLocalTime()}");
            if (config.CheckUpgrade < (DateTime.Now - new TimeSpan(1, 0, 0, 0)))
            {
                Log("正在从github.com获取最新版本信息...");
                try
                {
                    HttpResponseMessage Info = await HTTP.GetAsync(new Uri($"https://api.github.com/repos/{Developer}/{AppName}/releases/latest"), new Dictionary<string, IEnumerable<string>>() { ["user-agent"] = new List<string>() { AppName + " " + string.Join(".", Version) } });
                    if (Info.StatusCode != HttpStatusCode.OK)
                    {
                        Error("检查更新失败！请检查您的网络情况。");
                        throw new Exception($"检查更新失败！\r\n{ Info.StatusCode }\r\n{ await Info.Content.ReadAsStringAsync() }");
                    }
                    else
                    {
                        Upgrade.ReleasesInfo? LatestInfo = JsonSerializer.Deserialize<Upgrade.ReleasesInfo>(await Info.Content.ReadAsStringAsync());
                        if (LatestInfo != null)
                        {
                            if (Upgrade.CheckUpgrade(LatestInfo, Version, VersionType))
                            {
                                Log("当前本地程序已是最新版本。");
                                config.CheckUpgrade = DateTime.Now;
                                config.Libs.Clear();
                                foreach (var item in (LatestInfo.Body ?? "").Split("\r\n"))
                                {
                                    if (!string.IsNullOrEmpty(item) && item[0..4] == "lib." && LatestInfo.Assets.Any(i => i.Name == item.Split(":")[0]))
                                    {
                                        LatestInfo.Assets.Find(i => i.Name == item.Split(":")[0]).Label = item.Split(":")[1];
                                    }
                                }
                                foreach (var item in LatestInfo.Assets)
                                {
                                    if (item.Name.Contains("lib."))
                                    {
                                        config.Libs.Add(new() { Name = item.Name.Replace("lib.", ""), DownloadUrl = item.DownloadUrl, Hash = item.Label });
                                    }
                                }
                                File.WriteAllText($"config.json", JsonSerializer.Serialize(config));
                            }
                            else
                            {
                                Warn("版本不是最新的, 请下载最新版。");
                                Process.Start("explorer.exe", AppHomePage);
                                throw new Exception("版本过低！");
                            }
                        }
                    }
                    
                }
                catch (Exception ex)
                {
                    Error("检查更新失败！请检查您的网络情况。");
                    throw new Exception($"检查更新失败！\r\n {ex}");
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
            }
            if (!File.Exists("PasswordDictionary.txt"))
            {
                File.WriteAllText("PasswordDictionary.txt", "");
            }
        }
        static async Task Main(string[] args)
        {
            using (SentrySdk.Init(o =>
            {
                o.Dsn = "https://9361b53d22da420c95bdb43d1b78eb1e@o687854.ingest.sentry.io/5773141";
                o.TracesSampleRate = 1.0;
            }))
            {
                string ArchiveFile;
                await AnsiConsole.Status().StartAsync("初始化...", async ctx => {
                    await Initialization(ctx);
                });
                if (config.Libs.Any(i => !i.Exists))
                {
                    await AnsiConsole.Progress().AutoClear(true).HideCompleted(true).StartAsync(async ctx =>
                    {
                        foreach (var item in config.Libs.Where(i => !i.Exists))
                        {
                            HTTP.DownloadAsync(await HTTP.GetAsync(new Uri(item.DownloadUrl), new Dictionary<string, IEnumerable<string>>() { ["user-agent"] = new List<string>() { AppName + " " + string.Join(".", Version) } }), $"lib/{item.Name}", ctx.AddTask($"下载 {item.Name}"), item.Name);
                        }
                    });
                    Log("下载完成，请重启软件以完成更新。");
                    await Task.Delay(5000);
                    Environment.Exit(0);
                }
                AnsiConsole.Clear();

                while (true)
                {
                    if (StartupParametersCheck(args, "D"))
                    {
                        config.Dictionary = GetParameter(args, "D", "PasswordDictionary.txt");
                    }
                    else
                    {
                        config.Dictionary = AnsiConsole.Ask("请输入需要进行测试的密码字典路径[[或将密码字典拖至本窗口]]", "PasswordDictionary.txt");
                    }

                    if (File.Exists(config.Dictionary))
                    {
                        break;
                    }
                }
                while (true)
                {
                    if (StartupParametersCheck(args, "F"))
                    {
                        ArchiveFile = GetParameter(args, "F", AnsiConsole.Ask<string>("请输入需要进行测试的压缩包路径[[或将压缩包拖至本窗口]]:"));
                    }
                    else
                    {
                        ArchiveFile = AnsiConsole.Ask<string>("请输入需要进行测试的压缩包路径[[或将压缩包拖至本窗口]]:");
                    }
                    if (File.Exists(ArchiveFile))
                    {
                        break;
                    }
                }

                SevenZipBase.SetLibraryPath("lib/7z.dll");
                string? EncryptArchivePassword = null;
                await AnsiConsole.Status().StartAsync("测试密码中...", async ctx => {
                    Parallel.ForEach(File.ReadAllLines(config.Dictionary), new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 }, (i, loopState) =>
                    {
                        using var temp = new SevenZipExtractor(ArchiveFile, i);
                        if (temp.Check())
                        {
                            EncryptArchivePassword = i;
                            loopState.Break();
                        }
                    });
                    ctx.Status("测试结束。");
                    await Task.Delay(500);
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
                    Process.Start("Explorer.exe", $"/select, \"{ArchiveFile}[测试报告].txt\"");
                }
            }
        }
    }
}