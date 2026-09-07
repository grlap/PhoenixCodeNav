using CodeNav.Core.Indexing;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Watcher lifecycle tests get a dedicated workspace and database; no other test assembly
/// can attach a writer or watcher to this fixture.
/// </summary>
public sealed class IndexFixture : IDisposable
{
    public string Root { get; }
    public string DbPath { get; }

    public IndexFixture()
    {
        Root = Directory.CreateTempSubdirectory("codenav-watcher").FullName;
        WorkspaceGenerator.Generate(Root, targetProjects: 40, seed: 7);
        DbPath = IndexBuilder.DefaultDbPath(Root);
        IndexBuilder.Build(Root, DbPath);
    }

    public IndexQueries Open() => new(DbPath);

    public void Dispose()
    {
        TestWorkspaceCleanup.ClearIndexPools(Root);
        try
        {
            TestWorkspaceCleanup.DeleteWorkspaceStrict(Root);
            Assert.False(Directory.Exists(Root));
        }
        finally
        {
            if (Directory.Exists(Root))
                TestWorkspaceCleanup.DeleteWorkspace(Root);
        }
    }
}

/// <summary>
/// The bounded-Dispose contracts share an isolated generated workspace whose teardown remains
/// tolerant because production Dispose may defer owned-resource release.
/// </summary>
public sealed class DisposeIndexFixture : IDisposable
{
    public string Root { get; }
    public string DbPath { get; }

    public DisposeIndexFixture()
    {
        Root = Directory.CreateTempSubdirectory("codenav-disp").FullName;
        WorkspaceGenerator.Generate(Root, targetProjects: 40, seed: 7);
        DbPath = IndexBuilder.DefaultDbPath(Root);
        IndexBuilder.Build(Root, DbPath);
    }

    public void Dispose()
    {
        TestWorkspaceCleanup.DeleteWorkspace(Root);
    }
}
