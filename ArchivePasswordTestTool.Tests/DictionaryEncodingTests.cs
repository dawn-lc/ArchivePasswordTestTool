using System.Text;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 字典编码探测。缺陷背景：早前把编码硬定为 UTF-8，GBK 密码本（简中 Windows
/// 记事本另存为 ANSI 的默认结果）整份读成乱码，即使密码就在字典里也必然失败。
/// 实测：同一中文密码包，GBK 字典报"未找到"，内容相同的 UTF-8 字典找到密码。
/// </summary>
public class DictionaryEncodingTests
{
    private static readonly string TempFolder =
        Path.Combine(Path.GetTempPath(), "aptt-encoding-tests");

    private static byte[] GbkBytes(string text)
    {
        DictionaryEncoding.EnsureCodePagesRegistered();
        return Encoding.GetEncoding(936).GetBytes(text);
    }

    [Fact]
    public void GbkDictionaryIsDetectedAsGbk()
    {
        byte[] sample = GbkBytes("错误密码1\r\n机密口令\r\n错误密码2\r\n");

        Encoding detected = DictionaryEncoding.DetectFromSample(sample);

        Assert.Equal(936, detected.CodePage);
        Assert.Equal("机密口令", Encoding.GetEncoding(936).GetString(GbkBytes("机密口令")));
    }

    [Fact]
    public void GbkDictionaryRoundTripsThroughDetectedEncoding()
    {
        const string Content = "错误密码1\r\n机密口令\r\n错误密码2\r\n";
        byte[] sample = GbkBytes(Content);

        Encoding detected = DictionaryEncoding.DetectFromSample(sample);

        Assert.Equal(Content, detected.GetString(sample));
    }

    [Fact]
    public void Utf8DictionaryIsDetectedAsUtf8()
    {
        byte[] sample = Encoding.UTF8.GetBytes("wrong1\r\n机密口令\r\nwrong2\r\n");

        Assert.Equal(65001, DictionaryEncoding.DetectFromSample(sample).CodePage);
    }

    [Fact]
    public void AsciiOnlyFileStaysUtf8()
    {
        byte[] sample = "password\r\n123456\r\nadmin\r\n"u8.ToArray();

        Assert.Equal(65001, DictionaryEncoding.DetectFromSample(sample).CodePage);
    }

    [Fact]
    public void Utf8BomIsDetectedAndStripped()
    {
        byte[] body = Encoding.UTF8.GetBytes("密码\r\n");
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. body];

        Encoding detected = DictionaryEncoding.DetectFromSample(withBom);

        Assert.Equal(65001, detected.CodePage);
        // emitBOM=false：读取时不能再把 BOM 当成内容（否则第一行密码会多出 U+FEFF）
        Assert.Empty(detected.GetPreamble());
    }

    [Fact]
    public void Utf16LittleEndianBomIsDetected()
    {
        byte[] withBom = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("密码\r\n")];

        Assert.Equal(1200, DictionaryEncoding.DetectFromSample(withBom).CodePage);
    }

    [Fact]
    public void Utf16BigEndianBomIsDetected()
    {
        byte[] withBom = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("密码\r\n")];

        Assert.Equal(1201, DictionaryEncoding.DetectFromSample(withBom).CodePage);
    }

    /// <summary>
    /// 探测窗口按 64KB 截断，很可能切在多字节字符中间。
    /// 截断的尾字节不能导致整份文件被误判成非 UTF-8。
    /// </summary>
    [Fact]
    public void TruncatedTrailingCharacterDoesNotFoolUtf8Detection()
    {
        byte[] full = Encoding.UTF8.GetBytes("密码字典内容");

        // "容" 是 3 字节（E5 AE B9）：截到只剩 1 个字节，等于切在字符中间
        byte[] truncated = full[..^2];

        Assert.True(DictionaryEncoding.IsValidUtf8(truncated));
        Assert.Equal(65001, DictionaryEncoding.DetectFromSample(truncated).CodePage);
    }

    [Fact]
    public void EmptySampleFallsBackToUtf8()
    {
        Assert.Equal(65001, DictionaryEncoding.DetectFromSample([]).CodePage);
    }

    /// <summary>端到端：GBK 字典必须读出正确的中文密码（这是原始缺陷的直接回归）。</summary>
    [Fact]
    public void GbkDictionaryYieldsReadablePasswords()
    {
        Directory.CreateDirectory(TempFolder);
        string path = Path.Combine(TempFolder, "DictGbk.txt");

        try
        {
            File.WriteAllBytes(path, GbkBytes("错误密码1\r\n机密口令\r\n错误密码2\r\n"));

            using var dictionary = new PasswordDictionary(path);

            Assert.Equal(3, dictionary.Count);
            Assert.Equal(936, dictionary.Encoding.CodePage);

            var passwords = new List<string?>();
            while (dictionary.Pop() is { } password)
            {
                passwords.Add(password);
            }

            Assert.Equal(["错误密码1", "机密口令", "错误密码2"], passwords);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Utf8DictionaryYieldsReadablePasswords()
    {
        Directory.CreateDirectory(TempFolder);
        string path = Path.Combine(TempFolder, "DictUtf8.txt");

        try
        {
            File.WriteAllText(path, "错误密码1\r\n机密口令\r\n错误密码2\r\n", new UTF8Encoding(false));

            using var dictionary = new PasswordDictionary(path);

            Assert.Equal(65001, dictionary.Encoding.CodePage);

            var passwords = new List<string?>();
            while (dictionary.Pop() is { } password)
            {
                passwords.Add(password);
            }

            Assert.Equal(["错误密码1", "机密口令", "错误密码2"], passwords);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileDoesNotThrowDuringDetection()
    {
        // 探测阶段不能替代真正的读取错误：返回 UTF-8，让后续 ReadLines 抛原始异常
        string path = Path.Combine(TempFolder, "DoesNotExist.txt");

        Assert.Equal(65001, DictionaryEncoding.Detect(path).CodePage);
    }

    [Fact]
    public void EnsureCodePagesRegisteredIsIdempotent()
    {
        DictionaryEncoding.EnsureCodePagesRegistered();
        DictionaryEncoding.EnsureCodePagesRegistered();

        Assert.Equal(936, Encoding.GetEncoding(936).CodePage);
    }
}
