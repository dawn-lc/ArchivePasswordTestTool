using ArchivePasswordTestTool.Tests.Infrastructure;
using static ArchivePasswordTestTool.ArchiveFormatDetector;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 格式检测单元测试：覆盖普通单文件、分卷首卷、SFX（MZ 封装签名扫描）与负例。
/// </summary>
[Collection(TestDataCollection.Name)]
public sealed class FormatDetectionTests(TestDataFixture fx, ITestOutputHelper output)
{
    [Fact]
    public void SevenZipPlainHeadOffsetZero()
    {
        string path = fx.Archive("Secret.7z");

        var first = DetectArchiveFormat(path);
        var second = DetectArchiveFormat(path); // 第二次调用覆盖检测缓存路径

        Assert.Equal(SevenZipGuids.CLSID_CFormat7z, first.Format);
        Assert.Equal(0, first.Offset);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ZipPlainHeadOffsetZero()
    {
        var (format, offset) = DetectArchiveFormat(fx.Archive("SecretZip.zip"));

        Assert.Equal(SevenZipGuids.CLSID_CFormatZip, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void SevenZipHeaderEncryptedStillDetectedByHead()
    {
        var (format, offset) = DetectArchiveFormat(fx.Archive("SecretHeader.7z"));

        Assert.Equal(SevenZipGuids.CLSID_CFormat7z, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void SevenZipFirstVolumeDetectedByHead()
    {
        // 分卷首卷 .001 的头部就是 7z 签名，格式检测只看文件本身
        var (format, offset) = DetectArchiveFormat(fx.Archive("SecretSplit.7z.001"));

        Assert.Equal(SevenZipGuids.CLSID_CFormat7z, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void SevenZipSfxSignatureFoundAfterStub()
    {
        string path = fx.Archive("SecretSfx.exe");
        long fileLength = new FileInfo(path).Length;

        var (format, offset) = DetectArchiveFormat(path);

        Assert.Equal(SevenZipGuids.CLSID_CFormat7z, format);
        Assert.InRange(offset, 1, fileLength - 6); // stub 之后、且完整签名可容纳

        output.WriteLine($"7z SFX stub 偏移：{offset}（文件大小 {fileLength}）");
    }

    [Fact]
    public void SevenZipHeaderEncryptedSfxSignatureFoundAfterStub()
    {
        string path = fx.Archive("SecretSfxHeader.exe");

        var (format, offset) = DetectArchiveFormat(path);

        Assert.Equal(SevenZipGuids.CLSID_CFormat7z, format);
        Assert.True(offset > 0, $"header 加密 SFX 的签名偏移应大于 0，实际 {offset}");
    }

    [Fact]
    public void NonArchiveExeRejected()
    {
        Assert.Equal(Guid.Empty, DetectArchiveFormat(fx.Archive("NotAnArchive.exe")).Format);
    }

    [Fact]
    public void TinyFileRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ApttTiny{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, [0x37, 0x7A, 0xBC]); // 仅 3 字节，低于最低识别长度
            Assert.Equal(Guid.Empty, DetectArchiveFormat(path).Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MzWithoutArchiveSignatureRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ApttMz{Guid.NewGuid():N}.exe");
        try
        {
            var mz = new byte[65536];
            mz[0] = (byte)'M';
            mz[1] = (byte)'Z';
            File.WriteAllBytes(path, mz);

            Assert.Equal(Guid.Empty, DetectArchiveFormat(path).Format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- RAR 系（需 WinRAR，缺失时跳过） ----------

    [Fact]
    public void Rar5SingleHeadOffsetZero()
    {
        if (!fx.HasWinRAR)
        {
            output.WriteLine("跳过：未安装 WinRAR");
            return;
        }

        var (format, offset) = DetectArchiveFormat(fx.Archive("Rar5Single.rar"));

        Assert.Equal(SevenZipGuids.CLSID_CFormatRar5, format);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Rar5SfxSignatureFoundAfterStub()
    {
        if (!fx.HasWinRAR)
        {
            output.WriteLine("跳过：未安装 WinRAR");
            return;
        }

        string path = fx.Archive("RarSfx.exe");

        var (format, offset) = DetectArchiveFormat(path);

        Assert.Equal(SevenZipGuids.CLSID_CFormatRar5, format);
        Assert.True(offset > 0, $"RAR SFX 的签名偏移应大于 0，实际 {offset}");
    }
}
