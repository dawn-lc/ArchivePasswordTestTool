using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 7z「存储模式 + 单文件」的快速判错。
/// <para>
/// 背景：7z 的加密头里没有密码校验值，Copy（不压缩）编码的密文也没有任何可自校验结构，
/// 错误密码必须解到文件尾靠 CRC 判错。实测 100MB 单文件 64 个/秒，且加线程与加进程
/// 都无效（瓶颈是内存带宽）。本探测器自己派生 7zAES 密钥、只解首块比对格式签名。
/// </para>
/// <para>
/// 样本一律用 7z.exe 真实生成（不用手写合成）：7zAES 的 coder properties 布局、
/// 密码编码、CBC 补齐这些细节必须以真实实现为准——开发时凭记忆写的 24 种参数组合全错，
/// 对照 7-Zip 源码后才解出明文。这类偏差只有「真实样本 + 7-Zip 能解开」才能兜住。
/// </para>
/// </summary>
public sealed class SevenZipStoreProbeTests
{
    private const string CorrectPassword = "Correct123!";

    private static readonly Lazy<TestDataFixture> Fixture = new(() => new TestDataFixture());

    private static string SevenZipExe => Fixture.Value.SevenZipExe;

    /// <summary>正确密码必须命中，错误密码必须不命中</summary>
    [Fact]
    public void ProbeMatchesCorrectPasswordAndRejectsWrongOne()
    {
        using var sample = new SevenZipStoreSample(4096);

        var probe = SevenZipStoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal("MP4", probe!.ContentFormat);
        Assert.Equal("Sample.mp4", probe.EntryName);
        Assert.Equal(PasswordProbeResult.Match, probe.Probe(CorrectPassword));
        Assert.Equal(PasswordProbeResult.NoMatch, probe.Probe("WrongPassword123"));
    }

    /// <summary>
    /// 密码字节是 UTF-16LE（7-Zip 内部就是宽字符串）。非 ASCII 密码必须同样命中——
    /// 若这里失败，说明编码假设错了，而错误假设会静默漏掉正确答案。
    /// </summary>
    [Theory]
    [InlineData("密码123")]
    [InlineData("pässwort")]
    [InlineData("пароль")]
    public void NonAsciiPasswordMatches(string password)
    {
        using var sample = new SevenZipStoreSample(1024, password);

        var probe = SevenZipStoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal(PasswordProbeResult.Match, probe!.Probe(password));
        Assert.Equal(PasswordProbeResult.NoMatch, probe.Probe(password + "x"));
    }

    /// <summary>明文大小不是 16 的倍数时，打包流会被补齐——必须仍能定位首块</summary>
    [Theory]
    [InlineData(25)]
    [InlineData(31)]
    [InlineData(1000)]
    [InlineData(4097)]
    public void NonBlockAlignedSizeIsSupported(int size)
    {
        using var sample = new SevenZipStoreSample(size);

        var probe = SevenZipStoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal(PasswordProbeResult.Match, probe!.Probe(CorrectPassword));
    }

    /// <summary>
    /// 小于一个 AES 块的载荷：首块几乎全是 CBC 补齐的零字节，不可能命中 MP4 签名。
    /// 这里断言的是「不崩、且如实返回 NoMatch」，而不是把它硬判成候选。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void PayloadSmallerThanBlockIsRejectedWithoutMatching(int size)
    {
        using var sample = new SevenZipStoreSample(size);

        var probe = SevenZipStoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal(PasswordProbeResult.NoMatch, probe!.Probe(CorrectPassword));
    }

    /// <summary>压缩编码（LZMA）的密文有内部结构，7-Zip 首块即判错，不需要也不应该走这条路</summary>
    [Fact]
    public void CompressedArchiveIsNotEligible()
    {
        using var sample = new SevenZipStoreSample(4096, method: "-mx=5");

        Assert.Null(SevenZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>header 加密时 Open 阶段就验证密码，快速判错没有意义</summary>
    [Fact]
    public void HeaderEncryptedArchiveIsNotEligible()
    {
        using var sample = new SevenZipStoreSample(4096, extraArguments: ["-mhe=on"]);

        Assert.Null(SevenZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>多文件时首个文件的格式不代表后续文件，保守关闭</summary>
    [Fact]
    public void MultiFileArchiveIsNotEligible()
    {
        using var sample = new SevenZipStoreSample(4096, additionalFile: true);

        // 多文件 -mx=0 的头部会被 LZMA 编码，解析阶段就会拒绝
        Assert.Null(SevenZipStoreProbe.TryCreate(sample.Path, sample.Info));

        // 即使调用方谎报只有 1 个文件，头部结构也会露馅
        sample.Info.FileCount = 1;
        Assert.Null(SevenZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>扩展名不在签名表内时无法判断「解密结果像不像真实文件」，必须放弃</summary>
    [Fact]
    public void UnknownExtensionIsNotEligible()
    {
        using var sample = new SevenZipStoreSample(4096, entryName: "Sample.unknownext");

        Assert.Null(SevenZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>非 7z 格式不应被误判</summary>
    [Fact]
    public void OtherFormatsAreNotEligible()
    {
        using var sample = new SevenZipStoreSample(4096);
        sample.Info.Type = SevenZipGuids.CLSID_CFormatRar;

        Assert.Null(SevenZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>未加密的包不需要判错器</summary>
    [Fact]
    public void UnencryptedArchiveIsNotEligible()
    {
        using var sample = new SevenZipStoreSample(4096);
        sample.Info.IsEncrypted = false;

        Assert.Null(SevenZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>
    /// 端到端：快速筛选命中后仍由 7-Zip 完整确认。
    /// 关闭快速筛选的那一次同时反向验证了 KDF —— 密钥算错就解不开真实 7z 存档。
    /// </summary>
    [Fact]
    public void EndToEndFindsPasswordWithAndWithoutFastFilter()
    {
        using var sample = new SevenZipStoreSample(64 * 1024);

        string dictPath = Path.ChangeExtension(sample.Path, ".txt");
        File.WriteAllLines(dictPath, ["wrong1", "wrong2", CorrectPassword, "wrong3"]);

        try
        {
            var fast = RunTool(dictPath, sample.Path);
            Assert.Equal(0, fast.ExitCode);
            Assert.Contains(CorrectPassword, fast.Output);

            // 断言快筛提示已出现：用格式名（ASCII）而不是中文文案——
            // FastFilterActive 在所有语言下都含 {0}（内容格式名），这个断言才能跨 UI 语言成立
            Assert.Contains("MP4", fast.Output);

            var slow = RunTool(dictPath, sample.Path, ("APTT_NO_FAST_FILTER", "1"));
            Assert.Equal(0, slow.ExitCode);
            Assert.Contains(CorrectPassword, slow.Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    /// <summary>负例：字典里没有正确密码时，快速筛选必须报告未找到（不能出现假阳性）</summary>
    [Fact]
    public void EndToEndReportsNotFoundForAllWrongPasswords()
    {
        using var sample = new SevenZipStoreSample(64 * 1024);

        string dictPath = Path.ChangeExtension(sample.Path, ".txt");
        File.WriteAllLines(dictPath, ["wrong1", "wrong2", "wrong3"]);

        try
        {
            var result = RunTool(dictPath, sample.Path);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(CorrectPassword, result.Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    private static (int ExitCode, string Output) RunTool(
        string dictPath,
        string archivePath,
        params (string Name, string Value)[] env)
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
        startInfo.ArgumentList.Add(dictPath);
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-NoQuery");
        startInfo.ArgumentList.Add("-NoReport");

        foreach (var (name, value) in env)
        {
            startInfo.Environment[name] = value;
        }

        // 关闭后台更新检查：测试不该依赖网络
        startInfo.Environment["APTT_NO_UPGRADE_CHECK"] = "1";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>用 7z.exe 生成的真实样本 + 与之匹配的 <see cref="ArchiveInfo"/></summary>
    private sealed class SevenZipStoreSample : IDisposable
    {
        private readonly string _payloadPath;
        private readonly string _directory;

        public SevenZipStoreSample(
            int payloadSize,
            string password = CorrectPassword,
            string method = "-mx=0",
            string entryName = "Sample.mp4",
            string[]? extraArguments = null,
            bool additionalFile = false)
        {
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"Aptt7z{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);

            _payloadPath = System.IO.Path.Combine(_directory, entryName);
            File.WriteAllBytes(_payloadPath, CreateMp4LikePlaintext(payloadSize));

            Path = System.IO.Path.Combine(_directory, "Sample.7z");

            var arguments = new List<string>
            {
                "a", "-t7z", method, "-p" + password, "-y", Path, _payloadPath,
            };

            if (extraArguments is not null)
            {
                arguments.AddRange(extraArguments);
            }

            if (additionalFile)
            {
                string second = System.IO.Path.Combine(_directory, "Extra.bin");
                File.WriteAllBytes(second, CreateMp4LikePlaintext(1024));
                arguments.Add(second);
            }

            RunSevenZip(arguments);
        }

        public string Path { get; }

        public ArchiveInfo Info { get; } = new()
        {
            Type = SevenZipGuids.CLSID_CFormat7z,
            FileCount = 1,
            IsEncrypted = true,
            IsHeaderEncryption = false,
            SupportsQuickTest = false,
        };

        /// <summary>以合法 MP4 首盒开头、其余为确定性填充的明文</summary>
        private static byte[] CreateMp4LikePlaintext(int length)
        {
            byte[] plaintext = new byte[length];
            if (length >= 24)
            {
                BinaryPrimitives.WriteUInt32BigEndian(plaintext, 0x18);
                Encoding.ASCII.GetBytes("ftyp").CopyTo(plaintext, 4);
                Encoding.ASCII.GetBytes("isom").CopyTo(plaintext, 8);
                BinaryPrimitives.WriteUInt32BigEndian(plaintext.AsSpan(12), 0x200);
                Encoding.ASCII.GetBytes("isomiso2").CopyTo(plaintext, 16);
            }

            for (int i = 24; i < plaintext.Length; i++)
            {
                plaintext[i] = (byte)(i * 7 + 3);
            }

            return plaintext;
        }

        private static void RunSevenZip(List<string> arguments)
        {
            var startInfo = new ProcessStartInfo(SevenZipExe)
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

            Assert.True(process.ExitCode == 0, $"7z 创建样本失败（exit {process.ExitCode}）：\n{stdout}\n{stderr}");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // 临时样本清理失败不影响断言
            }
        }
    }
}
