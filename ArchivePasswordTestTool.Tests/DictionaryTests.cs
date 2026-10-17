namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// Dictionary 多批次并发出队正确性测试：
/// 复现并守护旧双缓冲实现的并发缺陷（无同步 refill 导致跳行/重复）�?
/// 新契约：任意线程数下并发 Pop 收到的密码恰好覆盖字典全集且无重复�?
/// </summary>
public sealed class DictionaryTests : IDisposable
{
    private readonly string _path;
    private readonly string[] _expected;

    public DictionaryTests()
    {
        // 5210 行，跨过默认 Limit=2048 的三批边界，正确构�?refill 竞态条�?
        _expected = [.. Enumerable.Range(1, 5210).Select(i => $"Password{i}")];
        _path = Path.Combine(Path.GetTempPath(), $"ApttDict{Guid.NewGuid():N}.txt");
        File.WriteAllLines(_path, _expected);
    }

    public void Dispose()
    {
        File.Delete(_path);
    }

    [Fact]
    public void ConcurrentPopConsumesEveryPasswordExactlyOnce()
    {
        var dictionary = new PasswordDictionary(_path);
        Assert.Equal(_expected.Length, dictionary.Count);

        var consumed = new ConcurrentBag<string>();
        Parallel.For(0, dictionary.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, _ =>
        {
            var password = dictionary.Pop();
            if (password is not null)
            {
                consumed.Add(password);
            }
        });

        Assert.Equal(_expected.Length, consumed.Count);
        Assert.Empty(consumed.Except(_expected));
        Assert.Empty(_expected.Except(consumed));
        Assert.Equal(_expected.Length, consumed.Distinct().Count());
    }

    [Fact]
    public void SequentialPopReturnsEveryPasswordExactlyOnce()
    {
        var dictionary = new PasswordDictionary(_path, limit: 1000);

        var consumed = new List<string>();
        while (true)
        {
            var password = dictionary.Pop();
            if (password is null)
            {
                break;
            }

            consumed.Add(password);
        }

        Assert.Equal(_expected, consumed);
    }

    [Fact]
    public void PopReturnsNullWhenExhausted()
    {
        var dictionary = new PasswordDictionary(_path);

        // 正常消费完后继续 Pop 必须稳定返回 null（不得抛异常或死循环�?
        while (dictionary.Pop() is not null)
        {
        }

        Assert.Null(dictionary.Pop());
        Assert.Null(dictionary.Pop());
    }

    [Fact]
    public void SmallerThanLimitDictionaryIsFullyConsumed()
    {
        string smallPath = Path.Combine(Path.GetTempPath(), $"ApttDictSmall{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(smallPath, ["a", "b", "c"]);
            var dictionary = new PasswordDictionary(smallPath);

            Assert.Equal("a", dictionary.Pop());
            Assert.Equal("b", dictionary.Pop());
            Assert.Equal("c", dictionary.Pop());
            Assert.Null(dictionary.Pop());
        }
        finally
        {
            File.Delete(smallPath);
        }
    }

    [Fact]
    public void CountEqualsUniqueLinesAndDuplicatesServedOnceInFileOrder()
    {
        string dupPath = Path.Combine(Path.GetTempPath(), $"ApttDictDup{Guid.NewGuid():N}.txt");
        try
        {
            // 重复行跨越默�?Limit=2048 批次边界
            var lines = new List<string> { "alpha" };
            for (int i = 1; i <= 2100; i++)
            {
                lines.Add($"Password{i}");
            }

            lines.Add("alpha");     // 与首行重复，远跨批次
            lines.Add("Password1"); // 早期出现行重�?
            lines.Add("beta");
            File.WriteAllLines(dupPath, lines);

            using var dictionary = new PasswordDictionary(dupPath);

            // Distinct 保留首次出现顺序，与实现的文件序服务语义一�?
            var expected = lines.Distinct().ToList();
            Assert.Equal(expected.Count, dictionary.Count);

            var consumed = new List<string>();
            while (dictionary.Pop() is { } password)
            {
                consumed.Add(password);
            }

            Assert.Equal(expected, consumed);
        }
        finally
        {
            File.Delete(dupPath);
        }
    }

    [Fact]
    public void ConcurrentPopServesEachUniquePasswordOnceWithDuplicatesInFile()
    {
        string dupPath = Path.Combine(Path.GetTempPath(), $"ApttDictDupCon{Guid.NewGuid():N}.txt");
        try
        {
            // 大量重复行跨越批次边界：3 个唯一密码重复 2200 �?
            string[] unique = ["alpha", "beta", "gamma"];
            var lines = new List<string>(2200);
            for (int i = 0; i < 2200; i++)
            {
                lines.Add(unique[i % unique.Length]);
            }

            File.WriteAllLines(dupPath, lines);

            using var dictionary = new PasswordDictionary(dupPath);
            Assert.Equal(unique.Length, dictionary.Count);

            var consumed = new ConcurrentBag<string>();
            Parallel.For(0, dictionary.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, _ =>
            {
                var password = dictionary.Pop();
                if (password is not null)
                {
                    consumed.Add(password);
                }
            });

            Assert.Equal(unique.Length, consumed.Count);
            Assert.Empty(consumed.Except(unique));
        }
        finally
        {
            File.Delete(dupPath);
        }
    }
}
