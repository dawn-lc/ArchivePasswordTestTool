using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 端到端集成测试：启动真实进程运行主程序，覆盖本次重构手工验证过的全部场景矩阵。
/// 成功 = exit 0 且输出包含密码；失败负例 = exit 0 且不包含密码；格式不识别 = exit 1。
/// 输出按 UTF-8 读取——主程序在重定向时已统一为 UTF-8（见 Program.Main）。
/// </summary>
[Collection(TestDataCollection.Name)]
public sealed class EndToEndTests(TestDataFixture fx, ITestOutputHelper output)
{
    public static TheoryData<string, string> PositiveCases => new()
    {
        // (字典, 压缩包)
        { "PasswordDictionary.txt", "Secret.7z" },
        { "PasswordDictionary.txt", "SecretZip.zip" },
        { "PasswordDictionary.txt", "SecretHeader.7z" },
        { "PasswordDictionary.txt", "SecretSplit.7z.001" },
        { "PasswordDictionary.txt", "SecretSplitHeader.7z.001" },
        { "PasswordDictionary.txt", "SecretSplitZip.zip.001" },
        { "PasswordDictionary.txt", "SecretSfx.exe" },
        { "PasswordDictionary.txt", "SecretSfxHeader.exe" },
        { "DictOK.txt", "Secret.7z" },          // 最小可行流程
        { "DictOK.txt", "SecretSfx.exe" },     // 单密码正确路径（曾触发 Write 回调崩溃）
    };

    [Theory, MemberData(nameof(PositiveCases))]
    public void FindsCorrectPassword(string dictionary, string archive)
    {
        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict(dictionary)}\" -F \"{fx.Archive(archive)}\"");

        Assert.Equal(0, ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void EmptyArchivePathFails()
    {
        // 缺少 -F 参数应走 FileNotFoundException 分支：exit 1
        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("DictOK.txt")}\"");

        Assert.Equal(1, ExitCode);
        Assert.DoesNotContain(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void AllWrongPasswordsReportNotFound()
    {
        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("Dict7.txt")}\" -F \"{fx.Archive("Secret.7z")}\"");

        Assert.Equal(0, ExitCode);
        Assert.DoesNotContain(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void AllWrongPasswordsReportNotFoundForSplitArchive()
    {
        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("Dict7.txt")}\" -F \"{fx.Archive("SecretSplit.7z.001")}\"");

        Assert.Equal(0, ExitCode);
        Assert.DoesNotContain(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void NonArchiveExeReportsUnrecognized()
    {
        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("DictOK.txt")}\" -F \"{fx.Archive("NotAnArchive.exe")}\"");

        Assert.Equal(1, ExitCode);
        Assert.DoesNotContain(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void SingleThreadGlobalStillFindsPassword()
    {
        // APTT_PARALLELISM=1 曾是并发崩溃的隔离开关，回归保护
        var startInfo = $"-D \"{fx.Dict("PasswordDictionary.txt")}\" -F \"{fx.Archive("Secret.7z")}\"";
        string envValue = Environment.GetEnvironmentVariable("APTT_PARALLELISM") ?? string.Empty;
        try
        {
            var (ExitCode, Output) = RunTool(startInfo, env: ("APTT_PARALLELISM", "1"));

            Assert.Equal(0, ExitCode);
            Assert.Contains(TestDataFixture.CorrectPassword, Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APTT_PARALLELISM", string.IsNullOrEmpty(envValue) ? null : envValue);
        }
    }
    /// <summary>
    /// 大字典（2100 行，跨批次 refill 边界）在默认饱和并行度下的端到端回归：
    /// 守护 Dictionary 并发 refill 正确性与并行测试全链路，正确密码位于末尾，
    /// 任何因竞态导致的跳行漏报都会让本用例失败
    /// </summary>
    [Fact]
    public void BigDictionaryFullSweepFindsPassword()
    {
        var result = RunTool($"-D \"{fx.Dict("BigDictionary.txt")}\" -F \"{fx.Archive("Secret.7z")}\"", timeoutMs: 300_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, result.Output);
    }

    /// <summary>
    /// 密码含格式占位符或 Spectre 标记字符时必须原样输出。
    /// 密码始终作为 <c>string.Format</c> 的**参数**（而非格式串）传入，且输出前经
    /// <c>Markup.Escape</c>：若将来有人把密码当格式串用，<c>{0}</c> 会被替换甚至抛
    /// FormatException；漏掉转义则 <c>[red]</c> 会被当成样式吞掉。
    /// </summary>
    [Theory]
    [InlineData("{0}")]
    [InlineData("{0}Pw{1}")]
    [InlineData("{{pw}}")]
    [InlineData("a{b}c")]
    [InlineData("[red]X[/]")]
    public void PasswordWithFormatOrMarkupCharactersIsReportedVerbatim(string password)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"ApttFormat{Guid.NewGuid():N}.7z");
        string dictPath = Path.ChangeExtension(archivePath, ".txt");

        try
        {
            CreateArchiveWithPassword(archivePath, password);
            File.WriteAllLines(dictPath, ["DefinitelyWrong", password]);

            var result = RunTool($"-D \"{dictPath}\" -F \"{archivePath}\"");

            Assert.Equal(0, result.ExitCode);
            // 原样出现既是「找到了密码」的证明，也证明它没被格式化或标记解析吞掉
            Assert.Contains(password, result.Output);
        }
        finally
        {
            File.Delete(archivePath);
            File.Delete(dictPath);
        }
    }

    /// <summary>用 7-Zip CLI 现场创建一个指定密码的压缩包（密码通过 ArgumentList 传递，不经 shell）</summary>
    private void CreateArchiveWithPassword(string archivePath, string password)
    {
        var startInfo = new ProcessStartInfo(fx.SevenZipExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("a");
        startInfo.ArgumentList.Add("-t7z");
        startInfo.ArgumentList.Add("-p" + password);
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add(fx.Archive("Payload.bin"));
        startInfo.ArgumentList.Add("-y");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"7z 创建压缩包失败（exit {process.ExitCode}）：\n{stdout}\n{stderr}");
    }

    /// <summary>
    /// GBK 编码的中文密码字典必须能正常破解。
    /// <para>
    /// 缺陷背景：字典编码曾硬定为 UTF-8，GBK 密码本（简中 Windows 记事本另存为 ANSI
    /// 的默认结果）整份读成乱码，密码就在字典里也必然报"未找到"。
    /// 本用例同时覆盖两件事：编码探测生效，以及重定向输出为 UTF-8（能断言回中文密码）。
    /// </para>
    /// </summary>
    [Fact]
    public void GbkDictionaryFindsChinesePassword()
    {
        const string Password = "中文密碼测试";

        string archivePath = Path.Combine(Path.GetTempPath(), $"ApttGbk{Guid.NewGuid():N}.7z");
        string dictPath = Path.ChangeExtension(archivePath, ".txt");

        try
        {
            CreateArchiveWithPassword(archivePath, Password);

            DictionaryEncoding.EnsureCodePagesRegistered();
            byte[] gbkContent = Encoding.GetEncoding(936).GetBytes($"WrongPassword\r\n{Password}\r\n");
            File.WriteAllBytes(dictPath, gbkContent);

            var result = RunTool($"-D \"{dictPath}\" -F \"{archivePath}\"");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(Password, result.Output);
        }
        finally
        {
            File.Delete(archivePath);
            File.Delete(dictPath);
        }
    }

    /// <summary>
    /// 多进程模式端到端：2100 行字典超过多进程阈值，4 个工作进程按批分发，
    /// 正确密码在末尾，验证分发/ACK/结果回传全链路
    /// </summary>
    [Fact]
    public void MultiProcessFindsPasswordInBigDictionary()
    {
        var result = RunTool(
            $"-D \"{fx.Dict("BigDictionary.txt")}\" -F \"{fx.Archive("Secret.7z")}\"",
            timeoutMs: 300_000, ("APTT_WORKERS", "4"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, result.Output);
    }

    /// <summary>
    /// 多进程模式+header 加密：工作进程按密码重开归档（Open 内 KDF 受同一全局锁），
    /// 多进程同样受益；正确密码在末尾
    /// </summary>
    [Fact]
    public void MultiProcessFindsHeaderEncryptedPassword()
    {
        string dictPath = Path.Combine(Path.GetTempPath(), $"ApttWorkerDict{Guid.NewGuid():N}.txt");
        try
        {
            var lines = new List<string>(300);
            for (int i = 1; i <= 299; i++)
            {
                lines.Add($"WrongWorker{i}");
            }

            lines.Add(TestDataFixture.CorrectPassword);
            File.WriteAllLines(dictPath, lines);

            var result = RunTool(
                $"-D \"{dictPath}\" -F \"{fx.Archive("SecretHeader.7z")}\"",
                timeoutMs: 300_000, ("APTT_WORKERS", "2"));

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(TestDataFixture.CorrectPassword, result.Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    /// <summary>
    /// 直接驱动工作进程协议：READY → 批回复（含制表符/引号/中文等特殊字符密码）→
    /// 正确密码回传 FOUND → STOP 优雅退出（守护管道协议与 JSON 转义）
    /// </summary>
    [Fact]
    public void WorkerProtocolRoundTrip()
    {
        var startInfo = new ProcessStartInfo(TestPaths.MainExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 与 RunTool 一致：程序重定向输出为 UTF-8
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-Worker");
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add(fx.Archive("Secret.7z"));

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            var stdin = process!.StandardInput;
            var stdout = process.StandardOutput;

            Assert.Equal("READY", stdout.ReadLine());

            var batch = new List<string>
        {
            "Wrong\tTab", "Wrong\"Quote", "Wrong中文", "Wrong[Bracket]",
            TestDataFixture.CorrectPassword,
        };

            stdin.WriteLine(JsonSerializer.Serialize(batch, WorkerJsonContext.Default.ListString));
            stdin.Flush();
            var reply = JsonSerializer.Deserialize(stdout.ReadLine()!, WorkerJsonContext.Default.WorkerBatchReply);
            Assert.NotNull(reply);
            Assert.Equal(5, reply!.Tested); // 制表符等 4 个错误密码 + 正确密码，批内 break
            Assert.Equal(TestDataFixture.CorrectPassword, reply.Found);

            // 协议约定：工作进程回复 FOUND 后立即退出，master 无需再发 STOP
            Assert.True(process.WaitForExit(15000), "工作进程未在限时内退出");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            KillQuietly(process!);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // 进程可能已自行退出
        }
    }

    // ---------- RAR 系（需 WinRAR，缺失时跳过） ----------

    [Fact]
    public void Rar5SingleFindsPassword()
    {
        if (!RequireWinRAR())
        {
            return;
        }

        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("PasswordDictionary.txt")}\" -F \"{fx.Archive("Rar5Single.rar")}\"");

        Assert.Equal(0, ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void Rar5SplitFindsPassword()
    {
        if (!RequireWinRAR())
        {
            return;
        }

        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("PasswordDictionary.txt")}\" -F \"{fx.Archive("Rar5Split.part1.rar")}\"");

        Assert.Equal(0, ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void Rar5HeaderEncryptedSplitFindsPassword()
    {
        if (!RequireWinRAR())
        {
            return;
        }

        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("PasswordDictionary.txt")}\" -F \"{fx.Archive("Rar5HeaderSplit.part1.rar")}\"");

        Assert.Equal(0, ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void Rar5SfxFindsPassword()
    {
        if (!RequireWinRAR())
        {
            return;
        }

        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("PasswordDictionary.txt")}\" -F \"{fx.Archive("RarSfx.exe")}\"");

        Assert.Equal(0, ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, Output);
    }

    [Fact]
    public void Rar5SplitWrongPasswordsReportNotFound()
    {
        if (!RequireWinRAR())
        {
            return;
        }

        var (ExitCode, Output) = RunTool($"-D \"{fx.Dict("Dict7.txt")}\" -F \"{fx.Archive("Rar5Split.part1.rar")}\"");

        Assert.Equal(0, ExitCode);
        Assert.DoesNotContain(TestDataFixture.CorrectPassword, Output);
    }

    // ---------- 工具方法 ----------

    [Fact]
    public void BareLaunchPromptsForPathsInteractively()
    {
        // 无任何参数启动：应进入 CLI 交互引导（旧行为恢复），
        // 通过 stdin 依次喂入密码本与压缩包路径后应正常完成测试。
        // 带部分参数启动则仍是纯 CLI 语义（EmptyArchivePathFails 已锁定 exit 1）
        var (ExitCode, Output) = RunToolWithStdin(fx.Dict("DictOK.txt"), fx.Archive("Secret.7z"));

        Assert.Equal(0, ExitCode);
        Assert.Contains(TestDataFixture.CorrectPassword, Output);
    }

    /// <summary>无参数启动主程序并经 stdin 依次喳入引导答案，返回退出码与全量输出</summary>
    private (int ExitCode, string Output) RunToolWithStdin(params string[] lines)
    {
        Assert.True(File.Exists(TestPaths.MainExePath), $"主程序不存在：{TestPaths.MainExePath}");

        var startInfo = new ProcessStartInfo(TestPaths.MainExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动主程序（无参数交互模式）");

        foreach (var line in lines)
        {
            process.StandardInput.WriteLine(line);
        }

        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("无参数交互模式执行超时");
        }

        var outputText = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();

        output.WriteLine("exit: " + process.ExitCode);
        output.WriteLine("stdout: " + outputText);

        return (process.ExitCode, outputText);
    }

    /// <summary>WinRAR 缺失时返回 false 并输出跳过原因</summary>
    private bool RequireWinRAR()
    {
        if (fx.HasWinRAR)
        {
            return true;
        }

        output.WriteLine("跳过：未安装 WinRAR，无法生成/验证 RAR 样本");
        return false;
    }

    private (int ExitCode, string Output) RunTool(string arguments, int timeoutMs = 120_000, params (string Key, string Value)[] env)
    {
        Assert.True(File.Exists(TestPaths.MainExePath), $"主程序不存在：{TestPaths.MainExePath}");

        var startInfo = new ProcessStartInfo(TestPaths.MainExePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 主程序在输出重定向时把 stdout/stderr 改为 UTF-8（见 Program.Main）：
            // 默认编码是系统 OEM 代码页，非中文界面（法语重音/俄语西里尔）会变 '?'
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var (key, value) in env)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动进程：{TestPaths.MainExePath} {arguments}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"主程序执行超时：{arguments}");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        output.WriteLine($"args: {arguments}");
        output.WriteLine($"exit: {process.ExitCode}");
        output.WriteLine($"stdout: {stdout}");
        output.WriteLine($"stderr: {stderr}");

        return (process.ExitCode, stdout + stderr);
    }
}
