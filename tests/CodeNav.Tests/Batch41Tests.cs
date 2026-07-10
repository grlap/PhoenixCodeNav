using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 41 (v0.10.0) — worktree support for the review-system integration:
/// fgq — pins what was designed-but-untested (gitdir-FILE redirection in a linked worktree,
///       index relocatability — everything stored is workspace-relative — and the VACUUM INTO
///       snapshot primitive: consistent from a LIVE db, compacted, refuses to clobber);
/// c36 — the 'worktrees' + 'index_worktree' tools: seed a sibling worktree's index from a
///       snapshot and reconcile with two read-only git calls (commit diff UNION status dirt),
///       target validation against git's own list, own-workspace redirect, and the
///       worktree_index_locked ownership honesty. Phoenix never creates/removes worktrees.
/// All git-driven tests are env-guarded on GitInfo.GitAvailable (Batch 5/25 pattern).
/// </summary>
public class Batch41Tests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void LinkedWorktreePlumbingResolvesGitDirAndListsAndSeesDirt()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-41-plumb").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b review \"{wt}\"");

            // The .git FILE redirection: a linked worktree's resolved gitdir lives under the
            // MAIN repo's .git/worktrees/<name> — the per-worktree HEAD/logs GitWatcher watches.
            string? gitDir = GitInfo.ResolveGitDir(wt);
            Assert.NotNull(gitDir);
            Assert.Contains("worktrees", gitDir!.Replace('\\', '/'));

            // Enumeration works from EITHER root and carries head+branch.
            var fromMain = GitInfo.Worktrees(root);
            var fromWt = GitInfo.Worktrees(wt);
            Assert.NotNull(fromMain);
            Assert.NotNull(fromWt);
            Assert.Equal(2, fromMain!.Count);
            Assert.Equal(2, fromWt!.Count);
            var wtEntry = fromMain.First(w => w.Path.Replace('\\', '/').TrimEnd('/')
                .Equals(wt.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("review", wtEntry.Branch);
            Assert.NotNull(wtEntry.Head);

            // Dirt detection (the status half of the reconcile): clean -> empty; an
            // uncommitted edit shows up repo-root-relative with forward slashes.
            Assert.Empty(GitInfo.DirtyFiles(wt)!);
            File.WriteAllText(Path.Combine(wt, "Lab", "Dirty.cs"), "namespace Lab { class Dirty41 { } }");
            Assert.Contains("Lab/Dirty.cs", GitInfo.DirtyFiles(wt)!);

            // Review F1 (reproduced): git emits paths as UTF-8 BYTES; the console-codepage
            // decode mangled every non-ASCII path ('Ünïcode' -> '├£n├»code') and the
            // watcherless worktree reconcile silently LOST such files while reporting
            // success. Both halves must round-trip exactly: status dirt...
            File.WriteAllText(Path.Combine(wt, "Lab", "Ünïcode Dirt.cs"), "namespace Lab { class UnicodeDirt41 { } }");
            var dirty = GitInfo.DirtyFiles(wt)!;
            Assert.Contains("Lab/Ünïcode Dirt.cs", dirty);
            // ...and the commit diff (core.quotepath would octal-escape-and-quote it there).
            string? baseline = GitInfo.HeadCommit(wt);
            File.WriteAllText(Path.Combine(wt, "Lab", "Ärger.cs"), "namespace Lab { class Committed41Unicode { } }");
            Git(wt, "add -A");
            Git(wt, "commit -q -m unicode");
            var moved = GitInfo.ChangedFiles(wt, baseline!, GitInfo.HeadCommit(wt)!);
            Assert.NotNull(moved);
            Assert.Contains("Lab/Ärger.cs", moved!);
        }
        finally { Cleanup(root); CleanupWorktree(root, wt); }
    }

    // Review F2 (reproduced): a BARE entry in the worktree list (bare-main + linked worktrees —
    // a real monolith layout) has no working tree; the unguarded Ensure seeded a junk index
    // INSIDE the bare repository directory and reported success.
    [Fact]
    public void BareWorktreeEntriesAreRefusedNotIndexed()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-41-bare").FullName);
        string bare = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-bare.git"));
        string co = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-co"));
        try
        {
            WriteRepo(root);
            Git(Path.GetDirectoryName(root)!, $"clone --bare -q \"{root}\" \"{bare}\"");
            Git(bare, $"worktree add \"{co}\"");

            string coDb = IndexBuilder.DefaultDbPath(co);
            IndexBuilder.Build(co, coDb);

            var listed = GitInfo.Worktrees(co)!;
            var bareEntry = listed.First(w => w.Head is null);
            var result = WorktreeIndexer.Ensure(co, coDb, bareEntry.Path, "auto", _ => { });
            Assert.Equal("worktree_not_indexable", result.Action);
            Assert.False(Directory.Exists(Path.Combine(bare, ".codenav")),
                "nothing may be written into the bare repository directory");
        }
        finally
        {
            Cleanup(root);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(co, recursive: true); } catch { }
            try { Directory.Delete(bare, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SnapshotIsRelocatableConsistentFromALiveDbAndRefusesToClobber()
    {
        string rootA = Directory.CreateTempSubdirectory("codenav-41-snapA").FullName;
        string rootB = Directory.CreateTempSubdirectory("codenav-41-snapB").FullName;
        try
        {
            WriteLab(rootA);
            WriteLab(rootB); // same CONTENT under a different absolute root
            string dbA = IndexBuilder.DefaultDbPath(rootA);
            IndexBuilder.Build(rootA, dbA);

            // Snapshot from a LIVE manager (pump running, WAL active) — must open cleanly,
            // pass integrity_check, and answer queries under the OTHER root (relocatability:
            // everything stored is workspace-relative).
            using (var m = new IndexManager(rootA, dbA))
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 20000));
                string dbB = IndexBuilder.DefaultDbPath(rootB);
                IndexStore.SnapshotTo(dbA, dbB);

                using var conn = new SqliteConnection($"Data Source={dbB};Mode=ReadOnly;Pooling=false");
                conn.Open();
                using var check = conn.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                Assert.Equal("ok", (string)check.ExecuteScalar()!);

                using var qB = new IndexQueries(dbB);
                Assert.Single(qB.SearchSymbols("Alpha41", "exact", null, 2));
                Assert.Single(qB.ProjectsContaining("Lab/Alpha.cs"));

                // No silent clobber — the caller deletes explicitly. Pin the GUARD's message:
                // a bare Throws<IOException> passed vacuously under a guard-removed probe
                // (the probe's own delete threw delete-in-use IOException instead).
                var clobber = Assert.Throws<IOException>(() => IndexStore.SnapshotTo(dbA, dbB));
                Assert.Contains("already exists", clobber.Message);
            }
        }
        finally { Cleanup(rootA); Cleanup(rootB); }
    }

    [Fact]
    public void IndexWorktreeSeedsReconcilesCommitsAndDirtWithoutTouchingMain()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-41-e2e").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b review \"{wt}\"");
            // Diverge the worktree: a COMMITTED new symbol (the commit-diff half)...
            File.WriteAllText(Path.Combine(wt, "Lab", "Committed.cs"), "namespace Lab { public class Committed41 { } }");
            Git(wt, "add -A");
            Git(wt, "commit -q -m review-change");
            // ...and an UNCOMMITTED one (the status-dirt half).
            File.WriteAllText(Path.Combine(wt, "Lab", "Dirty.cs"), "namespace Lab { public class Dirty41 { } }");

            string db = IndexBuilder.DefaultDbPath(root);
            using var m = new IndexManager(root, db);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable && m.Health().IndexedCommit is not null, 30000),
                "main index did not record its git baseline");
            var tools = new NavigationTools(m, new SemanticService(m));

            // The listing sees both worktrees; the sibling has no index yet.
            var listed = Parse(tools.Worktrees()).GetProperty("worktrees").EnumerateArray().ToList();
            Assert.Equal(2, listed.Count);
            var sibling = listed.First(w => !w.TryGetProperty("isThisWorkspace", out _));
            Assert.False(sibling.GetProperty("hasIndex").GetBoolean());

            // Seed + reconcile in one call — the targeted path, not the sweep.
            var created = Parse(tools.IndexWorktree(wt));
            Assert.Equal("created", created.GetProperty("action").GetString());
            Assert.False(created.GetProperty("usedFullSweep").GetBoolean(),
                "expected the git-diff-union-status path, not the full sweep");

            string wtDb = IndexBuilder.DefaultDbPath(wt);
            using (var qWt = new IndexQueries(wtDb))
            {
                Assert.Single(qWt.SearchSymbols("Alpha41", "exact", null, 2));     // seeded content
                Assert.Single(qWt.SearchSymbols("Committed41", "exact", null, 2)); // commit-diff half
                Assert.Single(qWt.SearchSymbols("Dirty41", "exact", null, 2));     // status-dirt half
            }
            // Isolation: the MAIN index never saw the worktree's symbols.
            using (var qMain = m.OpenQueries())
            {
                Assert.Empty(qMain.SearchSymbols("Committed41", "exact", null, 2));
                Assert.Empty(qMain.SearchSymbols("Dirty41", "exact", null, 2));
            }

            // The listing now reports the seeded index as current and in sync.
            var relisted = Parse(tools.Worktrees()).GetProperty("worktrees").EnumerateArray().ToList();
            var siblingAfter = relisted.First(w => !w.TryGetProperty("isThisWorkspace", out _));
            Assert.True(siblingAfter.GetProperty("hasIndex").GetBoolean());
            Assert.True(siblingAfter.GetProperty("schemaCurrent").GetBoolean());
            Assert.True(siblingAfter.GetProperty("inSyncWithHead").GetBoolean());

            // Refresh picks up NEW dirt on an existing index.
            File.WriteAllText(Path.Combine(wt, "Lab", "Later.cs"), "namespace Lab { public class Later41 { } }");
            var refreshed = Parse(tools.IndexWorktree(wt, "refresh"));
            Assert.Equal("refreshed", refreshed.GetProperty("action").GetString());
            using (var qWt2 = new IndexQueries(wtDb))
            {
                Assert.Single(qWt2.SearchSymbols("Later41", "exact", null, 2));
            }
        }
        finally { Cleanup(root); CleanupWorktree(root, wt); }
    }

    [Fact]
    public void IndexWorktreeGuardsValidateOwnershipAndTargets()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-41-guard").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b review \"{wt}\"");
            string db = IndexBuilder.DefaultDbPath(root);
            using var m = new IndexManager(root, db);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000));
            var tools = new NavigationTools(m, new SemanticService(m));

            Assert.Equal("bad_request", Parse(tools.IndexWorktree(wt, "yolo")).GetProperty("error").GetString());
            Assert.Equal("worktree_not_found",
                Parse(tools.IndexWorktree(Path.Combine(root, ".."))).GetProperty("error").GetString());
            Assert.Equal("own_workspace", Parse(tools.IndexWorktree(root)).GetProperty("error").GetString());

            // Ownership honesty: any live handle on the sibling's db (a review session's own
            // phoenix) must yield worktree_index_locked, never a write into a foreign pump.
            Parse(tools.IndexWorktree(wt)); // seed it first
            string wtDb = IndexBuilder.DefaultDbPath(wt);
            using (var hold = new FileStream(wtDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var locked = Parse(tools.IndexWorktree(wt, "refresh"));
                Assert.Equal("worktree_index_locked", locked.GetProperty("error").GetString());
            }
            // Handle released — the same call succeeds again.
            Assert.Equal("refreshed", Parse(tools.IndexWorktree(wt, "refresh")).GetProperty("action").GetString());
        }
        finally { Cleanup(root); CleanupWorktree(root, wt); }
    }

    // ---------------------------------------------------------------- fixture + helpers

    private static void WriteLab(string root)
    {
        string dir = Path.Combine(root, "Lab");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Lab.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Alpha.cs"),
            "namespace Lab { public class Alpha41 { public void Go() { } } }");
    }

    private static void WriteRepo(string root)
    {
        WriteLab(root);
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
        Git(root, "init -q");
        Git(root, "config user.email test@example.com");
        Git(root, "config user.name CodeNavTest");
        Git(root, "config commit.gpgsign false");
        Git(root, "add -A");
        Git(root, "commit -q -m init");
    }

    private static void Git(string dir, string args) =>
        GitInfo.RunProcess("git", dir,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }

    private static void CleanupWorktree(string mainRoot, string wt)
    {
        SqliteConnection.ClearAllPools();
        try { Git(mainRoot, $"worktree remove --force \"{wt}\""); } catch { }
        try { Directory.Delete(wt, recursive: true); } catch { /* already removed / locks */ }
    }
}
