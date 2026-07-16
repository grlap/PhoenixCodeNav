namespace CodeNav.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedIndexCollection : ICollectionFixture<SharedIndexFixture>
{
    public const string Name = "Shared functional index";
}

[Collection(SharedIndexCollection.Name)]
public sealed class SharedIndexCollectionContractTests
{
    public SharedIndexCollectionContractTests(SharedIndexFixture fixture) => _ = fixture;

    [Fact]
    public void FunctionalIndexIsBuiltOncePerTestProcess() =>
        Assert.Equal(1, SharedIndexFixture.InstancesCreated);
}
