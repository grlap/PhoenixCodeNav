using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Batch 50 (lf4p review): client-assigned symbol-id allocation must survive a rolled-back
/// refresh. The first batching cut read MAX(id) lazily UNDER the caller's transaction — a
/// delta refresh deletes a file's symbols and reinserts in one tx, so the lazy read observed
/// the tx's own uncommitted deletes; after a rollback (a designed abort: csproj capture race,
/// hash mismatch) the restored rows sat ABOVE the counter, and the next refresh of a
/// DIFFERENT file collided on UNIQUE symbols.id — dropping its whole batch (stale index,
/// user-visible refresh failure). The fix reads committed MAX at store OPEN; this test
/// replays the reviewer's two-file kill-chain and is the deterministic reintroduction pin:
/// restore the lazy in-tx read and it goes red with the UNIQUE violation.
/// </summary>
public class Batch50SymbolIdTests
{
    [Fact]
    public void RolledBackRefreshDoesNotPoisonSymbolIdAllocationForOtherFiles()
    {
        string root = Directory.CreateTempSubdirectory("codenav-50-ids").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(proj, "One.cs"),
                "namespace S { public class One { public void A() { } public void B() { } " +
                "public void C() { } public void D() { } } }");
            File.WriteAllText(Path.Combine(proj, "Two.cs"),
                "namespace S { public class Two { public void X() { } public void Y() { } } }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            // Order-agnostic: the ROLLBACK target must be the file owning the TOP ids (its
            // restored rows sit above a poisoned counter); the SECOND refresh hits the other.
            long topFileId, otherFileId;
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                       IndexQueries.ReadConnectionString(dbPath, pinReadSnapshot: false, pooling: false)))
            {
                conn.Open();
                using var top = conn.CreateCommand();
                top.CommandText = "SELECT file_id FROM symbols ORDER BY id DESC LIMIT 1";
                topFileId = (long)top.ExecuteScalar()!;
                using var other = conn.CreateCommand();
                other.CommandText = "SELECT id FROM files WHERE lang='cs' AND id != $t LIMIT 1";
                other.Parameters.AddWithValue("$t", topFileId);
                otherFileId = (long)other.ExecuteScalar()!;
            }

            // Fresh store, as after a server restart: the counter initializes HERE, from
            // committed state — that is the invariant under test.
            using var store = new IndexStore(dbPath, createNew: false);
            var oneRow = new List<SymbolRow>
            {
                new(OrdinalInFile: 0, ParentOrdinal: -1, Kind: "class", Name: "R",
                    Namespace: "S", Container: null, Signature: "R", Accessibility: "public",
                    StartLine: 1, EndLine: 1, IsPartial: false, Arity: 0, AttrMarkers: null),
            };

            // T1: refresh the TOP-id file — delete its symbols, reinsert fewer — then the
            // designed abort rolls everything back (dispose == rollback). The deleted top
            // rows come back; a lazily-in-tx-initialized counter is now stranded below them.
            using (var t1 = store.BeginTransaction())
            {
                store.DeleteSymbolsForFile(t1, topFileId);
                store.InsertSymbols(t1, topFileId, oneRow);
            }

            // T2: refresh the OTHER file. Its delete does not touch the restored top rows, so
            // a poisoned counter allocates straight into them: UNIQUE constraint failed.
            using (var t2 = store.BeginTransaction())
            {
                store.DeleteSymbolsForFile(t2, otherFileId);
                store.InsertSymbols(t2, otherFileId, oneRow); // must not throw
                t2.Commit();
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }
}
