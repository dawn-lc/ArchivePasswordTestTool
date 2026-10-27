using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 宿主 CET 标记回归测试：项目必须保持 &lt;CetCompat&gt;false&lt;/CetCompat&gt;，
/// 否则 .NET 10 在启用了影子栈但系统缺少特殊用户态 APC 的旧 Windows 上
/// 会在托管 Main 之前 fail-fast（"Your Windows doesn't fully support CET..."）。
/// CET 兼容位存放在调试目录 IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS(20) 指向的 ushort 低位。
/// </summary>
public sealed class CetCompatTests
{
    private const int ImageDebugTypeExDllCharacteristics = 20;
    private const ushort ImageDllCharacteristicsExCetCompat = 0x1;

    [Fact]
    public void AppHostDoesNotAdvertiseCetCompat()
    {
        string exePath = TestPaths.MainExePath;
        Assert.True(File.Exists(exePath), $"主程序 exe 不存在：{exePath}");

        using var stream = File.OpenRead(exePath);
        using var reader = new PEReader(stream);

        int matched = 0;
        Span<byte> buffer = stackalloc byte[2];
        foreach (var entry in reader.ReadDebugDirectory())
        {
            if ((int)entry.Type != ImageDebugTypeExDllCharacteristics)
            {
                continue;
            }

            stream.Position = entry.DataPointer;
            stream.ReadExactly(buffer);

            ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
            matched++;

            Assert.True(
                (characteristics & ImageDllCharacteristicsExCetCompat) == 0,
                $"apphost 仍带 CETCOMPAT 标记（0x{characteristics:X4}）：项目应保持 <CetCompat>false</CetCompat>");
        }

        Assert.True(matched > 0, "apphost 缺少 EX_DLLCHARACTERISTICS 调试目录项，无法确认 CET 标记状态");
    }
}
