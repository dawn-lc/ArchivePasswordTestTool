using System.Diagnostics;
using ArchivePasswordTestTool.Tests.Infrastructure;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 「用本地 WinRAR 当对照源」的对照测试：拿真实 <c>Rar.exe</c> 生成一份同参数归档，
/// 与 <see cref="Rar5ArchiveWriter"/> 的产物做结构对照。
/// <para>
/// 为什么需要它：合成写入器的正确性判据有两层。第一层是「7-Zip 能打开」，但它只能证明没写坏，
/// 不能证明写对了——某个字段位置偏差只要不致命，7-Zip 往往会容忍。第二层就是本测试：
/// 把同一份载荷分别交给参考实现和自产代码，再用<b>同一个</b> <see cref="Rar5HeaderReader"/> 解析两边，
/// 要求语义字段逐项一致、Header CRC32 全部自洽。参考实现本身就是格式的权威定义，
/// 所以这一层能抓到「7-Zip 恰好容忍了」的偏差。
/// </para>
/// <para>
/// 门控：只在装有 WinRAR 的机器上真正执行（<c>C:\Program Files\WinRAR\Rar.exe</c>、
/// x86 目录或环境变量 <c>APTT_WINRAR</c>）。CI 上没有 WinRAR，这条会自动变成空跑并打印原因——
/// 它是<b>开发期</b>的对照手段，不是 CI 门禁；CI 门禁是不依赖专有软件的
/// <see cref="Rar5ArchiveWriterTests"/>。
/// </para>
/// <para>
/// 修改 <see cref="Rar5ArchiveWriter"/> 的头部布局、加密记录、分卷规则时，务必在装有 WinRAR 的机器上跑一次本测试。
/// </para>
/// </summary>
public sealed class Rar5ReferenceCompareTests(ITestOutputHelper output)
{
    private const string Password = "Correct123!";
    private const string EntryName = "Payload.bin";
    private const int PayloadLength = 20000;

    /// <summary>环境变量：显式指定 Rar.exe 路径（装到非默认目录时用）</summary>
    private const string WinRarPathVariable = "APTT_WINRAR";

    /// <summary>
    /// 同参数（RAR5 + 存储 + 密码）下，合成归档的语义字段必须与真实 WinRAR 归档一致；
    /// 两者的差异只允许是<b>刻意省略</b>的可选字段。
    /// </summary>
    [Fact]
    public void SyntheticArchiveMatchesRealWinRarStructure()
    {
        string? rarExe = FindWinRarExe();
        if (rarExe is null)
        {
            output.WriteLine($"本机未找到 WinRAR（默认目录或 {WinRarPathVariable}），对照未执行；CI 上属正常，装有 WinRAR 时应通过。");
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"ApttRar5Compare{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);

        try
        {
            byte[] plaintext = CreatePlaintext();
            string payloadPath = Path.Combine(directory, EntryName);
            File.WriteAllBytes(payloadPath, plaintext);

            // 参考实现：RAR5 + 存储（-m0）+ 仅数据加密（-p）
            string realPath = Path.Combine(directory, "Reference.rar");
            int createExit = RunRar(rarExe, $"a -ma5 -m0 -idq -y -p{Password} \"{Path.GetFileName(realPath)}\" \"{EntryName}\"", directory);
            Assert.Equal(0, createExit);
            Assert.True(File.Exists(realPath), "Rar.exe 未产出参考归档");

            // 自产：同样的载荷、同样的模式
            string syntheticPath = Path.Combine(directory, "Synthetic.rar");
            _ = Rar5ArchiveWriter.CreateStoredEncrypted(
                syntheticPath,
                EntryName,
                plaintext,
                Password,
                salt: [0x6B, 0x24, 0x80, 0x07, 0xF1, 0xE7, 0x71, 0x3F, 0x3D, 0xCE, 0xBE, 0x8B, 0xFE, 0xD4, 0x52, 0x49],
                iv: [0xA4, 0x2D, 0x69, 0x89, 0xE2, 0x76, 0x92, 0xBC, 0xAF, 0x9B, 0x94, 0xCA, 0xA1, 0xE4, 0xA1, 0x34]);

            byte[] realBytes = File.ReadAllBytes(realPath);
            byte[] syntheticBytes = File.ReadAllBytes(syntheticPath);

            IReadOnlyList<Rar5Header> realHeaders = Rar5HeaderReader.Read(realBytes);
            IReadOnlyList<Rar5Header> syntheticHeaders = Rar5HeaderReader.Read(syntheticBytes);

            // ① 解析器必须对真实归档同样自洽 —— 这条同时反证了 headerSize 语义与 CRC 覆盖范围的理解
            Assert.All(realHeaders, header => Assert.True(
                Rar5HeaderReader.CrcMatches(realBytes, header),
                $"参考归档的头部 @0x{header.Offset:X4} CRC 不匹配，说明解析规则与真实格式不符"));

            Assert.All(syntheticHeaders, header => Assert.True(
                Rar5HeaderReader.CrcMatches(syntheticBytes, header),
                $"合成归档的头部 @0x{header.Offset:X4} CRC 不匹配"));

            // ② 头部类型序列一致。忽略 Service：真实 WinRAR 会额外写 QO（快速打开）等服务头，合成归档不需要
            var realStructure = realHeaders.Where(h => h.Type != Rar5HeaderReader.TypeService).Select(h => h.Type).ToList();
            var syntheticStructure = syntheticHeaders.Where(h => h.Type != Rar5HeaderReader.TypeService).Select(h => h.Type).ToList();

            Assert.Equal(syntheticStructure, realStructure);
            Assert.Equal(
                [Rar5HeaderReader.TypeMain, Rar5HeaderReader.TypeFile, Rar5HeaderReader.TypeEndOfArchive],
                syntheticStructure);

            // ③ 主头
            Rar5Header realMainHeader = realHeaders.First(h => h.Type == Rar5HeaderReader.TypeMain);
            Rar5Header syntheticMainHeader = syntheticHeaders.First(h => h.Type == Rar5HeaderReader.TypeMain);

            Assert.Equal(realMainHeader.ArchiveFlags, syntheticMainHeader.ArchiveFlags);
            Assert.Null(syntheticMainHeader.VolumeNumber);

            // ④ 文件头：语义字段逐项对齐
            Rar5Header realFile = realHeaders.First(h => h.Type == Rar5HeaderReader.TypeFile);
            Rar5Header syntheticFile = syntheticHeaders.First(h => h.Type == Rar5HeaderReader.TypeFile);

            Assert.Equal(realFile.Name, syntheticFile.Name);
            Assert.Equal(realFile.UnpackedSize, syntheticFile.UnpackedSize);
            Assert.Equal((ulong)plaintext.Length, syntheticFile.UnpackedSize);
            Assert.Equal(realFile.Attributes, syntheticFile.Attributes);
            Assert.Equal(realFile.CompressionInfo, syntheticFile.CompressionInfo);
            Assert.Equal(realFile.HostOs, syntheticFile.HostOs);

            Assert.True(realFile.HasFileFlag(Rar5HeaderReader.FileFlagCrc32), "参考归档的文件头应带 Data CRC32");
            Assert.True(syntheticFile.HasFileFlag(Rar5HeaderReader.FileFlagCrc32), "合成归档的文件头应带 Data CRC32");

            Assert.True(realFile.HasFlag(Rar5HeaderReader.FlagExtraArea));
            Assert.True(syntheticFile.HasFlag(Rar5HeaderReader.FlagExtraArea));
            Assert.True(realFile.HasFlag(Rar5HeaderReader.FlagDataArea));
            Assert.True(syntheticFile.HasFlag(Rar5HeaderReader.FlagDataArea));

            // ⑤ 文件加密记录：算法与长度一致
            Rar5EncryptionRecord realEncryption = Assert.IsType<Rar5EncryptionRecord>(realFile.FileEncryption);
            Rar5EncryptionRecord syntheticEncryption = Assert.IsType<Rar5EncryptionRecord>(syntheticFile.FileEncryption);

            Assert.Equal(realEncryption.Version, syntheticEncryption.Version);
            Assert.Equal(16, realEncryption.Salt.Length);
            Assert.Equal(16, syntheticEncryption.Salt.Length);
            Assert.Equal(16, realEncryption.Iv.Length);
            Assert.Equal(16, syntheticEncryption.Iv.Length);

            // KDF 强度：合成归档固定用 WinRAR 的常见默认值；参考实现的默认值随版本可能变，只做合理范围检查
            Assert.Equal(Rar5ArchiveWriter.DefaultKdfCount, syntheticEncryption.KdfCount);
            Assert.InRange(realEncryption.KdfCount, 1, 24);

            // ⑥ Data CRC32 的取值规则取决于 tweaked checksums：
            //    置位时 CRC 是按密钥变换后的值（等于明文 CRC 反倒说明理解错了），未置位时就是普通 CRC32。
            //    合成归档不置位，所以必须等于明文 CRC32；参考实现通常置位，因此通常不等于。
            uint plaintextCrc = Crc32.Compute(plaintext);

            Assert.False(syntheticEncryption.HasTweakedChecksums, "合成归档刻意不用 tweaked checksums");
            Assert.Equal(plaintextCrc, syntheticFile.DataCrc);

            if (realEncryption.HasTweakedChecksums)
            {
                Assert.NotEqual(plaintextCrc, realFile.DataCrc);
            }
            else
            {
                Assert.Equal(plaintextCrc, realFile.DataCrc);
            }

            // ⑦ 差异必须只剩「刻意省略」这一条：
            //    参考实现置位 PswCheck（写 12 字节校验值）并额外用 tweaked checksums；
            //    合成归档两者都不置位（算法未公开，技术说明标为可选），由 7-Zip/WinRAR 靠 CRC 判错。
            Assert.True(realEncryption.HasPasswordCheck, "参考归档应带 PswCheck");
            Assert.False(syntheticEncryption.HasPasswordCheck, "合成归档刻意不写 PswCheck");
            Assert.Equal(0UL, syntheticEncryption.Flags);

            // ⑧ 参考实现必须能解开合成归档，且内容逐字节一致
            string extractDirectory = Path.Combine(directory, "extracted");
            _ = Directory.CreateDirectory(extractDirectory);
            int extractExit = RunRar(rarExe, $"x -y -idq -p{Password} \"{syntheticPath}\"", extractDirectory);
            Assert.Equal(0, extractExit);
            Assert.Equal(plaintext, File.ReadAllBytes(Path.Combine(extractDirectory, EntryName)));

            // 对照摘要：把两边的关键字段摊平打出来，将来复核时不用重新造样本
            output.WriteLine("字段                  参考(WinRAR)        合成");
            output.WriteLine($"头部类型序列          {string.Join("/", realStructure)}   {string.Join("/", syntheticStructure)}");
            output.WriteLine($"主头 ArchiveFlags     0x{realMainHeader.ArchiveFlags:X}                0x{syntheticMainHeader.ArchiveFlags:X}");
            output.WriteLine($"文件头 FileFlags      0x{realFile.FileFlags:X}                0x{syntheticFile.FileFlags:X}");
            output.WriteLine($"UnpackedSize          {realFile.UnpackedSize}               {syntheticFile.UnpackedSize}");
            output.WriteLine($"Attributes            0x{realFile.Attributes:X}               0x{syntheticFile.Attributes:X}");
            output.WriteLine($"CompressionInfo       0x{realFile.CompressionInfo:X}                0x{syntheticFile.CompressionInfo:X}");
            output.WriteLine($"HostOS                {realFile.HostOs}                  {syntheticFile.HostOs}");
            output.WriteLine($"加密记录 Version       {realEncryption.Version}                  {syntheticEncryption.Version}");
            output.WriteLine($"加密记录 Flags         0x{realEncryption.Flags:X}                0x{syntheticEncryption.Flags:X}");
            output.WriteLine($"KDFCount              {realEncryption.KdfCount}                 {syntheticEncryption.KdfCount}");
            output.WriteLine($"文件头 extra 类型     {string.Join("/", realFile.ExtraTypes)}   {string.Join("/", syntheticFile.ExtraTypes)}");
            output.WriteLine($"头部数量              {realHeaders.Count}                 {syntheticHeaders.Count}");
            output.WriteLine($"归档体积              {realBytes.Length}             {syntheticBytes.Length}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreatePlaintext()
    {
        var plaintext = new byte[PayloadLength];
        new Random(20240102).NextBytes(plaintext);
        return plaintext;
    }

    /// <summary>
    /// 探测 WinRAR：环境变量优先（装到非默认目录时用），否则查两个标准安装位置。
    /// 找不到返回 null，调用方早退。
    /// </summary>
    private static string? FindWinRarExe()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(WinRarPathVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        return new[]
        {
            @"C:\Program Files\WinRAR\Rar.exe",
            @"C:\Program Files (x86)\WinRAR\Rar.exe",
        }.FirstOrDefault(File.Exists);
    }

    private int RunRar(string exe, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        output.WriteLine($"rar {arguments} → exit={process.ExitCode}{Environment.NewLine}{stdout}{stderr}");
        return process.ExitCode;
    }
}
