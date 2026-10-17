namespace ArchivePasswordTestTool.Tests.Infrastructure;

/// <summary>
/// 共享测试集合：注册 TestData 夹具，集合内测试串行执行（避免 exe 并发冲突）。
/// </summary>
[CollectionDefinition(Name)]
public sealed class TestDataCollection : ICollectionFixture<TestDataFixture>
{
    public const string Name = "UsesTestData";
}
