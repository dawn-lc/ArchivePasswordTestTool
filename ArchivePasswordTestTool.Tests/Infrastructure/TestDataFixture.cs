using System.Diagnostics;

namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 测试样本夹具：确保 TestData 下存在完整样本集合（幂等，缺失才生成）。
/// 生产动作依赖本机工具：7-Zip CLI 必需；WinRAR 仅影响 RAR 系用例（无则相应用例跳过）。
/// 生成参数与本次重构实测时使用的完全一致，密码统一为 Correct123!。
/// </summary>
public sealed class TestDataFixture
{
    public const string CorrectPassword = "Correct123!";

    /// <summary>7-Zip CLI 路径（必需）</summary>
    public string SevenZipExe { get; }

    /// <summary>WinRAR Rar.exe 路径（可选，决定 RAR 系用例是否执行）</summary>
    public string? WinRarExe { get; }

    public string TestDataDir { get; }

    public bool HasWinRAR => WinRarExe is not null;

    public string Archive(string name) => Path.Combine(TestDataDir, name);

    public string Dict(string name) => Path.Combine(TestDataDir, name);

    public TestDataFixture()
    {
        SevenZipExe = FindFirstExisting(
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe")
            ?? throw new InvalidOperationException("生成测试样本需要 7z.exe，请先安装 7-Zip");

        WinRarExe = FindFirstExisting(
            @"C:\Program Files\WinRAR\Rar.exe",
            @"C:\Program Files (x86)\WinRAR\Rar.exe");

        TestDataDir = TestPaths.TestDataDir;
        Directory.CreateDirectory(TestDataDir);

        CreatePayloadIfMissing();
        CreateDictionariesIfMissing();
        CreateSevenZipFixtures();
        CreateNonArchiveExeIfMissing();

        if (WinRarExe is not null)
        {
            CreateRarFixtures();
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

    // ---------- RAR 系样本（需 WinRAR） ----------

    private void CreateRarFixtures()
    {
        string payload = PayloadPath;

        // 单卷 / 分卷 / header 加密分卷 / SFX。
        // 注意：RAR -v 同样不接受带 .part1 序号的目标名，需剥离后传入基名
        CreateIfMissing("Rar5Single.rar",
            p => RunWinRar($"a -ma5 -p{CorrectPassword} \"{p}\" \"{payload}\""));
        CreateIfMissing("Rar5Split.part1.rar",
            p => RunWinRar($"a -ma5 -v8k -p{CorrectPassword} \"{StripVolumeSuffix(p)}\" \"{payload}\""));
        CreateIfMissing("Rar5HeaderSplit.part1.rar",
            p => RunWinRar($"a -ma5 -v8k -hp{CorrectPassword} \"{StripVolumeSuffix(p)}\" \"{payload}\""));
        CreateIfMissing("RarSfx.exe",
            p => RunWinRar($"a -ma5 -sfx -p{CorrectPassword} \"{p}\" \"{payload}\""));
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
    /// 剥离第一分卷的序号后缀（.001 → 去尾，.part1.rar → .rar），得到分卷工具要求的基名
    /// </summary>
    private static string StripVolumeSuffix(string firstVolumePath) =>
        firstVolumePath.EndsWith(".part1.rar", StringComparison.OrdinalIgnoreCase)
            ? firstVolumePath[..^".part1.rar".Length] + ".rar"
            : Path.ChangeExtension(firstVolumePath, null);

    private void RunSevenZip(string arguments) => RunTool(SevenZipExe, arguments);

    private void RunWinRar(string arguments) => RunTool(WinRarExe!, $" {arguments} -idq");

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
