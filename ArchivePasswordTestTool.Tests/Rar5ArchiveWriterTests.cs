using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ArchivePasswordTestTool.Tests.Infrastructure;
using static ArchivePasswordTestTool.ArchiveFormatDetector;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 合成 RAR5 写入器（<see cref="Rar5ArchiveWriter"/>）的结构与相容性测试。
/// <para>
/// 这类测试的目的是**取代 WinRAR 依赖**：在没有任何专有工具的 CI 上，也能得到已知密码、
/// 已知结构、逐字节可重复的 RAR5 样本。验收标准只有一条最硬的：7-Zip 必须能解开自产归档，
/// 否则说明对 RAR5 结构的理解有偏差。在此基础上再加两层：
/// ① 结构层——按技术说明重新解析自产归档，断言 headerSize 语义与 Header CRC32 覆盖范围；
/// ② 行为层——正确密码能解开、错误密码必须被拒（这是主程序判定「没找到密码」的前提）。
/// </para>
/// </summary>
[Collection(TestDataCollection.Name)]
public sealed class Rar5ArchiveWriterTests(TestDataFixture fx, ITestOutputHelper output)
{
    private const string CorrectPassword = "Correct123!";
    private const string WrongPassword = "WrongPassword";
    private const string EntryName = "Payload.bin";

    private static readonly byte[] DataSalt =
        [0x6B, 0x24, 0x80, 0x07, 0xF1, 0xE7, 0x71, 0x3F, 0x3D, 0xCE, 0xBE, 0x8B, 0xFE, 0xD4, 0x52, 0x49];

    private static readonly byte[] DataIv =
        [0xA4, 0x2D, 0x69, 0x89, 0xE2, 0x76, 0x92, 0xBC, 0xAF, 0x9B, 0x94, 0xCA, 0xA1, 0xE4, 0xA1, 0x34];

    private static readonly byte[] HeaderSalt =
        [0x53, 0xF9, 0xB2, 0x69, 0xD8, 0x43, 0xF5, 0x43, 0xB4, 0xF4, 0xD5, 0x4D, 0xF8, 0xCF, 0xFF, 0xD2];

    private static byte[] CreatePlaintext(int length = 51200)
    {
        var plaintext = new byte[length];
        new Random(20240102).NextBytes(plaintext);
        return plaintext;
    }

    private string NewTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ApttRar5{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        return directory;
    }

    // ---------- 相容性：7-Zip 必须认得自产归档 ----------

    /// <summary>
    /// 单卷 + 仅数据加密 + 存储模式：正确密码可解，错误密码必须被拒。
    /// 这一条同时锁定了 KDF 参数（UTF-8 密码 + PBKDF2-SHA256 + 2^kdfCount）、
    /// AES-256-CBC 无填充、以及文件加密记录的字段顺序。
    /// </summary>
    [Fact]
    public void SingleVolumeStoredArchiveRoundTripsThroughSevenZip()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "Single.rar");
            byte[] plaintext = CreatePlaintext();
            _ = Rar5ArchiveWriter.CreateStoredEncrypted(path, EntryName, plaintext, CorrectPassword, DataSalt, DataIv);

            Assert.Equal(0, RunSevenZip($"t -y -p{CorrectPassword} \"{path}\""));

            int wrongExit = RunSevenZip($"t -y -p{WrongPassword} \"{path}\"");
            Assert.NotEqual(0, wrongExit);
            output.WriteLine($"错误密码退出码 = {wrongExit}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 头部加密：不带密码连列表都读不出来，带正确密码则一切正常。
    /// 这条锁定了「每个头 = 16 字节 IV + CRC 也一起加密的 16 字节对齐密文块」这一封装方式。
    /// </summary>
    [Fact]
    public void HeaderEncryptedArchiveRequiresCorrectPasswordToOpen()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "Header.rar");
            byte[] plaintext = CreatePlaintext();
            _ = Rar5ArchiveWriter.CreateHeaderEncrypted(
                path, EntryName, plaintext, CorrectPassword, DataSalt, DataIv, HeaderSalt);

            Assert.NotEqual(0, RunSevenZip($"l -y -p{WrongPassword} \"{path}\""));
            Assert.Equal(0, RunSevenZip($"l -y -p{CorrectPassword} \"{path}\""));

            // 解出来的内容必须与明文一致：用 7z 的 x 输出到临时目录再比对
            string extractDirectory = Path.Combine(directory, "out");
            Assert.Equal(0, RunSevenZip($"x -y -o\"{extractDirectory}\" -p{CorrectPassword} \"{path}\""));
            Assert.Equal(plaintext, File.ReadAllBytes(Path.Combine(extractDirectory, EntryName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 分卷 + 仅数据加密：首卷即可被 7-Zip 依次串起后续卷解开。
    /// </summary>
    [Fact]
    public void SplitVolumesAreLinkedAndTestable()
    {
        string directory = NewTempDirectory();
        try
        {
            string basePath = Path.Combine(directory, "Split.rar");
            byte[] plaintext = CreatePlaintext();
            IReadOnlyList<string> volumes = Rar5ArchiveWriter.Create(basePath, new Rar5WriterOptions
            {
                EntryName = EntryName,
                Plaintext = plaintext,
                Password = CorrectPassword,
                DataSalt = DataSalt,
                DataIv = DataIv,
                VolumeCount = 3,
            });

            Assert.Equal(3, volumes.Count);
            Assert.All(volumes, v => Assert.True(File.Exists(v), $"缺少分卷 {v}"));

            string extractDirectory = Path.Combine(directory, "out");
            Assert.Equal(0, RunSevenZip($"x -y -o\"{extractDirectory}\" -p{CorrectPassword} \"{volumes[0]}\""));
            Assert.Equal(plaintext, File.ReadAllBytes(Path.Combine(extractDirectory, EntryName)));

            Assert.NotEqual(0, RunSevenZip($"t -y -p{WrongPassword} \"{volumes[0]}\""));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 分卷 + 头部加密：每个分卷都要独立带归档加密头（7-Zip 逐卷解密头部）。
    /// </summary>
    [Fact]
    public void HeaderEncryptedSplitVolumesAreLinkedAndTestable()
    {
        string directory = NewTempDirectory();
        try
        {
            string basePath = Path.Combine(directory, "HeaderSplit.rar");
            byte[] plaintext = CreatePlaintext();
            IReadOnlyList<string> volumes = Rar5ArchiveWriter.Create(basePath, new Rar5WriterOptions
            {
                EntryName = EntryName,
                Plaintext = plaintext,
                Password = CorrectPassword,
                DataSalt = DataSalt,
                DataIv = DataIv,
                EncryptHeaders = true,
                HeaderSalt = HeaderSalt,
                VolumeCount = 3,
            });

            string extractDirectory = Path.Combine(directory, "out");
            Assert.Equal(0, RunSevenZip($"x -y -o\"{extractDirectory}\" -p{CorrectPassword} \"{volumes[0]}\""));
            Assert.Equal(plaintext, File.ReadAllBytes(Path.Combine(extractDirectory, EntryName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// SFX：签名之前插入 MZ 桩，格式检测必须报 RAR5 且偏移大于 0。
    /// </summary>
    [Fact]
    public void SfxStubKeepsRar5SignatureScannable()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "Sfx.exe");
            var stub = new byte[4096];
            stub[0] = (byte)'M';
            stub[1] = (byte)'Z';

            _ = Rar5ArchiveWriter.Create(path, new Rar5WriterOptions
            {
                EntryName = EntryName,
                Plaintext = CreatePlaintext(4096),
                Password = CorrectPassword,
                DataSalt = DataSalt,
                DataIv = DataIv,
                SfxStub = stub,
            });

            var (format, offset) = DetectArchiveFormat(path);

            Assert.Equal(SevenZipGuids.CLSID_CFormatRar5, format);
            Assert.Equal(stub.Length, offset);
            Assert.Equal(0, RunSevenZip($"t -y -p{CorrectPassword} \"{path}\""));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- 结构层：把技术说明里两条易错规则钉成断言 ----------

    /// <summary>
    /// 用 <see cref="Rar5HeaderReader"/> 重新解析自产归档，逐条核对：
    /// ① headerSize 从 type 字段开始计数（不含 headerSize 自身）；
    /// ② Header CRC32 覆盖 [headerSize 字段 .. extra area 末尾]。
    /// 这两条当初都是靠真实 WinRAR 样本标定出来的，理解错任何一条 7-Zip 都会拒绝整个归档，
    /// 而 7-Zip 的报错只会说「无法打开」，不会告诉你哪一条错了。
    /// </summary>
    [Fact]
    public void HeaderSizeAndCrcCoverageMatchDocumentedSemantics()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "Single.rar");
            byte[] plaintext = CreatePlaintext(1024);
            _ = Rar5ArchiveWriter.CreateStoredEncrypted(path, EntryName, plaintext, CorrectPassword, DataSalt, DataIv);

            byte[] bytes = File.ReadAllBytes(path);
            IReadOnlyList<Rar5Header> headers = Rar5HeaderReader.Read(bytes);

            Assert.Equal([Rar5HeaderReader.TypeMain, Rar5HeaderReader.TypeFile, Rar5HeaderReader.TypeEndOfArchive], headers.Select(h => h.Type));

            // CRC 自洽是「覆盖范围理解正确」的最直接证据：范围错一个字节就算不出同样的值
            Assert.All(headers, header => Assert.True(Rar5HeaderReader.CrcMatches(bytes, header), $"头部 @0x{header.Offset:X4} 的 CRC 覆盖范围不匹配"));

            Rar5Header file = headers.Single(h => h.Type == Rar5HeaderReader.TypeFile);
            Assert.Equal((ulong)plaintext.Length, file.UnpackedSize);
            Assert.Equal(EntryName, file.Name);
            Assert.True(file.HasFlag(Rar5HeaderReader.FlagExtraArea));
            Assert.True(file.HasFlag(Rar5HeaderReader.FlagDataArea));

            // 头部正文恰好填满 headerSize（不多不少），是「headerSize 不含自身」的等价表述
            Assert.All(headers, header => Assert.Equal((int)header.HeaderSize, header.BodyEnd - header.BodyStart));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 同样的字节必须每次一致：样本可重复是「快速可靠的测试」的前提。
    /// </summary>
    [Fact]
    public void ArchiveBytesAreReproducible()
    {
        string directory = NewTempDirectory();
        try
        {
            string first = Path.Combine(directory, "First.rar");
            string second = Path.Combine(directory, "Second.rar");
            byte[] plaintext = CreatePlaintext(2048);

            _ = Rar5ArchiveWriter.CreateStoredEncrypted(first, EntryName, plaintext, CorrectPassword, DataSalt, DataIv);
            _ = Rar5ArchiveWriter.CreateStoredEncrypted(second, EntryName, plaintext, CorrectPassword, DataSalt, DataIv);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- 工具 ----------

    private int RunSevenZip(string arguments)
    {
        var startInfo = new ProcessStartInfo(fx.SevenZipExe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        output.WriteLine($"7z {arguments} → exit={process.ExitCode}{Environment.NewLine}{stdout}{stderr}");
        return process.ExitCode;
    }
}
