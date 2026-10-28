namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// COM 契约必须用源生成形式（`[GeneratedComInterface]` / `[GeneratedComClass]`），
/// 不能用运行时内置 COM（`[ComImport]` / `[ComVisible]` + `[ClassInterface]`）。
/// <para>
/// 原因：内置 COM 依赖运行时的动态封送 stub，Native AOT 下直接抛
/// "COM Interop requires ComWrapper instance registered for marshalling"，
/// 程序在第一次接触 7z.dll 时就崩。源生成版本由编译期产出 stub，AOT 可用，
/// 且实测功能完全等价（147 项测试 + 13 场景 AOT 全矩阵均通过）。
/// </para>
/// <para>
/// 这条测试是为了防止后来者"顺手"把某个接口改回 `[ComImport]`——
/// 那在普通构建下测试全绿，只在 AOT 产物里才炸，属于最难发现的一类回归。
/// </para>
/// </summary>
public class InteropAotCompatibilityTests
{
    private static string ReadInteropSource(string fileName) =>
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "ArchivePasswordTestTool", "Interop", fileName));

    /// <summary>
    /// 只看非注释行：注释里解释"为什么不用 [ComVisible]"是正常的，
    /// 按整份文本匹配会把说明文字误判成实际用法。
    /// </summary>
    private static IEnumerable<string> CodeLines(string source) =>
        source
            .Split('\n')
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("///", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("*", StringComparison.Ordinal));

    [Fact]
    public void ComContractsUseSourceGeneratedInterfaces()
    {
        var code = CodeLines(ReadInteropSource("ComContracts.cs")).ToList();

        Assert.DoesNotContain(code, line => line.Contains("[ComImport]", StringComparison.Ordinal));
        Assert.DoesNotContain(code, line => line.Contains("InterfaceType(ComInterfaceType", StringComparison.Ordinal));
        Assert.Contains(code, line => line.Contains("[GeneratedComInterface]", StringComparison.Ordinal));

        // 接口本身必须是 partial，否则源生成器无法补 marshaller
        foreach (string line in code)
        {
            if (line.Contains("interface I", StringComparison.Ordinal) && line.Contains('{'))
            {
                Assert.Contains("partial", line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ComCallbacksUseSourceGeneratedClasses()
    {
        var code = CodeLines(ReadInteropSource("ComCallbacks.cs")).ToList();

        Assert.DoesNotContain(code, line => line.Contains("[ComVisible", StringComparison.Ordinal));
        Assert.DoesNotContain(code, line => line.Contains("ClassInterface(ClassInterfaceType", StringComparison.Ordinal));
        Assert.Contains(code, line => line.Contains("[GeneratedComClass]", StringComparison.Ordinal));

        // CCW 类必须是 partial
        foreach (string line in code)
        {
            if (line.Contains("class ", StringComparison.Ordinal)
                && (line.Contains("IInStream", StringComparison.Ordinal)
                    || line.Contains("IArchive", StringComparison.Ordinal)
                    || line.Contains("ISequentialOutStream", StringComparison.Ordinal)
                    || line.Contains("ICryptoGetTextPassword", StringComparison.Ordinal)))
            {
                Assert.Contains("partial", line, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// AOT 下不可用的 COM 封送 API。它们只在运行时可用，
    /// 源生成路径分别对应 <c>ComInterfaceMarshaller&lt;T&gt;</c> 或纯指针写入。
    /// </summary>
    [Theory]
    [InlineData("Marshal.GetObjectForIUnknown")]
    [InlineData("Marshal.ReleaseComObject")]
    [InlineData("Marshal.GetObjectForNativeVariant")]
    public void AotUnfriendlyComMarshallingIsNotUsed(string forbiddenApi)
    {
        string interopDir = Path.Combine(TestPaths.RepoRoot, "ArchivePasswordTestTool", "Interop");

        var offenders = Directory
            .EnumerateFiles(interopDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(file => CodeLines(File.ReadAllText(file))
                .Any(line => line.Contains(forbiddenApi, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{forbiddenApi} 在 Native AOT 下不可用，但出现在：{string.Join(", ", offenders)}");
    }
}
