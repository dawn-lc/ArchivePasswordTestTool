using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 快速判错的性能测量（默认跳过，设 <c>APTT_BENCH=1</c> 才执行）。
/// <para>
/// 测量对象是两类「特殊压缩包」——密文没有任何可自校验结构、错误密码必须靠解完
/// 数据才能判错，因此完整测密的每个候选代价与文件大小成正比：
/// <list type="bullet">
/// <item>RAR4 存储模式：每候选都要解完整个文件，靠文件尾 CRC 判错</item>
/// <item>ZipCrypto 存储模式：12 字节加密头只有 1 字节校验（1/256 假阳性），
/// 命中的候选仍要完整解压</item>
/// </list>
/// 对照方式是同一份大样本、同一份「全错误密码」字典，分别跑
/// ① 默认（快速判错启用）② <c>APTT_NO_FAST_FILTER=1</c>（完整解密）。
/// </para>
/// <para>
/// 结果同时输出到测试日志与 <c>%TEMP%\ApttBenchResult.txt</c>。
/// 用法（需显式开启，否则用例即时返回）：
/// <code>
/// dotnet test ArchivePasswordTestTool.Tests -c Release --filter "FullyQualifiedName~FastFilterBenchmarkTests" --logger "console;verbosity=detailed" --environment APTT_BENCH=1
/// </code>
/// 可调参数：<c>APTT_BENCH_MB</c>（样本大小，默认 200）、<c>APTT_BENCH_RAR4_COUNT</c>（默认 500）、<c>APTT_BENCH_ZIP_COUNT</c>（默认 5000）。
/// </para>
/// </summary>
public sealed class FastFilterBenchmarkTests(ITestOutputHelper output)
{
    private const string CorrectPassword = "Correct123!";

    private static readonly byte[] FixedSalt = [0x90, 0xBB, 0xAE, 0xF8, 0xD1, 0xC9, 0x0B, 0xC2];

    /// <summary>环境变量门控：没有显式开启时立即返回，不让 CI 承担几分钟的测量</summary>
    private static bool Enabled => Environment.GetEnvironmentVariable("APTT_BENCH") == "1";

    private static int SampleMegabytes =>
        int.TryParse(Environment.GetEnvironmentVariable("APTT_BENCH_MB"), out int mb) && mb > 0 ? mb : 200;

    [Fact]
    public void Rar4StoreSpeedup()
    {
        if (!RequireBenchmark())
        {
            return;
        }

        int passwordCount = ReadCount("APTT_BENCH_RAR4_COUNT", fallback: 500);
        int sizeBytes = SampleMegabytes * 1024 * 1024;
        string dir = CreateBenchDirectory("rar4");

        try
        {
            string archivePath = Path.Combine(dir, "Bench.mp4.rar");
            byte[] plaintext = CreateMp4LikePlaintext(sizeBytes);

            output.WriteLine($"生成 RAR4 存储模式样本：{sizeBytes / (1024 * 1024)} MB …");
            var generateWatch = Stopwatch.StartNew();
            Rar4ArchiveWriter.CreateStoredEncrypted(archivePath, "Bench.mp4", plaintext, CorrectPassword, FixedSalt);
            generateWatch.Stop();

            long archiveBytes = new FileInfo(archivePath).Length;
            output.WriteLine($"样本大小：{archiveBytes / (1024 * 1024)} MB（生成耗时 {generateWatch.Elapsed.TotalSeconds:0.0} s）");

            string dictPath = WriteWrongDictionary(dir, passwordCount);

            // 探测器本身的单候选成本用进程内微基准测：KDF 是纯 CPU，
            // 单线程重复调用即可量出（进程级差分会被几百毫秒的固定开销淹没）
            var info = new ArchiveInfo
            {
                Type = SevenZipGuids.CLSID_CFormatRar,
                FileCount = 1,
                IsEncrypted = true,
                IsHeaderEncryption = false,
                SupportsQuickTest = false,
            };
            Rar4StoreProbe probe = Assert.IsType<Rar4StoreProbe>(Rar4StoreProbe.TryCreate(archivePath, info));
            double probeMarginMs = MeasureProbe(probe, iterations: 100);

            Measure(
                title: "RAR4 存储模式（全错字典）",
                archivePath: archivePath,
                dictionaryPath: dictPath,
                passwordCount: passwordCount,
                archiveBytes: archiveBytes,
                probeMarginMs: probeMarginMs,
                everyCandidateDecodesAllBytes: true);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 7z Copy（不压缩）+ AES：完整解密必须把整个文件解完才能靠 CRC 判错，
    /// 且加线程/加进程都无效（瓶颈是内存带宽）。快速判错只派生密钥、解首块。
    /// </summary>
    [Fact]
    public void SevenZipCopySpeedup()
    {
        if (!RequireBenchmark())
        {
            return;
        }

        int passwordCount = ReadCount("APTT_BENCH_7Z_COUNT", fallback: 500);
        int sizeBytes = SampleMegabytes * 1024 * 1024;
        string dir = CreateBenchDirectory("7z");

        try
        {
            string payloadPath = Path.Combine(dir, "Bench.mp4");
            File.WriteAllBytes(payloadPath, CreateMp4LikePlaintext(sizeBytes));

            string archivePath = Path.Combine(dir, "Bench.7z");
            output.WriteLine("生成 7z Copy+AES 样本（7z -mx=0）…");
            RunSevenZip([
                "a", "-t7z", "-mx=0", "-p" + CorrectPassword, "-y", archivePath, payloadPath,
            ]);
            File.Delete(payloadPath);

            long archiveBytes = new FileInfo(archivePath).Length;
            output.WriteLine($"样本大小：{archiveBytes / (1024 * 1024)} MB");

            string dictPath = WriteWrongDictionary(dir, passwordCount);

            var info = new ArchiveInfo
            {
                Type = SevenZipGuids.CLSID_CFormat7z,
                FileCount = 1,
                IsEncrypted = true,
                IsHeaderEncryption = false,
                SupportsQuickTest = false,
            };
            SevenZipStoreProbe probe = Assert.IsType<SevenZipStoreProbe>(SevenZipStoreProbe.TryCreate(archivePath, info));
            double probeMarginMs = MeasureProbe(probe, iterations: 100);

            // 7z Copy 与 RAR4 存储模式同构：每个候选都要解全量，成本随样本线性增长
            Measure(
                title: "7z Copy+AES（全错字典）",
                archivePath: archivePath,
                dictionaryPath: dictPath,
                passwordCount: passwordCount,
                archiveBytes: archiveBytes,
                probeMarginMs: probeMarginMs,
                everyCandidateDecodesAllBytes: true);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void ZipCryptoStoreSpeedup()
    {
        if (!RequireBenchmark())
        {
            return;
        }

        int passwordCount = ReadCount("APTT_BENCH_ZIP_COUNT", fallback: 5000);
        int sizeBytes = SampleMegabytes * 1024 * 1024;
        string dir = CreateBenchDirectory("zip");

        try
        {
            string payloadPath = Path.Combine(dir, "Bench.mp4");
            File.WriteAllBytes(payloadPath, CreateMp4LikePlaintext(sizeBytes));

            string archivePath = Path.Combine(dir, "Bench.zip");
            output.WriteLine("生成 ZipCrypto 存储模式样本（7z -mem=ZipCrypto -mx=0）…");
            RunSevenZip([
                "a", "-tzip", "-mx=0", "-mem=ZipCrypto",
                "-p" + CorrectPassword, "-y", archivePath, payloadPath,
            ]);
            File.Delete(payloadPath);

            long archiveBytes = new FileInfo(archivePath).Length;
            output.WriteLine($"样本大小：{archiveBytes / (1024 * 1024)} MB");

            string dictPath = WriteWrongDictionary(dir, passwordCount);

            var info = new ArchiveInfo
            {
                Type = SevenZipGuids.CLSID_CFormatZip,
                FileCount = 1,
                IsEncrypted = true,
                IsHeaderEncryption = false,
                SupportsQuickTest = false,
            };
            ZipStoreProbe probe = Assert.IsType<ZipStoreProbe>(ZipStoreProbe.TryCreate(archivePath, info));
            double probeMarginMs = MeasureProbe(probe, iterations: 20000);

            Measure(
                title: "ZipCrypto 存储模式（全错字典）",
                archivePath: archivePath,
                dictionaryPath: dictPath,
                passwordCount: passwordCount,
                archiveBytes: archiveBytes,
                probeMarginMs: probeMarginMs,
                everyCandidateDecodesAllBytes: false);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 单线程重复调用探测器，得到「每候选」的真实成本。
    /// 同时断言全部为 NoMatch：这也是假阳性率的一次抽查（错密码不应被判为候选）。
    /// </summary>
    private static double MeasureProbe(IPasswordProbe probe, int iterations)
    {
        // 预热：JIT 与 AES / CNG 句柄初始化不计入统计
        for (int i = 0; i < Math.Min(iterations, 20); i++)
        {
            _ = probe.Probe($"Warmup{i:D6}");
        }

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            Assert.Equal(PasswordProbeResult.NoMatch, probe.Probe($"WrongCandidate{i:D6}"));
        }

        watch.Stop();
        return watch.Elapsed.TotalMilliseconds / iterations;
    }

    /// <summary>
    /// 同一归档跑多次并对比：快速判错启用 / 关闭（完整解密）。
    /// 每次都是独立进程，因此包含启动、打开归档等固定开销——先用 1 条字典量出该开销，
    /// 再从总时长里扣掉，得到与字典长度无关的单候选边际成本。
    /// </summary>
    private void Measure(
        string title,
        string archivePath,
        string dictionaryPath,
        int passwordCount,
        long archiveBytes,
        double probeMarginMs,
        bool everyCandidateDecodesAllBytes)
    {
        // 预热：把归档读入系统页缓存，让完整解密路径拿到最有利（最快）的条件，
        // 这样测出的加速比是保守下界而不是被冷盘 I/O 放大后的数字
        using (var warmup = File.OpenRead(archivePath))
        {
            byte[] buffer = new byte[1024 * 1024];
            while (warmup.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        // 基线：1 条字典，量出「启动 + 打开归档」的固定开销（两种路径都要付）
        string baselineDict = WriteWrongDictionary(Path.GetDirectoryName(dictionaryPath)!, 1, prefix: "Baseline");
        var fastBaseline = RunTool(archivePath, baselineDict, noFastFilter: false);
        var slowBaseline = RunTool(archivePath, baselineDict, noFastFilter: true);

        var fast = RunTool(archivePath, dictionaryPath, noFastFilter: false);
        var slow = RunTool(archivePath, dictionaryPath, noFastFilter: true);

        double fastPerSecond = passwordCount / Math.Max(fast.Seconds, 0.001);
        double slowPerSecond = passwordCount / Math.Max(slow.Seconds, 0.001);

        // 边际单候选成本：完整解密用「扣掉固定开销后的差分」量，
        // 快速判错的单候选成本改用探测器微基准（差分在这里分辨率不够）
        double slowMarginMs = MarginMilliseconds(slow.Seconds, slowBaseline.Seconds, passwordCount);
        double megabytes = archiveBytes / (1024.0 * 1024.0);

        // 两条路径的并行扩展性不同（KDF 是纯 CPU 可全核扩展，解码受内存带宽限制），
        // 因此单线程成本比与进程级加速比可能方向相反，不能混作一个「加速比」：
        // 分别给出单候选成本与含并行的边际吞吐。
        double singleThreadRatio = slowMarginMs / Math.Max(probeMarginMs, 0.000001);
        double costCrossoverMegabytes = probeMarginMs >= slowMarginMs
            ? megabytes * probeMarginMs / Math.Max(slowMarginMs, 0.000001)
            : 0;

        var report = new StringBuilder();
        report.AppendLine($"===== {title} =====");
        report.AppendLine($"样本 {megabytes:0} MB · 字典 {passwordCount} 条（全错）· 逻辑核 {Environment.ProcessorCount} / 物理核 {CpuTopology.PhysicalCoreCount}");
        report.AppendLine($"快速判错：{fast.Seconds:0.00} s → {fastPerSecond:0.0} 个/秒（固定开销 {fastBaseline.Seconds:0.00} s）");
        report.AppendLine($"完整解密：{slow.Seconds:0.00} s → {slowPerSecond:0.0} 个/秒（固定开销 {slowBaseline.Seconds:0.00} s）");
        report.AppendLine($"总时长加速比：{slow.Seconds / Math.Max(fast.Seconds, 0.001):0.0}×");
        report.AppendLine($"单候选成本（单线程）：探测器 {probeMarginMs:0.000} ms / 完整解密 {slowMarginMs:0.00} ms（比值 {singleThreadRatio:0.0}×）");

        if (costCrossoverMegabytes > 0)
        {
            report.AppendLine($"成本交叉点：约 {costCrossoverMegabytes:0} MB —— 小于该尺寸时单候选上探测器反而更慢，"
                + "快速判错只对大包有意义（进程级仍可能更快，差在并行扩展性）。");
        }

        // 成本模型不同的两类格式不能套同一个外推公式：
        // RAR4 每候选都要解全量（成本与样本大小线性）；
        // ZipCrypto 只有约 1/256 的假阳性才解全量，其余仅验 12 字节头，线性外推会严重高估。
        if (everyCandidateDecodesAllBytes)
        {
            double slowMarginThroughput = (passwordCount - 1) / Math.Max(slow.Seconds - slowBaseline.Seconds, 0.001);
            double fastMarginThroughput = (passwordCount - 1) / Math.Max(fast.Seconds - fastBaseline.Seconds, 0.001);
            double slowAtOnePointTwoGigabytes = slowMarginThroughput * megabytes / 1228.8;

            report.AppendLine($"边际吞吐（含并行）：快速 {fastMarginThroughput:0.0} 个/秒 / 完整 {slowMarginThroughput:0.0} 个/秒");
            report.AppendLine($"外推 1.2 GB：完整解密降到 {slowAtOnePointTwoGigabytes:0.0} 个/秒（内存带宽瓶颈，加线程无效），"
                + $"加速比约 {fastMarginThroughput / Math.Max(slowAtOnePointTwoGigabytes, 0.001):0.0}×（按边际吞吐）");
        }
        else
        {
            report.AppendLine("注：ZipCrypto 只有约 1/256 的候选需要解全量，成本不随样本大小线性增长，因此不做线性外推。");
        }

        report.AppendLine();

        output.WriteLine(report.ToString());
        AppendReport(report.ToString());

        Assert.Equal(0, fast.ExitCode);
        Assert.Equal(0, slow.ExitCode);
        Assert.Contains("快速", fast.Output);
    }

    /// <summary>扣除「1 条字典」基线后，每个候选密码的平均耗时（毫秒）</summary>
    private static double MarginMilliseconds(double totalSeconds, double baselineSeconds, int passwordCount) =>
        passwordCount > 1
            ? (totalSeconds - baselineSeconds) / (passwordCount - 1) * 1000
            : 0;

    private static (int ExitCode, double Seconds, string Output) RunTool(
        string archivePath,
        string dictionaryPath,
        bool noFastFilter)
    {
        var startInfo = new ProcessStartInfo(TestPaths.MainExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-D");
        startInfo.ArgumentList.Add(dictionaryPath);
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-NoQuery");
        startInfo.ArgumentList.Add("-NoReport");

        // 固定并行度，避免测量时被机器负载或自适应策略影响
        startInfo.Environment["APTT_NO_UPGRADE_CHECK"] = "1";
        if (noFastFilter)
        {
            startInfo.Environment["APTT_NO_FAST_FILTER"] = "1";
        }

        var watch = Stopwatch.StartNew();

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        // 两个流都必须持续消费：任一管道写满都会让子进程阻塞
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        watch.Stop();

        return (process.ExitCode, watch.Elapsed.TotalSeconds, stdoutTask.Result + stderrTask.Result);
    }

    private bool RequireBenchmark()
    {
        if (Enabled)
        {
            return true;
        }

        output.WriteLine("跳过性能测量：设置 APTT_BENCH=1 后重新运行");
        return false;
    }

    private static int ReadCount(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out int value) && value > 0 ? value : fallback;

    private static string CreateBenchDirectory(string kind)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ApttBench-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>全错字典：内容确定、长度一致，避免任何候选意外命中</summary>
    private static string WriteWrongDictionary(string dir, int count, string prefix = "Wrong")
    {
        string path = Path.Combine(dir, $"{prefix}.txt");
        var lines = new string[count];
        for (int i = 0; i < count; i++)
        {
            lines[i] = $"{prefix}Candidate{i:D6}";
        }

        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// 以合法 MP4 首盒开头的大块明文（首 24 字节可被格式签名识别，其余为确定性填充，
    /// 保证产出可重复）。存储模式下它就是密文长度，决定完整解密的单候选代价。
    /// </summary>
    private static byte[] CreateMp4LikePlaintext(int length)
    {
        byte[] plaintext = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(plaintext, 0x18);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(plaintext, 4);
        Encoding.ASCII.GetBytes("isom").CopyTo(plaintext, 8);
        BinaryPrimitives.WriteUInt32BigEndian(plaintext.AsSpan(12), 0x200);
        Encoding.ASCII.GetBytes("isomiso2").CopyTo(plaintext, 16);

        for (int i = 24; i < plaintext.Length; i++)
        {
            plaintext[i] = (byte)(i * 7 + 3);
        }

        return plaintext;
    }

    private static void RunSevenZip(string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Fixture.Value.SevenZipExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"7z 执行失败（exit {process.ExitCode}）：\n{stdout}\n{stderr}");
    }

    private static void AppendReport(string text)
    {
        try
        {
            // 写临时目录而不是 TestData：测量产物不该出现在仓库里
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ApttBenchResult.txt"), text, Encoding.UTF8);
        }
        catch (IOException)
        {
            // 报告文件只是本机产物，写不进去不影响测量结果（日志里已有完整内容）
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时样本清理失败不影响结果
        }
    }

    private static readonly Lazy<TestDataFixture> Fixture = new(() => new TestDataFixture());
}
