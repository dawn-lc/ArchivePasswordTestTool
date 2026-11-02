using System.Diagnostics;
using System.Text;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// ZipCrypto（传统 zip 加密）+ Store 的快速判错。
/// <para>
/// 背景：ZipCrypto 的 12 字节加密头只校验 1 个字节（CRC 高字节），误判率 1/256，
/// 每个假阳性都要完整解压才能被 CRC32 否决——实测 200MB 单文件下吞吐被拖到
/// 1100 个/秒（WinZip AES 有 2 字节校验值，可达 3754 个/秒）。
/// 加密头之后是 Store 模式的明文，因此再解首块比对文件格式签名即可把误判率
/// 压到约 2^-32。
/// </para>
/// <para>
/// 样本用项目依赖的 7z.exe 生成（`-mem=ZipCrypto -mx=0`），不是手写合成——
/// 密码字节编码这类细节必须以真实实现为准。
/// </para>
/// </summary>
public sealed class ZipStoreProbeTests
{
    private const string CorrectPassword = "Correct123!";

    private static string SevenZipExe => Fixture.Value.SevenZipExe;

    private static readonly Lazy<TestDataFixture> Fixture = new(() => new TestDataFixture());

    /// <summary>正确密码必须命中，错误密码必须不命中</summary>
    [Fact]
    public void ProbeMatchesCorrectPasswordAndRejectsWrongOne()
    {
        using var sample = new ZipCryptoSample();

        var probe = ZipStoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal("MP4", probe!.ContentFormat);
        Assert.Equal(PasswordProbeResult.Match, probe.Probe(CorrectPassword));
        Assert.Equal(PasswordProbeResult.NoMatch, probe.Probe("WrongPassword123"));
    }

    /// <summary>
    /// 非 ASCII 密码的字节编码取决于实现（OEM/UTF-8/系统代码页），不做猜测：
    /// 必须返回 Unsupported 由调用方退回完整测密，绝不静默漏测
    /// </summary>
    [Theory]
    [InlineData("密码")]
    [InlineData("pässwort")]
    [InlineData("пароль")]
    [InlineData("a\u00A0b")]
    public void NonAsciiPasswordIsReportedUnsupported(string password)
    {
        using var sample = new ZipCryptoSample();

        var probe = ZipStoreProbe.TryCreate(sample.Path, sample.Info);

        Assert.NotNull(probe);
        Assert.Equal(PasswordProbeResult.Unsupported, probe!.Probe(password));
    }

    /// <summary>WinZip AES（method 99）有 2 字节校验值，不需要也不应该走这条路</summary>
    [Fact]
    public void AesEncryptedZipIsNotEligible()
    {
        using var sample = new ZipCryptoSample(aes: true);
        sample.Info.FileCount = 1;

        Assert.Null(ZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>非 zip 格式不应被误判</summary>
    [Fact]
    public void OtherFormatsAreNotEligible()
    {
        using var sample = new ZipCryptoSample();
        sample.Info.Type = SevenZipGuids.CLSID_CFormat7z;

        Assert.Null(ZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>未加密的包不需要判错器（IsEncrypted=false）</summary>
    [Fact]
    public void UnencryptedZipIsNotEligible()
    {
        using var sample = new ZipCryptoSample();
        sample.Info.IsEncrypted = false;

        Assert.Null(ZipStoreProbe.TryCreate(sample.Path, sample.Info));
    }

    /// <summary>
    /// 端到端：快速筛选命中后仍由 7-Zip 完整确认；关闭快速筛选也应当能找到密码。
    /// 后者还顺带保证筛选没有把正确答案排除掉。
    /// </summary>
    [Fact]
    public void EndToEndFindsPasswordWithAndWithoutFastFilter()
    {
        using var sample = new ZipCryptoSample();

        string dictPath = Path.ChangeExtension(sample.Path, ".txt");
        File.WriteAllLines(dictPath, ["wrong1", "wrong2", CorrectPassword, "wrong3"]);

        try
        {
            var (ExitCode, Output) = RunTool(dictPath, sample.Path);
            Assert.Equal(0, ExitCode);
            Assert.Contains(CorrectPassword, Output);
            Assert.Contains("快速密码筛选", Output);

            var slow = RunTool(dictPath, sample.Path, ("APTT_NO_FAST_FILTER", "1"));
            Assert.Equal(0, slow.ExitCode);
            Assert.Contains(CorrectPassword, slow.Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    /// <summary>负例：字典里没有正确密码时不能出现假阳性</summary>
    [Fact]
    public void EndToEndReportsNotFoundForAllWrongPasswords()
    {
        using var sample = new ZipCryptoSample();

        string dictPath = Path.ChangeExtension(sample.Path, ".txt");
        File.WriteAllLines(dictPath, ["wrong1", "wrong2", "wrong3"]);

        try
        {
            var (ExitCode, Output) = RunTool(dictPath, sample.Path);

            Assert.Equal(0, ExitCode);
            Assert.DoesNotContain(CorrectPassword, Output);
        }
        finally
        {
            File.Delete(dictPath);
        }
    }

    private static (int ExitCode, string Output) RunTool(string dictPath, string archivePath, params (string Name, string Value)[] env)
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

        startInfo.Environment["APTT_NO_UPGRADE_CHECK"] = "1";

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>用 7z.exe 生成的 ZipCrypto/Store 样本（MP4 开头，使签名可判定）</summary>
    private sealed class ZipCryptoSample : IDisposable
    {
        private readonly string _payloadPath;

        public ZipCryptoSample(bool aes = false)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ApttZip{Guid.NewGuid():N}.zip");
            _payloadPath = System.IO.Path.ChangeExtension(Path, ".mp4");

            File.WriteAllBytes(_payloadPath, CreateMp4LikePlaintext());

            var startInfo = new ProcessStartInfo(SevenZipExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string arg in new[]
                     {
                         "a", "-tzip", "-mx=0",
                         aes ? "-mem=AES256" : "-mem=ZipCrypto",
                         "-p" + CorrectPassword, "-y", Path, _payloadPath,
                     })
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            string stdout = process!.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"7z 创建 zip 失败（exit {process.ExitCode}）：\n{stdout}\n{stderr}");
        }

        public string Path { get; }

        public ArchiveInfo Info { get; } = new()
        {
            Type = SevenZipGuids.CLSID_CFormatZip,
            FileCount = 1,
            IsEncrypted = true,
            IsHeaderEncryption = false,
            SupportsQuickTest = false,
        };

        private static byte[] CreateMp4LikePlaintext()
        {
            byte[] plaintext = new byte[4096];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(plaintext, 0x18);
            Encoding.ASCII.GetBytes("ftyp").CopyTo(plaintext, 4);
            Encoding.ASCII.GetBytes("isom").CopyTo(plaintext, 8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(plaintext.AsSpan(12), 0x200);
            Encoding.ASCII.GetBytes("isomiso2").CopyTo(plaintext, 16);
            for (int i = 24; i < plaintext.Length; i++)
            {
                plaintext[i] = (byte)(i * 7 + 3);
            }

            return plaintext;
        }

        public void Dispose()
        {
            foreach (string path in new[] { Path, _payloadPath })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 临时文件清理失败不影响断言
                }
            }
        }
    }
}
