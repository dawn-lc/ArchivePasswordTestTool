using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// 构建期任务：把 resvg 渲染出的各尺寸 PNG 打包成多分辨率、透明背景的 .ico
// （csproj 通过 <UsingTask TaskName="PackAppIconTask" ... Source="本文件" /> 引用）。
//
// 分工：**SVG 解析与栅格化交给 resvg**（<Exec> 调用，见 csproj 的 GenerateAppIcon），
// 本文件只把一个已渲染好的 PNG 序列装进 ICO 容器：不解析、不解码、不重编码任何像素。
//
// 为什么不让 resvg 直接产出 .ico：它只有 PNG 一种输出（CLI 里那参数就叫 out-png）。
// 而 ICO 是「多张独立位图的容器」，8 档尺寸无论如何都要渲染 8 次，差异只在收尾那一步。
//
// **为什么每一帧都原样嵌 PNG（含小尺寸）**：ICO 自 Windows Vista 起允许任意尺寸用 PNG 帧。
// 曾经的实现把 <256 的帧转成 32 位 DIB，理由是「System.Drawing.Icon 读不了 64px 以上的
// PNG 帧」。实测该顾虑不成立 —— 构造全 PNG 帧的 ICO 与 DIB 版本逐项对比：
//   · System.Drawing 请求 16/20/24/32/40/48/64 px：两者都正确返回对应尺寸；
//   · System.Drawing 请求 256 px：两者都退回 64x64（256 那档原本就是 PNG）；
//   · Windows Shell（SHGetFileInfo）取 16/32 px 位图：像素摘要完全一致。
// 也就是说 DIB 转换没换来任何可测的兼容性收益，代价却是约 300 行 PNG 解码/反滤波代码
// （5 种行滤波含 Paeth、zlib 头处理）加一段 DIB 编码，以及 3 倍体积（51.6 KB vs 17.7 KB）。
// 故全部直嵌 PNG。
// 兼容下限：PNG 帧自 Windows Vista 起支持，而本项目最低要求 Windows 10。
//
// 仍然校验帧尺寸：resvg 若没按 --width/--height 出图（参数拼错等），ICO 目录项里记的尺寸
// 就会与真实像素不符，得到一个「能打开但显示错乱」的图标 —— 这类问题极难发现，
// 所以读 IHDR 的 8 个字节核对一次。为此保留完整解码器不值得。
//
// 注意：RoslynCodeTaskFactory 的编译参考集较旧，此处只用 .NET Framework 4.5 级别的 API
// （不用 using 声明、switch 表达式、目标类型 new()、Math.Clamp 等）。

/// <summary>把一组 PNG 帧打包成 ICO。</summary>
public class PackAppIconTask : Task
{
    /// <summary>PNG 帧所在目录（由 csproj 里的 Exec 调用 resvg 生成）。</summary>
    public string PngDirectory { get; set; }

    /// <summary>尺寸列表，分隔符可为逗号、分号或空白。</summary>
    public string Sizes { get; set; }

    /// <summary>ICO 输出路径。</summary>
    public string OutputIco { get; set; }

    /// <summary>PNG 文件名前缀：帧文件名为「前缀 + 尺寸 + .png」。</summary>
    public string FilePrefix { get; set; }

    public override bool Execute()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(OutputIco))
            {
                Log.LogError("未指定图标输出路径（OutputIco）。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(PngDirectory) || !Directory.Exists(PngDirectory))
            {
                Log.LogError("PNG 帧目录不存在：" + (PngDirectory ?? "(未指定)") + "（resvg 渲染步骤没有产出？）");
                return false;
            }

            OutputIco = Path.GetFullPath(OutputIco);
            PngDirectory = Path.GetFullPath(PngDirectory);

            string prefix = string.IsNullOrEmpty(FilePrefix) ? "AppIcon-" : FilePrefix;
            int[] sizes = ParseSizes();

            var images = new List<IconImage>();

            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                string framePath = Path.Combine(PngDirectory, prefix + size.ToString(CultureInfo.InvariantCulture) + ".png");
                if (!File.Exists(framePath))
                {
                    Log.LogError("缺少 " + size + "px 的帧文件：" + framePath);
                    return false;
                }

                // 原样嵌入：帧本身就是 resvg 按目标像素渲染出的 PNG（不重采样、不重编码）
                byte[] png = File.ReadAllBytes(framePath);
                PngFrame.Validate(png, size, framePath);
                images.Add(new IconImage(size, png));
            }

            byte[] content = IcoImage.Encode(images);

            // 增量判定按内容比对，不看时间戳：下游是 Csc 的 /win32icon，
            // 内容没变就不动文件，避免无谓地触发重新编译。
            if (File.Exists(OutputIco) && SameContent(OutputIco, content))
            {
                Log.LogMessage(MessageImportance.Low, "图标已是最新：" + OutputIco);
                return true;
            }

            string directory = Path.GetDirectoryName(OutputIco);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(OutputIco, content);

            Log.LogMessage(
                MessageImportance.Normal,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "打包程序图标：{0}（{1} 档 PNG 帧，共 {2} 字节）",
                    OutputIco,
                    sizes.Length,
                    content.Length));
            return true;
        }
        catch (IconFormatException ex)
        {
            Log.LogError("图标打包失败：" + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, true);
            return false;
        }
    }

    private static bool SameContent(string path, byte[] content)
    {
        if (new FileInfo(path).Length != content.Length)
        {
            return false;
        }

        byte[] existing = File.ReadAllBytes(path);
        if (existing.Length != content.Length)
        {
            return false;
        }

        for (int i = 0; i < content.Length; i++)
        {
            if (existing[i] != content[i])
            {
                return false;
            }
        }

        return true;
    }

    private int[] ParseSizes()
    {
        if (string.IsNullOrWhiteSpace(Sizes))
        {
            throw new IconFormatException("未指定尺寸列表（Sizes）。");
        }

        string[] parts = Sizes.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var sizes = new List<int>();

        for (int i = 0; i < parts.Length; i++)
        {
            int size;
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 1 || size > 256)
            {
                throw new IconFormatException("尺寸 \"" + parts[i] + "\" 无效（只接受 1..256 的整数）。");
            }

            if (!sizes.Contains(size))
            {
                sizes.Add(size);
            }
        }

        if (sizes.Count == 0)
        {
            throw new IconFormatException("尺寸列表为空。");
        }

        sizes.Sort();
        return sizes.ToArray();
    }
}

/// <summary>图标数据不符合预期。抛出即构建失败。</summary>
public class IconFormatException : Exception
{
    public IconFormatException(string message) : base(message)
    {
    }
}

/// <summary>ICO 中的一帧。</summary>
internal sealed class IconImage
{
    public readonly int Size;
    public readonly byte[] Payload;

    public IconImage(int size, byte[] payload)
    {
        Size = size;
        Payload = payload;
    }
}

/// <summary>
/// 只读 PNG 头部做校验：签名 + IHDR 里的真实像素尺寸。
/// <para>
/// 不解析像素：帧是原样嵌进 ICO 的，解码出来也没有用处，只核对尺寸。
/// 签名 8 + 长度 4 + 类型 4 = 16，再读 IHDR 的宽高各 4 字节，故最少需要 24 字节；
/// 这里要求 33（含 IHDR 余下的 5 个字段）以便一次读完整个头部。
/// </para>
/// </summary>
internal static class PngFrame
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static void Validate(byte[] png, int expectedSize, string path)
    {
        if (png.Length < 33)
        {
            throw new IconFormatException("PNG 文件过短：" + path);
        }

        for (int i = 0; i < Signature.Length; i++)
        {
            if (png[i] != Signature[i])
            {
                throw new IconFormatException("不是 PNG 文件（签名不符）：" + path);
            }
        }

        // PNG 规范要求首个分块必须是 IHDR
        if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            throw new IconFormatException("PNG 首个分块不是 IHDR：" + path);
        }

        int width = ReadBigEndianInt32(png, 16);
        int height = ReadBigEndianInt32(png, 20);
        if (width != expectedSize || height != expectedSize)
        {
            throw new IconFormatException(
                "PNG 尺寸不符：期望 " + expectedSize + "x" + expectedSize
                + "，实际 " + width + "x" + height + "（" + path + "）");
        }
    }

    private static int ReadBigEndianInt32(byte[] buffer, int offset)
    {
        return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
    }
}

/// <summary>ICO 容器编码：ICONDIR + ICONDIRENTRY × N + 各帧数据。</summary>
internal static class IcoImage
{
    public static byte[] Encode(IList<IconImage> images)
    {
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((short)0);            // reserved
                writer.Write((short)1);            // type = icon
                writer.Write((short)images.Count);

                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    IconImage image = images[i];
                    // 256 在这一字节里记为 0（字段本身只有 8 位）
                    writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
                    writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
                    writer.Write((byte)0);         // 调色板色数
                    writer.Write((byte)0);         // reserved
                    writer.Write((short)1);        // planes
                    writer.Write((short)32);       // bitCount
                    writer.Write(image.Payload.Length);
                    writer.Write(offset);
                    offset += image.Payload.Length;
                }

                for (int i = 0; i < images.Count; i++)
                {
                    writer.Write(images[i].Payload);
                }

                writer.Flush();
            }

            return stream.ToArray();
        }
    }
}
