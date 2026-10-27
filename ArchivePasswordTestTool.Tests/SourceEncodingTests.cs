namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 源码编码一致性：仓库约定 UTF-8 无 BOM（见根目录 .editorconfig 的 charset = utf-8）。
/// 带 BOM 会让文件首行多出 \ufeff，在按字节比对、内容哈希、diff 等场景产生假差异，
/// 也会让部分外部工具（PowerShell 5.1 读管道、某些正则/文本工具）在首行匹配失败。
/// </summary>
public sealed class SourceEncodingTests
{
    /// <summary>检查的文件类型（源码、项目配置、CI 配置、文档）</summary>
    private static readonly string[] CheckedExtensions = [".cs", ".csproj", ".sln", ".resx", ".yml", ".md"];

    /// <summary>无扩展名但同样需要遵守约定的文件</summary>
    private static readonly string[] CheckedNames = [".editorconfig", ".gitignore"];

    /// <summary>构建产物与工具目录，不参与检查</summary>
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", "TestResults"];

    [Fact]
    public void SourceFilesUseUtf8WithoutBom()
    {
        var offenders = new List<string>();

        // stackalloc 放在循环外：写在循环体内会随迭代累积栈空间（CA2014）
        Span<byte> head = stackalloc byte[3];

        foreach (var path in EnumerateCheckedFiles(TestPaths.RepoRoot))
        {
            head.Clear();

            int read;
            using (var stream = File.OpenRead(path))
            {
                read = stream.ReadAtLeast(head, 3, throwOnEndOfStream: false);
            }

            if (read < 3)
            {
                continue;
            }

            if (head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                offenders.Add(Path.GetRelativePath(TestPaths.RepoRoot, path));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下文件带 UTF-8 BOM，应保存为无 BOM（.editorconfig 已声明 charset = utf-8）：\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void CheckedFileCountIsSane()
    {
        // 防止枚举逻辑失效（如根目录判断错误导致扫不到文件）时测试假通过
        int count = EnumerateCheckedFiles(TestPaths.RepoRoot).Count();

        Assert.True(count > 40, $"仅扫描到 {count} 个文件，疑似枚举逻辑失效");
    }

    private static IEnumerable<string> EnumerateCheckedFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(file);

                if (CheckedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || CheckedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }
}
