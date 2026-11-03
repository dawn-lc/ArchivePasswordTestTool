using System.Diagnostics;

namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 测试样本夹具：确保 TestData 下存在完整样本集合（幂等，缺失才生成）。
/// 生产动作只依赖 7-Zip CLI（唯一的外部工具）；RAR 系样本由 <see cref="Rar5ArchiveWriter"/> 纯托管合成，
/// 不再需要 WinRAR——托管 CI 镜像里根本没有 WinRAR，而它又是闭源付费软件，装不了也不该装。
/// 密码统一为 Correct123!。
/// </summary>
public sealed class TestDataFixture
{
    public const string CorrectPassword = "Correct123!";

    /// <summary>
    /// 样本生成互斥锁。
    /// <para>
    /// 多个测试类各自持有 <c>Lazy&lt;TestDataFixture&gt;</c>（<see cref="TestDataCollection"/> 之外的类是各自 new 的），
    /// xunit 会把不同 collection 并行跑，于是两个线程可能同时判定「样本不存在」并同时执行
    /// <c>7z a</c> 写同一个文件——后到的那个报 <c>The file exists.</c>（exit=2）。
    /// </para>
    /// <para>
    /// 本地测不出这个问题：开发机上的 TestData 早已生成好，<c>File.Exists</c> 恒为 true，
    /// 生成路径根本不会进入。CI 是全新 checkout，实测一次性挂掉 56 个用例。
    /// </para>
    /// </summary>
    private static readonly Lock CreationGate = new();

    /// <summary>7-Zip CLI 路径（唯一必需的外部工具）</summary>
    public string SevenZipExe { get; }

    public string TestDataDir { get; }

    public string Archive(string name) => Path.Combine(TestDataDir, name);

    public string Dict(string name) => Path.Combine(TestDataDir, name);

    public TestDataFixture()
    {
        SevenZipExe = FindFirstExisting(
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe")
            ?? throw new InvalidOperationException("生成测试样本需要 7z.exe，请先安装 7-Zip");

        TestDataDir = TestPaths.TestDataDir;

        // 全部生成动作串行化：第二个线程进来时第一个已完成，File.Exists 全为 true 直接跳过。
        // 文件系统操作与 7z 子进程都在锁内，不追求并行——样本总量只有几 MB
        lock (CreationGate)
        {
            _ = Directory.CreateDirectory(TestDataDir);

            CreatePayloadIfMissing();
            CreateDictionariesIfMissing();
            CreateSevenZipFixtures();
            CreateRarFixtureFiles();
            CreateNonArchiveExeIfMissing();
        }
    }

    // ---------- 载荷与字典 ----------

    private string PayloadPath => Archive("Payload.bin");

    private void CreatePayloadIfMissing()
    {
        if (File.Exists(PayloadPath))
        {
            return;
        }

        var bytes = new byte[51200];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(PayloadPath, bytes);
    }

    /// <summary>8 行字典：7 行错误密码 + 1 行正确密码（正确密码在末尾，覆盖全量扫描路径）</summary>
    private static readonly string[] FullDictionaryLines =
    [
        "wrong1", "wrong2", "abc", "123456", "password", "qwerty", "wrong3", CorrectPassword
    ];

    /// <summary>7 行错误密码（负例字典）</summary>
    private static readonly string[] WrongOnlyDictionaryLines =
    [
        "wrong1", "wrong2", "abc", "123456", "password", "qwerty", "wrong3"
    ];

    private void CreateDictionariesIfMissing()
    {
        WriteLinesIfMissing("PasswordDictionary.txt", FullDictionaryLines);
        WriteLinesIfMissing("DictOK.txt", [CorrectPassword]);
        WriteLinesIfMissing("Dict7.txt", WrongOnlyDictionaryLines);
        WriteBigDictionaryIfMissing();
    }

    /// <summary>
    /// 2100 行大字典（2099 错误 + 末尾正确密码）：
    /// 跨过 Dictionary 类内默认批次 Limit(2048)，对并发 refill 路径做端到端覆盖
    /// </summary>
    private void WriteBigDictionaryIfMissing()
    {
        if (File.Exists(Dict("BigDictionary.txt")))
        {
            return;
        }

        var lines = new List<string>(2100)
        {
            Capacity = 2100
        };

        for (int i = 1; i <= 2099; i++)
        {
            lines.Add($"WrongPassword{i}");
        }

        lines.Add(CorrectPassword);
        File.WriteAllLines(Dict("BigDictionary.txt"), lines);
    }

    private void WriteLinesIfMissing(string name, string[] lines)
    {
        string path = Dict(name);
        if (!File.Exists(path))
        {
            File.WriteAllLines(path, lines);
        }
    }

    // ---------- 7-Zip 系样本 ----------

    private void CreateSevenZipFixtures()
    {
        string payload = PayloadPath;
        string dir = TestDataDir;

        // 单文件（标准加密 / header 加密）
        CreateIfMissing("Secret.7z",
            p => RunSevenZip($"a -y \"{p}\" \"{payload}\" -p{CorrectPassword}"));
        CreateIfMissing("SecretZip.zip",
            p => RunSevenZip($"a -y \"{p}\" \"{payload}\" -p{CorrectPassword}"));
        CreateIfMissing("SecretHeader.7z",
            p => RunSevenZip($"a -y -mhe=on \"{p}\" \"{payload}\" -p{CorrectPassword}"));

        // 分卷（标准加密 / header 加密 / zip）。
        // 注意：7z -v 的目标名不能带分卷序号后缀，否则报"尚未实现"，传入基名由工具自动生成 .001..N
        CreateIfMissing("SecretSplit.7z.001",
            p => RunSevenZip($"a -y \"{StripVolumeSuffix(p)}\" \"{payload}\" -p{CorrectPassword} -v8k"));
        CreateIfMissing("SecretSplitHeader.7z.001",
            p => RunSevenZip($"a -y -mhe=on \"{StripVolumeSuffix(p)}\" \"{payload}\" -p{CorrectPassword} -v8k"));
        CreateIfMissing("SecretSplitZip.zip.001",
            p => RunSevenZip($"a -y \"{StripVolumeSuffix(p)}\" \"{payload}\" -p{CorrectPassword} -v8k"));

        // SFX 自解压（标准加密 / header 加密）
        CreateIfMissing("SecretSfx.exe",
            p => RunSevenZip($"a -y -sfx \"{p}\" \"{payload}\" -p{CorrectPassword}"));
        CreateIfMissing("SecretSfxHeader.exe",
            p => RunSevenZip($"a -y -sfx -mhe=on \"{p}\" \"{payload}\" -p{CorrectPassword}"));
    }

    private void CreateNonArchiveExeIfMissing()
    {
        string path = Archive("NotAnArchive.exe");
        if (File.Exists(path))
        {
            return;
        }

        string notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        if (File.Exists(notepad))
        {
            File.Copy(notepad, path);
        }
        else
        {
            // 兜底：合成一个不含压缩包签名的最小 MZ 文件
            var mz = new byte[65536];
            mz[0] = (byte)'M';
            mz[1] = (byte)'Z';
            File.WriteAllBytes(path, mz);
        }
    }

    // ---------- RAR 系样本（纯托管合成，不依赖 WinRAR） ----------

    /// <summary>
    /// RAR5 样本的盐与 IV：全部写死，保证生成的字节每次都一样（可以断言体积与偏移）。
    /// 每个样本各用一组，避免不同样本共享密钥材料。
    /// </summary>
    private static readonly byte[] SingleDataSalt =
        [0x6B, 0x24, 0x80, 0x07, 0xF1, 0xE7, 0x71, 0x3F, 0x3D, 0xCE, 0xBE, 0x8B, 0xFE, 0xD4, 0x52, 0x49];

    private static readonly byte[] SingleDataIv =
        [0xA4, 0x2D, 0x69, 0x89, 0xE2, 0x76, 0x92, 0xBC, 0xAF, 0x9B, 0x94, 0xCA, 0xA1, 0xE4, 0xA1, 0x34];

    private static readonly byte[] SplitDataSalt =
        [0x7D, 0xEC, 0xF2, 0xEA, 0xE9, 0x92, 0xE1, 0x3F, 0xFE, 0x2A, 0xA1, 0xB0, 0xE2, 0x00, 0x6D, 0xF8];

    private static readonly byte[] SplitDataIv =
        [0xDA, 0x33, 0x5D, 0x21, 0x3D, 0x0A, 0xBD, 0x7E, 0xF1, 0xA7, 0x44, 0x3F, 0x57, 0xE4, 0x40, 0x99];

    private static readonly byte[] HeaderSplitDataSalt =
        [0x15, 0x8F, 0x97, 0x5A, 0x02, 0x1B, 0x3C, 0xFC, 0x9D, 0x01, 0x04, 0x80, 0x90, 0x03, 0x20, 0x1F];

    private static readonly byte[] HeaderSplitDataIv =
        [0x43, 0x32, 0xAD, 0x80, 0x4D, 0x1A, 0x9E, 0x3B, 0xC7, 0x25, 0x08, 0x74, 0xB1, 0x6D, 0x03, 0xE9];

    private static readonly byte[] HeaderSalt =
        [0x53, 0xF9, 0xB2, 0x69, 0xD8, 0x43, 0xF5, 0x43, 0xB4, 0xF4, 0xD5, 0x4D, 0xF8, 0xCF, 0xFF, 0xD2];

    private void CreateRarFixtureFiles()
    {
        byte[] payload = File.ReadAllBytes(PayloadPath);
        const string entryName = "Payload.bin";

        CreateIfMissing("Rar5Single.rar", path => _ = Rar5ArchiveWriter.Create(path, new Rar5WriterOptions
        {
            EntryName = entryName,
            Plaintext = payload,
            Password = CorrectPassword,
            DataSalt = SingleDataSalt,
            DataIv = SingleDataIv,
        }));

        CreateIfMissing("Rar5Split.part1.rar", path => _ = Rar5ArchiveWriter.Create(path, new Rar5WriterOptions
        {
            EntryName = entryName,
            Plaintext = payload,
            Password = CorrectPassword,
            DataSalt = SplitDataSalt,
            DataIv = SplitDataIv,
            VolumeCount = 3,
        }));

        CreateIfMissing("Rar5HeaderSplit.part1.rar", path => _ = Rar5ArchiveWriter.Create(path, new Rar5WriterOptions
        {
            EntryName = entryName,
            Plaintext = payload,
            Password = CorrectPassword,
            DataSalt = HeaderSplitDataSalt,
            DataIv = HeaderSplitDataIv,
            EncryptHeaders = true,
            HeaderSalt = HeaderSalt,
            VolumeCount = 3,
        }));

        CreateIfMissing("RarSfx.exe", path =>
        {
            // 只需要 4 KB 的 MZ 桩：RAR5 允许最多 1 MB 的自解压前缀，7-Zip 靠扫描签名定位
            var stub = new byte[4096];
            stub[0] = (byte)'M';
            stub[1] = (byte)'Z';

            _ = Rar5ArchiveWriter.Create(path, new Rar5WriterOptions
            {
                EntryName = entryName,
                Plaintext = payload,
                Password = CorrectPassword,
                DataSalt = SingleDataSalt,
                DataIv = SingleDataIv,
                SfxStub = stub,
            });
        });
    }

    // ---------- 工具方法 ----------

    private void CreateIfMissing(string name, Action<string> create)
    {
        string path = Archive(name);
        if (!File.Exists(path))
        {
            create(path);
        }
    }

    /// <summary>
    /// 剥掉第一分卷的序号后缀（<c>X.zip.001</c> → <c>X.zip</c>），得到 7z 分卷参数要求的基名。
    /// 只用于 7z 系样本：RAR 分卷由 <see cref="Rar5ArchiveWriter"/> 直接按 <c>X.partN.rar</c> 命名写出，不经过这里。
    /// </summary>
    private static string StripVolumeSuffix(string firstVolumePath) => Path.ChangeExtension(firstVolumePath, null);

    private void RunSevenZip(string arguments) => RunTool(SevenZipExe, arguments);

    private static void RunTool(string exe, string arguments)
    {
        var startInfo = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动进程：{exe}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"工具执行超时：{exe} {arguments}");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"工具执行失败 exit={process.ExitCode}：{exe} {arguments}\n{stdout}\n{stderr}");
        }
    }

    private static string? FindFirstExisting(params string[] paths) =>
        paths.FirstOrDefault(File.Exists);
}
