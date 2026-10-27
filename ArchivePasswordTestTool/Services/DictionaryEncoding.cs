using System.Globalization;
using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 字典文件的编码探测。
    /// <para>
    /// 早期实现把编码硬定为 UTF-8，于是 GBK 编码的密码本（中文 Windows 记事本
    /// 另存为 ANSI 的默认结果）整份读成乱码，即使密码就在字典里也一律解不出——
    /// 实测：同一个中文密码包，GBK 字典报"未找到"，UTF-8 字典（内容相同）找到密码。
    /// </para>
    /// <para>
    /// 探测顺序（文件级，读一次即可）：
    /// <list type="number">
    /// <item>BOM（UTF-8 / UTF-16 LE / UTF-16 BE）—— 有标记就没有歧义</item>
    /// <item>严格 UTF-8 校验 —— 能完整解码即 UTF-8（现代编辑器默认）</item>
    /// <item>GBK（936）—— 非 UTF-8 的老密码本绝大多数来自简中 Windows</item>
    /// <item>系统 ANSI 代码页 —— 兜底（GBK 不可用时，如精简运行时）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 注意 .NET Core 起 <see cref="Encoding.Default"/> 恒为 UTF-8（不是系统 ANSI），
    /// 且 <c>Encoding.GetEncoding(936)</c> 需先 <see cref="Encoding.RegisterProvider"/>，
    /// 否则抛 <see cref="NotSupportedException"/>。该包在 .NET 10 内置，无体积代价。
    /// </para>
    /// </summary>
    internal static class DictionaryEncoding
    {
        /// <summary>探测窗口：足够判定编码，又不必读完整份字典（密码本可能上百 MB）</summary>
        private const int ProbeBytes = 64 * 1024;

        private const int GbkCodePage = 936;

        private static readonly object RegisterLock = new();
        private static bool providerRegistered;

        /// <summary>
        /// 探测字典文件的编码。文件不存在/无法读取时返回 UTF-8（后续读取会自然报错，
        /// 让真正的错误走统一的"字典文件不存在"路径，而不是在这里伪装成编码问题）。
        /// </summary>
        internal static Encoding Detect(string dictionaryPath)
        {
            byte[] sample;
            int length;

            try
            {
                using var stream = new FileStream(
                    dictionaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                sample = new byte[ProbeBytes];
                length = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
            }
            catch
            {
                return Encoding.UTF8;
            }

            return DetectFromSample(sample.AsSpan(0, length));
        }

        /// <summary>按样本字节判定编码（纯函数，便于单测）</summary>
        internal static Encoding DetectFromSample(ReadOnlySpan<byte> sample)
        {
            if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
            {
                return Encoding.Unicode;
            }

            if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }

            if (IsValidUtf8(sample))
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }

            return GetGbkOrAnsi();
        }

        /// <summary>
        /// 严格 UTF-8 校验。样本末尾可能截断在多字节字符中间，这是正常的——
        /// 只要「完整字符」都能解码即可，不能因为最后几个字节而被判成非 UTF-8。
        /// </summary>
        internal static bool IsValidUtf8(ReadOnlySpan<byte> sample)
        {
            try
            {
                // throwOnInvalidBytes: true —— 遇到非法字节序列立刻抛
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

                // 去掉末尾可能被截断的不完整字符（最多 3 字节的尾随续字节）
                int trim = 0;
                while (trim < 3 && trim < sample.Length)
                {
                    byte b = sample[^(trim + 1)];
                    if ((b & 0xC0) != 0x80)
                    {
                        // 遇到 lead byte 或 ASCII：若它引导的字符长度超过剩余字节，则截断
                        int needed = b < 0x80 ? 1 : b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : 2;
                        if (needed > trim + 1)
                        {
                            trim++;
                        }
                        break;
                    }

                    trim++;
                }

                ReadOnlySpan<byte> body = sample[..(sample.Length - trim)];
                strict.GetCharCount(body);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>
        /// 非 UTF-8 文本的回退编码：优先 GBK（简中密码本的主流编码），
        /// 该代码页不可用时退回系统 ANSI 代码页。
        /// </summary>
        private static Encoding GetGbkOrAnsi()
        {
            EnsureCodePagesRegistered();

            try
            {
                return Encoding.GetEncoding(GbkCodePage);
            }
            catch (Exception)
            {
                // 精简运行时/未注册 CodePages：退回系统 ANSI 代码页，再不行只能 UTF-8
            }

            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            }
            catch (Exception)
            {
                return Encoding.UTF8;
            }
        }

        /// <summary>
        /// 注册 CodePages 提供程序（幂等）。.NET 10 已内置该包（NU1510 提示无需显式引用），
        /// 但不注册就取不到 936/950 等代码页。
        /// </summary>
        internal static void EnsureCodePagesRegistered()
        {
            if (providerRegistered)
            {
                return;
            }

            lock (RegisterLock)
            {
                if (providerRegistered)
                {
                    return;
                }

                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                }
                catch (Exception)
                {
                    // 注册失败不应影响主流程：后续 GetEncoding 会失败并退回 ANSI/UTF-8
                }

                providerRegistered = true;
            }
        }
    }
}
