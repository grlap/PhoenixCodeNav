using CodeNav.Core.Indexing;
using CodeNav.WorkspaceGen;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 10 (-wyq/-rbg + rebuild infra): the syntax indexer now defaults interface members to
/// public, keeps ref/out/params parameter modifiers in signatures, and a stale on-disk
/// schema_version forces a rebuild so a deployed binary never trusts an old index.
/// </summary>
public class Batch10ModifiersTests
{
    // -wyq: interface members are implicitly public; class members stay private-by-default.
    [Fact]
    public void InterfaceMembersDefaultToPublic()
    {
        var parsed = SyntaxIndexer.Parse("I.cs",
            "namespace N { public interface IFoo { void Bar(); int Baz { get; } } " +
            "public class C { void Hidden() { } int Field; } }");

        Assert.Equal("public", parsed.Symbols.First(s => s.Name == "Bar").Accessibility);
        Assert.Equal("public", parsed.Symbols.First(s => s.Name == "Baz").Accessibility);
        // No regression: class members without a modifier are still private.
        Assert.Equal("private", parsed.Symbols.First(s => s.Name == "Hidden").Accessibility);
        Assert.Equal("private", parsed.Symbols.First(s => s.Name == "Field").Accessibility);
        // An explicit modifier on an interface member still wins.
        var p2 = SyntaxIndexer.Parse("I2.cs", "interface IX { private void Helper() { } }");
        Assert.Equal("private", p2.Symbols.First(s => s.Name == "Helper").Accessibility);
    }

    // -wyq (review follow-up): a nested type/delegate's default accessibility follows its container.
    [Fact]
    public void NestedTypeAndDelegateAccessibilityFollowsContainer()
    {
        var p = SyntaxIndexer.Parse("N.cs",
            "namespace N { public interface I { class InIface { } delegate void DIface(); } " +
            "public class C { class InClass { } delegate void DClass(); } " +
            "delegate void DTop(); }");
        Assert.Equal("public", p.Symbols.First(s => s.Name == "InIface").Accessibility);  // interface-nested type
        Assert.Equal("public", p.Symbols.First(s => s.Name == "DIface").Accessibility);   // interface-nested delegate
        Assert.Equal("private", p.Symbols.First(s => s.Name == "InClass").Accessibility); // class-nested type
        Assert.Equal("private", p.Symbols.First(s => s.Name == "DClass").Accessibility);  // class-nested delegate
        Assert.Equal("internal", p.Symbols.First(s => s.Name == "DTop").Accessibility);   // top-level delegate
    }

    // -rbg: parameter modifiers are part of the signature (and often the overload disambiguator).
    [Fact]
    public void SignatureKeepsParameterModifiers()
    {
        var parsed = SyntaxIndexer.Parse("C.cs",
            "class C { void M(ref string a, out int b, params object[] c) { b = 0; } }");
        string sig = parsed.Symbols.First(s => s.Name == "M").Signature;
        Assert.Contains("ref string a", sig);
        Assert.Contains("out int b", sig);
        Assert.Contains("params object[] c", sig);
    }

    // Deploy-safety: an index written by an older binary (lower schema_version) is rebuilt on open,
    // so it can never be queried with the new code's assumptions.
    [Fact]
    public void StaleSchemaVersionForcesRebuild()
    {
        string root = Directory.CreateTempSubdirectory("codenav-schema").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 4, seed: 3);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using (var store = new IndexStore(dbPath, createNew: false))
                store.SetMeta("schema_version", "0"); // pretend an older binary built this
            SqliteConnection.ClearAllPools();

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            for (int i = 0; i < 200 && !manager.IsQueryable; i++) Thread.Sleep(50);
            Assert.True(manager.IsQueryable, "index did not become queryable after rebuild");

            using var store2 = new IndexStore(dbPath, createNew: false);
            Assert.Equal(IndexBuilder.SchemaVersion, store2.GetMeta("schema_version"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    // A corrupt (non-SQLite-header) index.db must be rebuilt on open, not leave the manager failed.
    // Guards the version-check open path: a leaked connection here would lock the file so the
    // rebuild's File.Delete throws (regression if IndexStore.Open stops disposing on failure).
    [Fact]
    public void CorruptIndexIsRebuiltOnOpen()
    {
        string root = Directory.CreateTempSubdirectory("codenav-corrupt").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 4, seed: 5);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            File.WriteAllText(dbPath, "this is not a sqlite database"); // corrupt header

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            for (int i = 0; i < 200 && !manager.IsQueryable; i++) Thread.Sleep(50);
            Assert.True(manager.IsQueryable, "corrupt index did not recover via rebuild");

            using var q = new IndexQueries(dbPath);
            Assert.True(q.Overview().Symbols > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }
}
