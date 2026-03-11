using Figgle.Fonts;
using Spectre.Console;
using System.Diagnostics;
using System.Net.NetworkInformation;
using static ArchivePasswordTestTool.Utils;
using static ArchivePasswordTestTool.Utils.Util;
using ArchivePasswordTestTool.Properties;

namespace ArchivePasswordTestTool
{
    public class Program
    {
        public static readonly int[] Version = [2, 0, 0];

        /// <summary>
        /// 查找已安装的7-Zip路径
        /// </summary>
        public static string? FindInstalled7Zip()
        {
            // 常见7-Zip安装路径
            string[] possiblePaths =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.dll"),
                "C:\\Program Files\\7-Zip\\7z.dll",
                "C:\\Program Files (x86)\\7-Zip\\7z.dll"
            ];

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // 尝试从注册表查找
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
                if (key != null)
                {
                    var installPath = key.GetValue("Path") as string;
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        string dllPath = Path.Combine(installPath, "7z.dll");
                        if (File.Exists(dllPath))
                        {
                            return dllPath;
                        }

                        dllPath = Path.Combine(installPath, "7z64.dll");
                        if (File.Exists(dllPath))
                        {
                            return dllPath;
                        }
                    }
                }
            }
            catch
            {
                // 忽略注册表访问错误
            }

            return null;
        }

        /// <summary>
        /// 当前使用的7z.dll路径（由主程序设置）
        /// </summary>
        public static string? Current7zPath { get; set; }

        /// <summary>
        /// 创建ArchiveContext的辅助方法，处理完全加密压缩包的特殊情况
        /// </summary>
        private static ArchiveContext CreateArchiveContext(string archivePath, string dllPath, string password, string? preferredFormat)
        {
            // 如果提供了密码，首先尝试使用密码打开
            if (!string.IsNullOrEmpty(password))
            {
                try
                {
                    return SevenZipNative.OpenArchive(archivePath, dllPath, password, preferredFormat);
                }
                catch
                {
                    // 如果使用密码打开失败，可能是密码错误，尝试使用空密码
                    // 对于非完全加密的压缩包，空密码可能可以打开
                    return SevenZipNative.OpenArchive(archivePath, dllPath, "", preferredFormat);
                }
            }
            else
            {
                // 如果没有提供密码，直接使用空密码打开
                return SevenZipNative.OpenArchive(archivePath, dllPath, "", preferredFormat);
            }
        }


        static void Main(string[] args)
        {
            if (Process.GetProcessesByName(Resources.AppName).Count(p => p.MainModule!.FileName != null && p.MainModule.FileName == Environment.ProcessPath) > 1)
            {
                Environment.Exit(0);
            }
            using (SentrySdk.Init(o =>
            {
                o.Dsn = "https://9361b53d22da420c95bdb43d1b78eb1e@o687854.ingest.sentry.io/5773141";
                o.DiagnosticLevel = SentryLevel.Debug;
                o.IsGlobalModeEnabled = true;
                o.TracesSampleRate = 1.0;
                o.Release = $"{string.Join(".", Version)}";
                o.AutoSessionTracking = true;
            }))
            {
                SentrySdk.ConfigureScope(s =>
                {
                    s.User = new()
                    {
                        Id = NetworkInterface.GetAllNetworkInterfaces().First(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet).GetPhysicalAddress().ToString(),
                        Username = Environment.UserName
                    };
                });

                if (Environment.OSVersion.Platform.ToString().Contains("win", StringComparison.InvariantCultureIgnoreCase) && File.Exists("ArchivePasswordTestToolGUI.exe"))
                {
                    Process.Start("ArchivePasswordTestToolGUI.exe");
                    Environment.Exit(0);
                }

                AnsiConsole.Clear();
                AnsiConsole.Write(FiggleFonts.Standard.Render(Resources.AppName));

                string? Default7zipPath = null;
                string? ArchivePath = null;
                string? DictionaryPath = null;
                string? ArchiveHash = null;
                long DictionaryCount = 0;
                bool IsEncryptedArchive = true;
                bool IsSupportQuickTestArchive = true;
                KeyValuePair<uint, ulong>? MinEncryptedFile = null;
                string? EncryptArchivePassword = null;
                Stopwatch sw = new();
                try
                {
                    // 优先检查已安装的7zip
                    string? installed7zPath = FindInstalled7Zip();
                    if (installed7zPath != null)
                    {
                        Default7zipPath = installed7zPath;
                        AnsiConsole.MarkupLine(string.Format(Resources.UsingInstalledSevenZip, installed7zPath));
                    }
                    else
                    {
                        Default7zipPath = Path.Combine(Environment.CurrentDirectory, "7z.dll");
                        if (!File.Exists(Default7zipPath) && !StartupParametersCheck(args, "lib"))
                        {
                            throw new FileNotFoundException(Resources.SevenZipDllPathRequired);
                        }
                        Default7zipPath = GetParameter(args, "lib", Default7zipPath).Replace("\"", "");
                        AnsiConsole.MarkupLine(string.Format(Resources.UsingSpecifiedSevenZipDll, Default7zipPath));
                    }

                    // 验证7z.dll是否存在
                    if (!File.Exists(Default7zipPath))
                    {
                        throw new FileNotFoundException(string.Format(Resources.SevenZipDllNotFound, Default7zipPath));
                    }

                    // 初始化7z.dll
                    try
                    {
                        NativeMethods.Initialize(Default7zipPath);
                        AnsiConsole.MarkupLine(Resources.SevenZipDllLoadedSuccessfully);

                        // 设置当前使用的7z.dll路径，供SevenZipNative使用
                        Current7zPath = Default7zipPath;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(string.Format(Resources.SevenZipDllInitializationFailed, ex.Message), ex);
                    }

                    if (!StartupParametersCheck(args, "D"))
                    {
                        throw new FileNotFoundException(Resources.DictionaryFileNotFound);
                    }

                    DictionaryPath = GetParameter(args, "D", "PasswordDictionary.txt").Replace("\"", "");

                    if (!StartupParametersCheck(args, "F"))
                    {
                        throw new FileNotFoundException(Resources.ArchiveFileNotFound);
                    }

                    ArchivePath = GetParameter(args, "F", "").Replace("\"", "");

                    AnsiConsole.MarkupLine(Resources.AnalyzingArchiveInformation);

                    using (var file = File.OpenRead(ArchivePath))
                    {
#if DEBUG
                        ArchiveHash = "test";
#else
                        ArchiveHash = Convert.ToBase64String(FileHash(file));
#endif
                    }

                    // 使用新的 GetArchiveInfo 方法获取压缩包信息，并缓存格式
                    var archiveInfo = SevenZipNative.GetArchiveInfo(ArchivePath, Default7zipPath);

                    IsSupportQuickTestArchive = archiveInfo.SupportsQuickTest;
                    MinEncryptedFile = archiveInfo.MinEncryptedFile;
                    IsEncryptedArchive = archiveInfo.IsEncrypted;

                    if (!archiveInfo.IsEncrypted)
                    {
                        AnsiConsole.WriteLine(string.Format(Resources.ArchiveNotEncrypted, ArchivePath));
                        EncryptArchivePassword = "";
                        IsEncryptedArchive = false;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(string.Format(Resources.ArchiveInformation, archiveInfo.Format, archiveInfo.FileCount, archiveInfo.IsFullyEncrypted, archiveInfo.SupportsQuickTest));
                    }

                    if (IsEncryptedArchive)
                    {
                        Dictionary Dictionary = new(DictionaryPath);
                        DictionaryCount = Dictionary.Count;
                        AnsiConsole.MarkupLine(string.Format(Resources.DictionaryPasswordCount, DictionaryCount));
                        SentrySdk.AddBreadcrumb(
                            message: $"DictionaryCount {DictionaryCount}",
                            category: "Info",
                            level: BreadcrumbLevel.Info
                        );

                        string? preferredFormat = archiveInfo.GetPreferredFormat();
                        bool isFullyEncrypted = archiveInfo.IsFullyEncrypted;

                        int maxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1);
                        var contextPool = new List<ArchiveContext>();

                        if (!isFullyEncrypted)
                        {
                            for (int i = 0; i < maxDegreeOfParallelism; i++)
                            {
                                try
                                {
                                    var context = CreateArchiveContext(ArchivePath, Default7zipPath, "", preferredFormat);
                                    contextPool.Add(context);
                                }
                                catch
                                {
                                }
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[yellow]{Resources.FullyEncryptedArchiveWarning}[/]");
                        }

                        // 使用线程安全的进度更新
                        long testedCount = 0;
                        sw.Restart();
                        AnsiConsole.Progress()
                            .AutoClear(true)
                            .HideCompleted(true)
                            .Columns([
                                new TaskDescriptionColumn(),
                                new ProgressBarColumn(),
                                new PercentageColumn(),
                                new RemainingTimeColumn(),
                                new SpinnerColumn()
                            ])
                            .Start(ctx =>
                            {
                                var TestProgressBar = ctx.AddTask(Resources.TestingPasswordProgress, maxValue: DictionaryCount);

                                Parallel.For(0, Dictionary.Count, new ParallelOptions() { MaxDegreeOfParallelism = maxDegreeOfParallelism }, (i, loopState) =>
                                {
                                    try
                                    {
                                        string Password = Dictionary.Pop();

                                        bool passwordCorrect;
                                        if (IsSupportQuickTestArchive && MinEncryptedFile is not null)
                                        {
                                            // 快速测试：只测试最小的加密文件
                                            uint[] fileIndices = [MinEncryptedFile.Value.Key];

                                            // 从池中获取或创建ArchiveContext
                                            ArchiveContext? context = null;
                                            lock (contextPool)
                                            {
                                                if (contextPool.Count > 0)
                                                {
                                                    context = contextPool[0];
                                                    contextPool.RemoveAt(0);
                                                }
                                            }

                                            // 如果池中没有可用的上下文，创建一个新的
                                            context ??= CreateArchiveContext(ArchivePath, Default7zipPath, isFullyEncrypted ? Password : "", preferredFormat);

                                            using (context)
                                            {
                                                passwordCorrect = context.TestPassword(Password, fileIndices);
                                            }
                                        }
                                        else
                                        {
                                            // 完整测试：测试所有文件
                                            // 从池中获取或创建ArchiveContext
                                            ArchiveContext? context = null;
                                            lock (contextPool)
                                            {
                                                if (contextPool.Count > 0)
                                                {
                                                    context = contextPool[0];
                                                    contextPool.RemoveAt(0);
                                                }
                                            }

                                            // 如果池中没有可用的上下文，创建一个新的
                                            context ??= CreateArchiveContext(ArchivePath, Default7zipPath, isFullyEncrypted ? Password : "", preferredFormat);

                                            using (context)
                                            {
                                                passwordCorrect = context.TestPassword(Password);
                                            }
                                        }

                                        if (passwordCorrect)
                                        {
                                            sw.Stop();
                                            EncryptArchivePassword = Password;
                                            loopState.Break();
                                        }

                                        // 使用原子操作更新进度
                                        Interlocked.Increment(ref testedCount);
                                        TestProgressBar.Increment(1);
                                    }
                                    catch (Exception)
                                    {
                                        // 即使发生异常，也要更新进度
                                        Interlocked.Increment(ref testedCount);
                                        TestProgressBar.Increment(1);
                                    }
                                });

                                // 确保进度条完成
                                TestProgressBar.Value = TestProgressBar.MaxValue;
                            });

                        // 清理剩余的ArchiveContext
                        foreach (var context in contextPool)
                        {
                            context.Dispose();
                        }
                        contextPool.Clear();

                        AnsiConsole.MarkupLine(EncryptArchivePassword != null ? string.Format(Resources.PasswordFoundSuccess, EncryptArchivePassword) : Resources.PasswordNotFound);
                        AnsiConsole.MarkupLine(string.Format(Resources.TestCompletedWithTime, sw.Elapsed));
                        SentrySdk.AddBreadcrumb(
                            message: $"Archive Hash [{ArchiveHash}] Results [{EncryptArchivePassword}]",
                            category: "Info",
                            level: BreadcrumbLevel.Debug
                        );
                    }
                }
                catch (FileNotFoundException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    Environment.Exit(1);
                }
                catch (Exception ex)
                {
                    SentrySdk.AddBreadcrumb(
                        message: $"DictionaryCount {DictionaryCount}",
                        category: "Info",
                        level: BreadcrumbLevel.Info
                    );
                    if (IsEncryptedArchive)
                    {
                        SentrySdk.AddBreadcrumb(
                            message: $"EncryptArchivePassword {EncryptArchivePassword ?? "NULL"}",
                            category: "Info",
                            level: BreadcrumbLevel.Info
                        );
                    }
                    SentrySdk.CaptureException(ex);
                    Console.Error.WriteLine(ex.Message);
                    Environment.Exit(1);
                }
                finally
                {
                    if (ArchivePath is not null && DictionaryPath is not null)
                    {
#if !DEBUG
                        if (AnsiConsole.Confirm(Resources.SaveTestResultsPrompt, true))
                        {
                            using (StreamWriter TestOut = new($"{ArchivePath}[results].txt", false))
                            {
                                TestOut.WriteLine(string.Format(Resources.TestResultArchive, ArchivePath));
                                TestOut.WriteLine(string.Format(Resources.TestResultDictionary, DictionaryPath));
                                TestOut.WriteLine(string.Format(Resources.TestResultElapsedTime, sw.Elapsed));
                                TestOut.WriteLine(string.Format(Resources.TestResultArchiveHash, ArchiveHash));
                                TestOut.WriteLine(EncryptArchivePassword != null ? string.Format(Resources.TestResultDecompressionPassword, EncryptArchivePassword) : Resources.TestResultPasswordNotFound);
                            }

                            if (Environment.OSVersion.Platform.ToString().Contains("win", StringComparison.InvariantCultureIgnoreCase) && AnsiConsole.Confirm(Resources.ViewTestReportPrompt, true))
                            {
                                Process.Start("explorer.exe", $"/select, \"{ArchivePath}[results].txt\"");
                            }
                        }
#endif
                    }
                }
            }
        }
    }
}