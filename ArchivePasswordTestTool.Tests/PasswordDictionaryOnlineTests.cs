namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 在线共享密码并入字典的语义（一个压缩包哈希对应一个密码）：
/// 该密码必须最优先测试；与文件内已有行重复时按去重规则只算一次。
/// </summary>
public sealed class PasswordDictionaryOnlineTests : IDisposable
{
    private readonly string _path;

    public PasswordDictionaryOnlineTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"ApttDictOnline{Guid.NewGuid():N}.txt");
        File.WriteAllLines(_path, ["first", "second", "third"]);
    }

    public void Dispose()
    {
        File.Delete(_path);
    }

    [Fact]
    public void OnlinePasswordAlreadyInFileIsServedFirstWithoutDuplicating()
    {
        using var dictionary = new PasswordDictionary(_path, onlinePassword: "second");

        // 与文件内已有行重复 → 唯一密码数不变，但仍从队首被取出
        Assert.Equal(3, dictionary.Count);
        Assert.Equal("second", dictionary.Pop());
        Assert.Equal("first", dictionary.Pop());
        Assert.Equal("third", dictionary.Pop());
        Assert.Null(dictionary.Pop());
    }

    [Fact]
    public void OnlinePasswordAbsentFromFileIsAddedAtTheFront()
    {
        using var dictionary = new PasswordDictionary(_path, onlinePassword: "fromServer");

        Assert.Equal(4, dictionary.Count);
        Assert.Equal("fromServer", dictionary.Pop());
        Assert.Equal("first", dictionary.Pop());
        Assert.Equal("second", dictionary.Pop());
        Assert.Equal("third", dictionary.Pop());
        Assert.Null(dictionary.Pop());
    }
}
