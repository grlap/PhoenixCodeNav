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
        try { Directory.Delete(Root, recursive: true); } catch { /* leave temp on Windows lock */ }
    }
}
