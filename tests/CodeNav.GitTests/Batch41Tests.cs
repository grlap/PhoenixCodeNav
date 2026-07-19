using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
        if (!GitInfo.GitAvailable || OperatingSystem.IsMacOS()) return;
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
        if (!GitInfo.GitAvailable || OperatingSystem.IsMacOS()) return;
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
            // kae review: co holds coDb (bare holds nothing) — clear each root it deletes.
            TestWorkspaceCleanup.ClearIndexPools(co);
            TestWorkspaceCleanup.ClearIndexPools(bare);
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

                using var conn = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = dbB,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString());
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
        if (!GitInfo.GitAvailable || OperatingSystem.IsMacOS()) return;
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

            // Seed + reconcile in one call — Windows targets commit+dirt; Linux performs the
            // mandatory anchored full sweep.
            var created = Parse(tools.IndexWorktree(wt));
            Assert.Equal("created", ActionOf(created, "create leg"));
            AssertNoTemporaryIndexArtifacts(Path.Combine(wt, ".codenav"));
            Assert.Equal(OperatingSystem.IsLinux(),
                created.GetProperty("usedFullSweep").GetBoolean());

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

            // Refresh picks up NEW dirt on an existing index. Keep a tracked path in distinct
            // staged + worktree layers as well: review_pack must refuse to certify those layers,
            // but index reconciliation only needs the complete final-worktree path union and must
            // not fall back to a repository-wide sweep.
            File.WriteAllText(Path.Combine(wt, "Lab", "Later.cs"), "namespace Lab { public class Later41 { } }");
            string alpha = Path.Combine(wt, "Lab", "Alpha.cs");
            File.AppendAllText(alpha, "\n// staged layer\n");
            Git(wt, "add Lab/Alpha.cs");
            File.AppendAllText(alpha, "// final worktree layer\n");
            var refreshed = Parse(tools.IndexWorktree(wt, "refresh"));
            Assert.Equal("refreshed", ActionOf(refreshed, "refresh leg"));
            AssertNoTemporaryIndexArtifacts(Path.Combine(wt, ".codenav"));
            Assert.Equal(OperatingSystem.IsLinux(),
                refreshed.GetProperty("usedFullSweep").GetBoolean());
            using (var qWt2 = new IndexQueries(wtDb))
            {
                Assert.Single(qWt2.SearchSymbols("Later41", "exact", null, 2));
            }

            // An unresolved merge is also a complete final-worktree path manifest for indexing,
            // even though review_pack correctly refuses to certify conflict bytes.
            Git(wt, "add -A");
            Git(wt, "commit -q -m layered-baseline");
            Git(wt, "checkout -q -b conflict-side");
            File.WriteAllText(alpha, "namespace Lab { public class ConflictSide41 { } }");
            Git(wt, "commit -qam conflict-side");
            Git(wt, "checkout -q review");
            File.WriteAllText(alpha, "namespace Lab { public class ConflictMain41 { } }");
            Git(wt, "commit -qam conflict-main");
            var merge = GitInfo.RunProcessEx(
                GitInfo.ResolveGitExeFrom(null, Environment.GetEnvironmentVariable("PATH"))!,
                wt, "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " +
                    "merge --no-edit conflict-side", waitMs: 20_000);
            Assert.Equal("exit_nonzero", merge.Status);

            var conflictRefresh = Parse(tools.IndexWorktree(wt, "refresh"));
            Assert.Equal("refreshed", conflictRefresh.GetProperty("action").GetString());
            Assert.False(conflictRefresh.GetProperty("usedFullSweep").GetBoolean(),
                "a complete unmerged dirt manifest should drive a targeted reconcile");
        }
        finally { Cleanup(root); CleanupWorktree(root, wt); }
    }

    [Fact]
    public void LinuxCaseDistinctWorktreeRootsNeverAlias()
    {
        if (!OperatingSystem.IsLinux() || !GitInfo.GitAvailable) return;
        string container = Directory.CreateTempSubdirectory("codenav-41-case").FullName;
        string root = Path.Combine(container, "CaseRoot");
        string wt = Path.Combine(container, "caseroot");
        try
        {
            Directory.CreateDirectory(root);
            WriteRepo(root);
            Git(root, $"worktree add -b case-review \"{wt}\"");

            string db = IndexBuilder.DefaultDbPath(root);
            using var manager = new IndexManager(root, db);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));

            List<WorktreeIndexStatus> statuses =
                Assert.IsType<List<WorktreeIndexStatus>>(WorktreeIndexer.Status(root));
            WorktreeIndexStatus current = Assert.Single(statuses,
                status => status.IsThisWorkspace);
            Assert.True(CodeNav.Core.WorkspacePaths.FullPathsEqual(current.Path, root));
            Assert.Contains(statuses, status =>
                CodeNav.Core.WorkspacePaths.FullPathsEqual(status.Path, wt) &&
                !status.IsThisWorkspace);

            var tools = new NavigationTools(manager, semantic);
            JsonElement sibling = Parse(tools.IndexWorktree(wt, "refresh"));
            Assert.Equal("worktree_index_missing", sibling.GetProperty("error").GetString());
        }
        finally
        {
            CleanupWorktree(root, wt);
            Cleanup(container);
        }
    }

    [Fact]
    public void WorktreeIndexesMapConfiguredSubtreeOntoEveryGitWorktreeRoot()
    {
        if (!GitInfo.GitAvailable || OperatingSystem.IsMacOS()) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-subtree").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b subtree-review \"{wt}\"");
            string workspace = Path.Combine(root, "Lab");
            string siblingWorkspace = Path.Combine(wt, "Lab");

            File.WriteAllText(Path.Combine(siblingWorkspace, "SubtreeOnly.cs"),
                "namespace Lab { public class SubtreeOnly41 { } }");
            Git(wt, "add -A");
            Git(wt, "commit -q -m subtree-change");

            string db = IndexBuilder.DefaultDbPath(workspace);
            using var manager = new IndexManager(workspace, db);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable &&
                manager.Health().IndexedCommit is not null, 30_000));
            var tools = new NavigationTools(manager, semantic);

            List<JsonElement> listed = Parse(tools.Worktrees()).GetProperty("worktrees")
                .EnumerateArray().ToList();
            Assert.Equal(2, listed.Count);
            Assert.Single(listed, entry =>
                entry.TryGetProperty("isThisWorkspace", out JsonElement current) &&
                current.GetBoolean() &&
                CodeNav.Core.WorkspacePaths.FullPathsEqual(
                    entry.GetProperty("path").GetString()!, workspace));
            Assert.Contains(listed, entry =>
                CodeNav.Core.WorkspacePaths.FullPathsEqual(
                    entry.GetProperty("path").GetString()!, siblingWorkspace));

            JsonElement created = Parse(tools.IndexWorktree(siblingWorkspace));
            Assert.Equal("created", created.GetProperty("action").GetString());
            Assert.False(created.GetProperty("usedFullSweep").GetBoolean());
            string siblingDb = IndexBuilder.DefaultDbPath(siblingWorkspace);
            Assert.True(File.Exists(siblingDb));
            Assert.False(Directory.Exists(Path.Combine(wt, ".codenav")),
                "a subtree-scoped Phoenix must never create an index at the sibling repo root");
            using var queries = new IndexQueries(siblingDb);
            Assert.Single(queries.SearchSymbols("Alpha41", "exact", null, 2));
            Assert.Single(queries.SearchSymbols("SubtreeOnly41", "exact", null, 2));
        }
        finally
        {
            CleanupWorktree(root, wt);
            Cleanup(root);
        }
    }

    [Fact]
    public void IndexWorktreeGuardsValidateOwnershipAndTargets()
    {
        if (!GitInfo.GitAvailable || OperatingSystem.IsMacOS()) return;
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
            string invalidPath = "invalid\0worktree";
            Assert.Equal("bad_request",
                Parse(tools.IndexWorktree(invalidPath)).GetProperty("error").GetString());
            Assert.Equal("bad_request",
                WorktreeIndexer.Ensure(root, db, invalidPath, "auto", _ => { }).Action);

            // Ownership honesty is Phoenix-to-Phoenix, not inferred from SQLite/native sharing.
            Parse(tools.IndexWorktree(wt)); // seed it first
            string wtDb = IndexBuilder.DefaultDbPath(wt);
            using (var siblingManager = new IndexManager(wt, wtDb))
            {
                siblingManager.Start();
                Assert.True(WaitUntil(() => siblingManager.IsQueryable, 20_000));
                var locked = Parse(tools.IndexWorktree(wt, "refresh"));
                Assert.Equal("worktree_index_locked", locked.GetProperty("error").GetString());
            }
            // Phoenix lease released — the same call succeeds again.
            Assert.Equal("refreshed", Parse(tools.IndexWorktree(wt, "refresh")).GetProperty("action").GetString());
        }
        finally { Cleanup(root); CleanupWorktree(root, wt); }
    }

    [Fact]
    public void UnixMappedWorkspaceSymlinkIsRefusedWithoutTouchingExternalIndexPath()
    {
        if (!OperatingSystem.IsLinux() || !GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-link-root").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        string external = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-link-external").FullName);
        string siblingWorkspace = Path.Combine(wt, "Lab");
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b linked-workspace-review \"{wt}\"");
            string mainWorkspace = Path.Combine(root, "Lab");
            string mainDb = IndexBuilder.DefaultDbPath(mainWorkspace);
            IndexBuilder.Build(mainWorkspace, mainDb);

            string externalIndexDirectory = Path.Combine(external, ".codenav");
            Directory.CreateDirectory(externalIndexDirectory);
            string marker = Path.Combine(externalIndexDirectory, "do-not-touch.txt");
            File.WriteAllText(marker, "external-marker");
            Directory.Delete(siblingWorkspace, recursive: true);
            Directory.CreateSymbolicLink(siblingWorkspace, external);

            List<WorktreeIndexStatus> statuses =
                Assert.IsType<List<WorktreeIndexStatus>>(WorktreeIndexer.Status(mainWorkspace));
            WorktreeIndexStatus linked = Assert.Single(statuses,
                status => CodeNav.Core.WorkspacePaths.FullPathsEqual(
                    status.Path, siblingWorkspace));
            Assert.False(linked.HasIndex);

            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                mainWorkspace, mainDb, siblingWorkspace, "create", _ => { });
            Assert.Equal("worktree_not_indexable", result.Action);
            Assert.Equal("external-marker", File.ReadAllText(marker));
            Assert.False(File.Exists(Path.Combine(externalIndexDirectory, "index.db")));
        }
        finally
        {
            try
            {
                if (Directory.Exists(siblingWorkspace) || File.Exists(siblingWorkspace))
                    Directory.Delete(siblingWorkspace);
            }
            catch { }
            CleanupWorktree(root, wt);
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void WindowsIndexDestinationJunctionIsRefusedWithoutTouchingExternalIndexPath()
    {
        if (!OperatingSystem.IsWindows() || !GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-junction-root").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        string external = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-junction-external").FullName);
        string junction = Path.Combine(wt, ".codenav");
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b junction-review \"{wt}\"");
            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);

            string marker = Path.Combine(external, "do-not-touch.txt");
            File.WriteAllText(marker, "external-marker");
            CreateJunction(junction, external);

            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });
            Assert.Equal("worktree_not_indexable", result.Action);
            Assert.Equal("external-marker", File.ReadAllText(marker));
            Assert.False(File.Exists(Path.Combine(external, "index.db")));
        }
        finally
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction); } catch { }
            CleanupWorktree(root, wt);
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void ForeignPhoenixProcessLeaseBlocksCreateAndRefreshAndReleasesAfterCrash()
    {
        if (!GitInfo.GitAvailable || OperatingSystem.IsMacOS()) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-process-lease").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        Process? child = null;
        Task<string>? stderr = null;
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b process-lease-review \"{wt}\"");
            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            var seedLogs = new List<string>();
            WorktreeIndexResult seed = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", seedLogs.Add);
            Assert.True(seed.Action == "created",
                $"{seed.Action}: {seed.Detail}; {string.Join(" | ", seedLogs)}");
            string wtDb = IndexBuilder.DefaultDbPath(wt);

            child = StartPhoenixProcess(wt, wtDb);
            stderr = child.StandardError.ReadToEndAsync();
            Task<string> stdout = child.StandardOutput.ReadToEndAsync();
            Assert.True(WaitUntil(() => child.HasExited || IndexOwnershipLease.IsHeld(wt, wtDb),
                20_000), "child phoenix never acquired its index ownership lease");
            Assert.False(child.HasExited,
                $"child phoenix exited before holding the lease: {CompletedText(stderr)}");
            Assert.True(WaitUntil(() => File.Exists(wtDb + "-wal"), 10_000),
                "child Phoenix never opened the target WAL");

            WorktreeIndexResult create = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });
            WorktreeIndexResult refresh = WorktreeIndexer.Ensure(
                root, mainDb, wt, "refresh", _ => { });
            Assert.Equal("worktree_index_locked", create.Action);
            Assert.Equal("worktree_index_locked", refresh.Action);
            Assert.True(File.Exists(wtDb));

            child.Kill(entireProcessTree: true); // crash semantics: no cooperative Dispose
            Assert.True(child.WaitForExit(10_000));
            Assert.True(WaitUntil(() => !IndexOwnershipLease.IsHeld(wt, wtDb), 10_000),
                "kernel did not release the ownership lease after process death");
            WorktreeIndexResult recoveryRequired = WorktreeIndexer.Ensure(
                root, mainDb, wt, "refresh", _ => { });
            Assert.Equal("worktree_not_indexable", recoveryRequired.Action);
            using (var recover = new IndexStore(wtDb, createNew: false))
                recover.CheckpointForAtomicInstall();
            IndexQueries.ClearPoolsFor(wtDb); // kae: scoped — mirror the product's install-path clear
            Assert.Equal("refreshed", WorktreeIndexer.Ensure(
                root, mainDb, wt, "refresh", _ => { }).Action);
            GC.KeepAlive(stdout);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                try { child.WaitForExit(10_000); } catch { }
            }
            child?.Dispose();
            CleanupWorktree(root, wt);
            Cleanup(root);
        }
    }

    [Fact]
    public void UnixLeaseIdentityCanonicalizesSymlinkAliasesOfTheSameDatabase()
    {
        if (OperatingSystem.IsWindows()) return;
        string real = Directory.CreateTempSubdirectory("codenav-41-lease-real").FullName;
        string alias = real + "-alias";
        try
        {
            Directory.CreateSymbolicLink(alias, real);
            string realDb = Path.Combine(real, "index.db");
            string aliasDb = Path.Combine(alias, "index.db");
            Assert.True(IndexOwnershipLease.TryAcquire(real, realDb,
                out IndexOwnershipLease? owner));
            using (owner!)
            {
                Assert.False(IndexOwnershipLease.TryAcquire(alias, aliasDb, out _));
            }
            Assert.True(IndexOwnershipLease.TryAcquire(alias, aliasDb,
                out IndexOwnershipLease? successor));
            successor!.Dispose();
        }
        finally
        {
            try { if (Directory.Exists(alias)) Directory.Delete(alias); } catch { }
            Cleanup(real);
        }
    }

    [Fact]
    public void UnixCheckToUseDestinationSwapCannotRedirectAtomicInstall()
    {
        if (!OperatingSystem.IsLinux() || !GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-anchor-swap").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        string external = Directory.CreateTempSubdirectory(
            "codenav-41-anchor-external").FullName;
        string indexDirectory = Path.Combine(wt, ".codenav");
        string pinnedDirectory = Path.Combine(wt, ".codenav-pinned");
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b anchor-swap-review \"{wt}\"");
            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            Directory.CreateDirectory(indexDirectory);
            string marker = Path.Combine(external, "do-not-touch.txt");
            File.WriteAllText(marker, "external-marker");

            WorktreeIndexer.BeforeAnchoredInstallForTest = installDb =>
            {
                if (!IsInstallFor(installDb, wt)) return; // foreign parallel-class Ensure
                Directory.Move(indexDirectory, pinnedDirectory);
                Directory.CreateSymbolicLink(indexDirectory, external);
            };
            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });

            Assert.Equal("worktree_not_indexable", result.Action);
            Assert.Equal("external-marker", File.ReadAllText(marker));
            Assert.False(File.Exists(Path.Combine(external, "index.db")));
            Assert.True(File.Exists(Path.Combine(pinnedDirectory, "index.db")),
                "the handle-relative install should remain bound to the pinned directory");
        }
        finally
        {
            WorktreeIndexer.BeforeAnchoredInstallForTest = null;
            try { if (Directory.Exists(indexDirectory)) Directory.Delete(indexDirectory); } catch { }
            try
            {
                if (Directory.Exists(pinnedDirectory))
                    Directory.Move(pinnedDirectory, indexDirectory);
            }
            catch { }
            CleanupWorktree(root, wt);
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void WindowsPinnedDestinationBlocksCheckToUseJunctionSwap()
    {
        if (!OperatingSystem.IsWindows() || !GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-anchor-win").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        string external = Directory.CreateTempSubdirectory(
            "codenav-41-anchor-win-external").FullName;
        string indexDirectory = Path.Combine(wt, ".codenav");
        string movedDirectory = Path.Combine(wt, ".codenav-moved");
        bool replacementBlocked = false;
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b anchor-win-review \"{wt}\"");
            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            Directory.CreateDirectory(indexDirectory);
            string marker = Path.Combine(external, "do-not-touch.txt");
            File.WriteAllText(marker, "external-marker");

            WorktreeIndexer.BeforeAnchoredInstallForTest = installDb =>
            {
                if (!IsInstallFor(installDb, wt)) return; // foreign parallel-class Ensure
                // Only a blocked MOVE may set replacementBlocked. The junction step must not be
                // inside the same catch: a junction-creation failure after a SUCCESSFUL move
                // would otherwise masquerade as "pin held" while the destination is gone.
                try
                {
                    Directory.Move(indexDirectory, movedDirectory);
                }
                catch (IOException)
                {
                    replacementBlocked = true;
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    replacementBlocked = true;
                    return;
                }
                CreateJunction(indexDirectory, external); // move slipped the pin: finish the swap
            };
            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });

            Assert.True(replacementBlocked,
                "the pinned destination must deny rename/replacement until install completes");
            Assert.Equal("created", result.Action);
            Assert.Equal("external-marker", File.ReadAllText(marker));
            Assert.False(File.Exists(Path.Combine(external, "index.db")));
            Assert.True(File.Exists(Path.Combine(indexDirectory, "index.db")),
                "installed db missing at pinned destination — " +
                $"action='{result.Action}' detail='{result.Detail}' " +
                $"destinationExists={Directory.Exists(indexDirectory)} " +
                $"destinationEntries=[{DirectoryEntryNames(indexDirectory)}] " +
                $"movedExists={Directory.Exists(movedDirectory)} " +
                $"movedEntries=[{DirectoryEntryNames(movedDirectory)}] " +
                $"externalEntries=[{DirectoryEntryNames(external)}]");
        }
        finally
        {
            WorktreeIndexer.BeforeAnchoredInstallForTest = null;
            try { if (Directory.Exists(indexDirectory)) Directory.Delete(indexDirectory, true); } catch { }
            try
            {
                if (Directory.Exists(movedDirectory))
                    Directory.Move(movedDirectory, indexDirectory);
            }
            catch { }
            CleanupWorktree(root, wt);
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void GitPathComparerUsesHostCaseSemantics()
    {
        var caseSensitive = new HashSet<string>(
            WorktreeIndexer.GitPathComparerForHost(isWindows: false))
        {
            "Lab/Case.cs",
            "Lab/case.cs",
        };
        var windows = new HashSet<string>(
            WorktreeIndexer.GitPathComparerForHost(isWindows: true))
        {
            "Lab/Case.cs",
            "Lab/case.cs",
        };
        Assert.Equal(2, caseSensitive.Count);
        Assert.Single(windows);
    }

    [Fact]
    public void LinuxAnchoredFullSweepKeepsCaseDistinctGitPaths()
    {
        if (!OperatingSystem.IsLinux() || !GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-41-case-paths").FullName);
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b case-path-review \"{wt}\"");
            File.WriteAllText(Path.Combine(wt, "Lab", "Case.cs"),
                "namespace Lab { public class UpperCasePath41 { } }");
            Git(wt, "add Lab/Case.cs");
            Git(wt, "commit -q -m upper-case-path");
            File.WriteAllText(Path.Combine(wt, "Lab", "case.cs"),
                "namespace Lab { public class LowerCasePath41 { } }");

            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            using (var store = new IndexStore(mainDb, createNew: false))
            {
                store.SetMeta("indexed_commit", GitInfo.HeadCommit(root)!);
            }

            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });
            Assert.Equal("created", result.Action);
            Assert.True(result.UsedFullSweep);
            using var queries = new IndexQueries(IndexBuilder.DefaultDbPath(wt));
            Assert.Single(queries.SearchSymbols("UpperCasePath41", "exact", null, 2));
            Assert.Single(queries.SearchSymbols("LowerCasePath41", "exact", null, 2));
            using var worktreeStore = new IndexStore(IndexBuilder.DefaultDbPath(wt), createNew: false);
            Assert.Equal(result.IndexedCommit, worktreeStore.GetMeta("indexed_commit"));
        }
        finally
        {
            CleanupWorktree(root, wt);
            Cleanup(root);
        }
    }

    [Fact]
    public void WorktreePlatformPolicyIsExplicitAndSingular()
    {
        Assert.Equal(WorktreeIndexPlatformPolicy.WindowsTargeted,
            WorktreeIndexer.PlatformPolicyForHost(isWindows: true, isLinux: false));
        Assert.Equal(WorktreeIndexPlatformPolicy.LinuxAnchoredFullSweep,
            WorktreeIndexer.PlatformPolicyForHost(isWindows: false, isLinux: true));
        Assert.Equal(WorktreeIndexPlatformPolicy.Unsupported,
            WorktreeIndexer.PlatformPolicyForHost(isWindows: false, isLinux: false));
        Assert.Equal("unsupported_platform",
            NavigationTools.WorktreeUnavailableForHost(isMacOS: true).Error);
        Assert.Equal("git_unavailable",
            NavigationTools.WorktreeUnavailableForHost(isMacOS: false).Error);
    }

    [Fact]
    public void NormalIndexAuthorityRejectsLinkedDefaultParentWithoutTouchingTarget()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-main-parent").FullName;
        string external = Directory.CreateTempSubdirectory(
            "codenav-41-main-parent-external").FullName;
        string link = Path.Combine(root, ".codenav");
        try
        {
            WriteLab(root);
            string marker = Path.Combine(external, "do-not-touch.txt");
            File.WriteAllText(marker, "external-marker");
            if (OperatingSystem.IsWindows()) CreateJunction(link, external);
            else Directory.CreateSymbolicLink(link, external);

            using var manager = new IndexManager(root);
            manager.Start();
            Assert.True(WaitUntil(() => manager.State == "failed", 10_000));
            Assert.Contains("destination", manager.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);
            Assert.Throws<IOException>(() => IndexBuilder.Build(root));
            Assert.Equal("external-marker", File.ReadAllText(marker));
            Assert.False(File.Exists(Path.Combine(external, "index.db")));
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void UnixNormalIndexAuthorityRejectsDanglingParentAndDatabaseLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-main-dangling").FullName;
        string missingParent = root + "-missing-parent";
        string missingDatabase = root + "-missing-index.db";
        string indexDirectory = Path.Combine(root, ".codenav");
        try
        {
            WriteLab(root);
            Directory.CreateSymbolicLink(indexDirectory, missingParent);
            Assert.Throws<IOException>(() => IndexBuilder.Build(root));
            Assert.False(Directory.Exists(missingParent));
            Directory.Delete(indexDirectory);

            Directory.CreateDirectory(indexDirectory);
            string database = Path.Combine(indexDirectory, "index.db");
            File.CreateSymbolicLink(database, missingDatabase);
            using var manager = new IndexManager(root);
            manager.Start();
            Assert.True(WaitUntil(() => manager.State == "failed", 10_000));
            Assert.Contains("destination", manager.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(missingDatabase));
        }
        finally
        {
            try { if (Directory.Exists(indexDirectory)) Directory.Delete(indexDirectory, true); } catch { }
            Cleanup(root);
            Cleanup(missingParent);
            try { File.Delete(missingDatabase); } catch { }
        }
    }

    [Fact]
    public void NormalIndexAuthorityRejectsHardlinkedDatabase()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-main-hardlink").FullName;
        string external = Directory.CreateTempSubdirectory(
            "codenav-41-main-hardlink-external").FullName;
        try
        {
            WriteLab(root);
            string indexDirectory = Path.Combine(root, ".codenav");
            Directory.CreateDirectory(indexDirectory);
            string externalFile = Path.Combine(external, "external.db");
            File.WriteAllText(externalFile, "external-marker");
            string database = Path.Combine(indexDirectory, "index.db");
            CreateHardLinkForTest(database, externalFile);

            Assert.Throws<IOException>(() => IndexBuilder.Build(root));
            Assert.Equal("external-marker", File.ReadAllText(externalFile));
        }
        finally
        {
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void UnixNormalIndexAuthorityRejectsDanglingSqliteSidecarLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-sidecar-link").FullName;
        try
        {
            WriteLab(root);
            string indexDirectory = Path.Combine(root, ".codenav");
            Directory.CreateDirectory(indexDirectory);
            string database = Path.Combine(indexDirectory, "index.db");
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            {
                string external = root + "-missing" + suffix;
                string sidecar = database + suffix;
                File.CreateSymbolicLink(sidecar, external);
                Assert.Throws<IOException>(() => IndexBuilder.Build(root, database));
                Assert.False(File.Exists(external));
                File.Delete(sidecar);
            }
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void WindowsNormalIndexAuthorityRejectsJunctionSqliteSidecars(string suffix)
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-sidecar-junction").FullName;
        string external = Directory.CreateTempSubdirectory(
            "codenav-41-sidecar-junction-external").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        string sidecar = database + suffix;
        try
        {
            WriteLab(root);
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            string marker = Path.Combine(external, "do-not-touch.txt");
            File.WriteAllText(marker, "external-marker");
            CreateJunction(sidecar, external);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(WaitUntil(() => manager.State == "failed", 10_000));
            Assert.Throws<IOException>(() => IndexBuilder.Build(root, database));
            Assert.Equal("external-marker", File.ReadAllText(marker));
            Assert.False(File.Exists(Path.Combine(external, "index.db")));
        }
        finally
        {
            try { if (Directory.Exists(sidecar)) Directory.Delete(sidecar); } catch { }
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void NormalBuildRemovesRegularRollbackJournalBeforeSqliteOpen()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-journal").FullName;
        try
        {
            WriteLab(root);
            string database = IndexBuilder.DefaultDbPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            File.WriteAllText(database + "-journal", "stale rollback bytes");

            IndexBuilder.Build(root, database);
            Assert.True(File.Exists(database));
            Assert.False(File.Exists(database + "-journal"));
            using var queries = new IndexQueries(database);
            Assert.Single(queries.SearchSymbols("Alpha41", "exact", null, 2));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DirectBuildReleasesLeaseAndNativeHandlesAfterSuccessAndSchemaFailure()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-build-handoff").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteLab(root);
            IndexStore.AfterOpenBeforeCreateSchemaForTest = path =>
            {
                if (CodeNav.Core.WorkspacePaths.FullPathsEqual(path, database))
                    throw new InvalidOperationException("simulated schema failure");
            };
            Assert.Throws<InvalidOperationException>(() => IndexBuilder.Build(root, database));
            Assert.False(IndexOwnershipLease.IsHeld(root, database));
            AssertNoSqliteSidecars(database);

            IndexStore.AfterOpenBeforeCreateSchemaForTest = null;
            IndexBuilder.Build(root, database);
            Assert.False(IndexOwnershipLease.IsHeld(root, database));
            AssertNoSqliteSidecars(database);

            File.WriteAllText(Path.Combine(root, "Second.cs"),
                "namespace Lab { public class SecondBuild41 { } }");
            IndexBuilder.Build(root, database);
            AssertNoSqliteSidecars(database);
            using var queries = new IndexQueries(database);
            Assert.Single(queries.SearchSymbols("SecondBuild41", "exact", null, 2));
        }
        finally
        {
            IndexStore.AfterOpenBeforeCreateSchemaForTest = null;
            Cleanup(root);
        }
    }

    [Fact]
    public void ExternalAndRelativeIndexDatabaseParentsAreCreatedAndUsable()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-external-root").FullName;
        string external = Directory.CreateTempSubdirectory(
            "codenav-41-external-db").FullName;
        try
        {
            WriteLab(root);
            string directDb = Path.Combine(external, "direct", "nested", "index.db");
            IndexBuilder.Build(root, directDb);
            Assert.True(File.Exists(directDb));
            using (var queries = new IndexQueries(directDb))
                Assert.Single(queries.SearchSymbols("Alpha41", "exact", null, 2));

            string relativeAbsolute = Path.Combine(external, "relative", "nested", "index.db");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, relativeAbsolute);
            using var manager = new IndexManager(root, relative);
            manager.Start();
            Assert.True(WaitUntil(() => manager.State is "ready" or "failed", 20_000));
            Assert.Equal("ready", manager.State);
            Assert.Equal(Path.GetFullPath(relativeAbsolute), manager.DbPath);
            Assert.True(File.Exists(relativeAbsolute));
            using var managerQueries = manager.OpenQueries();
            Assert.Single(managerQueries.SearchSymbols("Alpha41", "exact", null, 2));
        }
        finally
        {
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public async Task LinuxAnchoredFullSweepRefusesOversizedRegularSourceWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux() || !GitInfo.GitAvailable) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-fifo").FullName;
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        try
        {
            WriteRepo(root);
            File.WriteAllText(Path.Combine(root, "Lab", "SocketSeed.cs"),
                "namespace Lab { public class SocketSeed41 { } }");
            File.WriteAllText(Path.Combine(root, "Lab", "OversizeSeed.cs"),
                "namespace Lab { public class OversizeSeed41 { } }");
            Git(root, "add -A");
            Git(root, "commit -q -m special-file-seed");
            Git(root, $"worktree add -b fifo-review \"{wt}\"");
            string fifo = Path.Combine(wt, "Lab", "Alpha.cs");
            File.Delete(fifo);
            Assert.Equal(0, mkfifo(fifo, Convert.ToUInt32("600", 8)));
            string projectFifo = Path.Combine(wt, "Lab", "Lab.csproj");
            File.Delete(projectFifo);
            Assert.Equal(0, mkfifo(projectFifo, Convert.ToUInt32("600", 8)));
            string socketPath = Path.Combine(wt, "Lab", "SocketSeed.cs");
            File.Delete(socketPath);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream,
                ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            string oversized = Path.Combine(wt, "Lab", "OversizeSeed.cs");
            using (var sparse = new FileStream(oversized, FileMode.Open, FileAccess.Write,
                       FileShare.ReadWrite))
                sparse.SetLength((long)DeltaRefresher.MaxIndexedFileBytes + 1);

            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            Task<WorktreeIndexResult> reconcile = Task.Run(() => WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { }));
            WorktreeIndexResult result = await reconcile.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshInputOversizedCause, result.Action);
            Assert.True(result.UsedFullSweep);
            Assert.Equal(["Lab/OversizeSeed.cs"], result.IncompleteSourcePaths);
            Assert.False(File.Exists(IndexBuilder.DefaultDbPath(wt)),
                "strict worktree reconcile must not install a staged index with incomplete source coverage");
        }
        finally
        {
            CleanupWorktree(root, wt);
            Cleanup(root);
        }
    }

    [Fact]
    public async Task LinuxColdBuildSkipsSpecialSourceAndStructuralInputsWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-build-special").FullName;
        try
        {
            WriteLab(root);
            string sourceFifo = Path.Combine(root, "Lab", "Alpha.cs");
            File.Delete(sourceFifo);
            Assert.Equal(0, mkfifo(sourceFifo, Convert.ToUInt32("600", 8)));
            string projectFifo = Path.Combine(root, "Bad.csproj");
            Assert.Equal(0, mkfifo(projectFifo, Convert.ToUInt32("600", 8)));
            string packagesFifo = Path.Combine(root, "Lab", "packages.config");
            Assert.Equal(0, mkfifo(packagesFifo, Convert.ToUInt32("600", 8)));
            string solutionSocket = Path.Combine(root, "Socket.sln");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream,
                ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(solutionSocket));

            string database = IndexBuilder.DefaultDbPath(root);
            Task<BuildResult> build = Task.Run(() => IndexBuilder.Build(root, database));
            _ = await build.WaitAsync(TimeSpan.FromSeconds(20));

            using var queries = new IndexQueries(database);
            Assert.Empty(queries.SearchSymbols("Alpha41", "exact", null, 2));
            Assert.Empty(queries.FindFiles("Bad.csproj", 2));
            Assert.Empty(queries.FindFiles("Socket.sln", 2));
            Assert.Empty(queries.FindFiles("packages.config", 2));
            Assert.Null(queries.ProjectByName("Bad"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task LinuxOrdinaryFullSweepDeletesSeededRowsReplacedBySpecialFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-refresh-special").FullName;
        try
        {
            WriteLab(root);
            string socketSeed = Path.Combine(root, "Lab", "SocketSeed.cs");
            File.WriteAllText(socketSeed,
                "namespace Lab { public class OrdinarySocketSeed41 { } }");
            string database = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, database);

            string fifo = Path.Combine(root, "Lab", "Alpha.cs");
            File.Delete(fifo);
            Assert.Equal(0, mkfifo(fifo, Convert.ToUInt32("600", 8)));
            File.Delete(socketSeed);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream,
                ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketSeed));
            string projectFifo = Path.Combine(root, "Lab", "Lab.csproj");
            File.Delete(projectFifo);
            Assert.Equal(0, mkfifo(projectFifo, Convert.ToUInt32("600", 8)));

            using var store = new IndexStore(database, createNew: false);
            Task<RefreshResult> refresh = Task.Run(() =>
                DeltaRefresher.Refresh(store, root, changedRelPaths: null));
            RefreshResult result = await refresh.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(result.DeletedFiles >= 3);

            using var queries = new IndexQueries(database);
            Assert.Empty(queries.SearchSymbols("Alpha41", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("OrdinarySocketSeed41", "exact", null, 2));
            Assert.Null(queries.ProjectByName("Lab"));
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void WindowsStageSidecarJunctionIsRefusedBeforeSnapshot(string suffix)
    {
        if (!OperatingSystem.IsWindows() || !GitInfo.GitAvailable) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-stage-junction").FullName;
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        string external = Directory.CreateTempSubdirectory(
            "codenav-41-stage-junction-external").FullName;
        string? planted = null;
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b stage-junction-{suffix[1..]} \"{wt}\"");
            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            string marker = Path.Combine(external, "do-not-touch.txt");
            File.WriteAllText(marker, "external-marker");
            AnchoredIndexDestination.BeforeStageSidecarReservationForTest = stagePath =>
            {
                planted = stagePath + suffix;
                CreateJunction(planted, external);
            };

            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });

            Assert.Equal("snapshot_failed", result.Action);
            Assert.Equal("external-marker", File.ReadAllText(marker));
            Assert.False(File.Exists(Path.Combine(external, "index.db")));
        }
        finally
        {
            AnchoredIndexDestination.BeforeStageSidecarReservationForTest = null;
            try { if (planted is not null && Directory.Exists(planted)) Directory.Delete(planted); }
            catch { }
            CleanupWorktree(root, wt);
            Cleanup(root);
            Cleanup(external);
        }
    }

    [Fact]
    public void UnixStageSidecarLinkIsRefusedBeforeSnapshotWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsLinux() || !GitInfo.GitAvailable) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-stage-link").FullName;
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        string external = root + "-external-journal";
        string? planted = null;
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b stage-link-review \"{wt}\"");
            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            AnchoredIndexDestination.BeforeStageSidecarReservationForTest = stagePath =>
            {
                planted = stagePath + "-journal";
                File.CreateSymbolicLink(planted, external);
            };

            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });
            Assert.Equal("snapshot_failed", result.Action);
            Assert.False(File.Exists(external));
            // The hostile planted journal link is intentionally retained for exact fixture
            // cleanup; Phoenix must remove every guard it created without deleting that attacker
            // entry or its target.
            Assert.NotNull(planted);
            Assert.NotNull(new FileInfo(planted).LinkTarget);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.Combine(wt, ".codenav")),
                path => Path.GetFileName(path).EndsWith("-wal", StringComparison.Ordinal) ||
                        Path.GetFileName(path).EndsWith("-shm", StringComparison.Ordinal));
        }
        finally
        {
            AnchoredIndexDestination.BeforeStageSidecarReservationForTest = null;
            try { if (planted is not null) File.Delete(planted); } catch { }
            CleanupWorktree(root, wt);
            Cleanup(root);
            try { File.Delete(external); } catch { }
        }
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void FailedPublishCleansEveryPhoenixOwnedStageAndSidecarArtifact(string suffix)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) ||
            !GitInfo.GitAvailable) return;
        string root = Directory.CreateTempSubdirectory("codenav-41-publish-cleanup").FullName;
        string wt = Path.GetFullPath(Path.Combine(root, "..", Path.GetFileName(root) + "-wt"));
        string? plantedSidecar = null;
        try
        {
            WriteRepo(root);
            Git(root, $"worktree add -b publish-cleanup-review \"{wt}\"");
            string mainDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, mainDb);
            plantedSidecar = IndexBuilder.DefaultDbPath(wt) + suffix;
            WorktreeIndexer.BeforeAnchoredInstallForTest = installDb =>
            {
                if (!IsInstallFor(installDb, wt)) return; // foreign parallel-class Ensure
                File.WriteAllText(plantedSidecar, "hostile-entry");
            };

            WorktreeIndexResult result = WorktreeIndexer.Ensure(
                root, mainDb, wt, "create", _ => { });

            Assert.Equal("snapshot_failed", result.Action);
            Assert.Equal("hostile-entry", File.ReadAllText(plantedSidecar));
            // Covers stage/publish database names and every -wal/-shm/-journal derivative.
            AssertNoTemporaryIndexArtifacts(Path.Combine(wt, ".codenav"));
        }
        finally
        {
            WorktreeIndexer.BeforeAnchoredInstallForTest = null;
            try { if (plantedSidecar is not null) File.Delete(plantedSidecar); } catch { }
            CleanupWorktree(root, wt);
            Cleanup(root);
        }
    }

    [Fact]
    public void DeferredDisposeReleasesLeaseAfterBlockedStartupFinishes()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-deferred-dispose").FullName;
        string db = IndexBuilder.DefaultDbPath(root);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var manager = new IndexManager(root, db)
        {
            DisposeWaitTimeoutForTest = TimeSpan.FromMilliseconds(50),
            StartupAfterLeaseAcquiredForTest = () =>
            {
                entered.Set();
                release.Wait();
            },
        };
        try
        {
            File.WriteAllText(Path.Combine(root, "Held.cs"),
                "namespace Lab { public class Held41 { } }");
            manager.Start(forceRebuild: true);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

            manager.Dispose();
            Assert.True(IndexOwnershipLease.IsHeld(root, db),
                "Dispose must not release the lease while startup still uses the destination");

            release.Set();
            Assert.True(WaitUntil(() => !IndexOwnershipLease.IsHeld(root, db), 20_000),
                "the completion continuation must release the lease without another Dispose call");

            using var successor = new IndexManager(root, db);
            successor.Start();
            Assert.True(WaitUntil(() => successor.IsQueryable, 20_000));
        }
        finally
        {
            release.Set();
            manager.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void DisposeCleanupFailureRetainsLeaseUntilSafeRetry()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-dispose-fail-closed").FullName;
        string db = IndexBuilder.DefaultDbPath(root);
        var manager = new IndexManager(root, db);
        try
        {
            File.WriteAllText(Path.Combine(root, "Owned.cs"),
                "namespace Lab { public class Owned41 { } }");
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            manager.CleanupBeforePoolClearForTest = () =>
                throw new IOException("simulated cleanup failure");

            manager.Dispose();
            Assert.True(IndexOwnershipLease.IsHeld(root, db),
                "a failed SQLite teardown must retain cross-process ownership");

            manager.CleanupBeforePoolClearForTest = null;
            manager.Dispose();
            Assert.True(WaitUntil(() => !IndexOwnershipLease.IsHeld(root, db), 10_000));
        }
        finally
        {
            manager.CleanupBeforePoolClearForTest = null;
            manager.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void PostBuildSweepReconcilesProjectEditBeforeWatcherAttachmentWithoutSolutionAuthority()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-post-build-sweep").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        int mutated = 0;
        try
        {
            WriteLab(root);
            string dependencyDir = Path.Combine(root, "Dependency");
            Directory.CreateDirectory(dependencyDir);
            File.WriteAllText(Path.Combine(dependencyDir, "Dependency.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dependencyDir, "Dependency.cs"),
                "namespace Dependency { public class Dependency41 { } }");

            using var manager = new IndexManager(root, database, message =>
            {
                if (!message.StartsWith("Parsing ", StringComparison.Ordinal) ||
                    Interlocked.Exchange(ref mutated, 1) != 0)
                    return;
                File.WriteAllText(Path.Combine(root, "Lab", "Lab.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                      <ItemGroup>
                        <ProjectReference Include="../Dependency/Dependency.csproj" />
                      </ItemGroup>
                    </Project>
                    """);
            });
            manager.Start(forceRebuild: true);

            Assert.True(WaitUntil(() =>
            {
                if (!File.Exists(database)) return false;
                try
                {
                    using var queries = new IndexQueries(database);
                    return queries.ProjectGraph("Lab", 1, "downstream").Any(edge =>
                        edge.ToProject.Equals("Dependency",
                            StringComparison.OrdinalIgnoreCase));
                }
                catch (SqliteException)
                {
                    return false;
                }
            }, 20_000), "the post-build detect-all sweep did not reconcile the pre-watcher edit");
            Assert.Equal(1, Volatile.Read(ref mutated));
            Assert.Empty(Directory.EnumerateFiles(root, "*.sln*",
                SearchOption.AllDirectories));
            using var finalQueries = new IndexQueries(database);
            Assert.Single(finalQueries.SearchSymbols("Dependency41", "exact", null, 2));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DeltaGraphRefreshRollsBackWhenAnyRegularProjectSnapshotIsOversized()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-graph-atomic").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            string libraryDir = Path.Combine(root, "Library");
            string consumerDir = Path.Combine(root, "Consumer");
            Directory.CreateDirectory(libraryDir);
            Directory.CreateDirectory(consumerDir);
            string libraryProject = Path.Combine(libraryDir, "Library.csproj");
            string consumerProject = Path.Combine(consumerDir, "Consumer.csproj");
            File.WriteAllText(libraryProject,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(consumerProject,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(libraryDir, "Library.cs"),
                "namespace Library { public class Library41 { } }");
            File.WriteAllText(Path.Combine(consumerDir, "Consumer.cs"),
                "namespace Consumer { public class Consumer41 { } }");
            IndexBuilder.Build(root, database);

            File.WriteAllText(consumerProject,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            using (var oversized = new FileStream(libraryProject, FileMode.Create,
                       FileAccess.Write, FileShare.ReadWrite))
                oversized.SetLength((long)IndexBuilder.MaxStructuralFileBytes + 1);

            using (var store = new IndexStore(database, createNew: false))
            {
                Assert.Throws<RefreshInputOversizedException>(() =>
                    DeltaRefresher.Refresh(store, root,
                    ["Consumer/Consumer.csproj", "Library/Library.csproj"]));
            }

            using var queries = new IndexQueries(database);
            Assert.Contains(queries.ProjectGraph("Consumer", 1, "downstream"), edge =>
                edge.ToProject.Equals("Library", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("ProjectReference",
                queries.ContentByPath("Consumer/Consumer.csproj") ?? "",
                StringComparison.Ordinal);
            Assert.NotNull(queries.ProjectByName("Library"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ColdBuildRefusesOversizedRegularProjectBeforePublishingDatabase()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-project-limit").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteLab(root);
            string oversizedProject = Path.Combine(root, "Oversized.csproj");
            using (var oversized = new FileStream(oversizedProject, FileMode.Create,
                       FileAccess.Write, FileShare.ReadWrite))
                oversized.SetLength((long)IndexBuilder.MaxStructuralFileBytes + 1);

            Assert.Throws<RefreshInputOversizedException>(() =>
                IndexBuilder.Build(root, database));
            Assert.False(File.Exists(database));
            Assert.False(IndexOwnershipLease.IsHeld(root, database));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ColdBuildIndexesOneThousandProjectsWithoutAggregateProjectCap()
    {
        string root = Directory.CreateTempSubdirectory("codenav-41-project-scale").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const int projectCount = 1_000;
            for (int i = 0; i < projectCount; i++)
            {
                File.WriteAllText(Path.Combine(root, $"Project{i:D4}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                    "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            }

            BuildResult result = IndexBuilder.Build(root, database);
            Assert.Equal(projectCount, result.Projects);
            Assert.Equal(0, result.Solutions);
            using var queries = new IndexQueries(database);
            Assert.NotNull(queries.ProjectByName("Project0000"));
            Assert.NotNull(queries.ProjectByName("Project0999"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.sln*",
                SearchOption.AllDirectories));
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture + helpers

    private static void AssertNoTemporaryIndexArtifacts(string indexDirectory)
    {
        string[] artifacts = Directory.EnumerateFileSystemEntries(indexDirectory)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return name.StartsWith(".phoenix-stage-", StringComparison.Ordinal) ||
                       name.StartsWith(".phoenix-publish-", StringComparison.Ordinal);
            })
            .Select(path => Path.GetFileName(path) ?? path)
            .ToArray();
        Assert.True(artifacts.Length == 0,
            "Phoenix temporary index artifacts remained: " + string.Join(", ", artifacts));
    }

    private static void AssertNoSqliteSidecars(string database)
    {
        Assert.False(File.Exists(database + "-wal"));
        Assert.False(File.Exists(database + "-shm"));
        Assert.False(File.Exists(database + "-journal"));
    }

    private static Process StartPhoenixProcess(string workspaceRoot, string dbPath)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string testsAssembly = typeof(Batch41Tests).Assembly.Location;
        var psi = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--runtimeconfig");
        psi.ArgumentList.Add(Path.ChangeExtension(testsAssembly, ".runtimeconfig.json"));
        psi.ArgumentList.Add("--depsfile");
        psi.ArgumentList.Add(Path.ChangeExtension(testsAssembly, ".deps.json"));
        psi.ArgumentList.Add(typeof(NavigationTools).Assembly.Location);
        psi.ArgumentList.Add("--workspace-root");
        psi.ArgumentList.Add(workspaceRoot);
        psi.ArgumentList.Add("--index-db");
        psi.ArgumentList.Add(dbPath);
        return Process.Start(psi) ?? throw new InvalidOperationException(
            "could not start child Phoenix process");
    }

    private static string CompletedText(Task<string>? text) =>
        text is { IsCompletedSuccessfully: true } ? text.Result : "(no stderr available)";

    private static void CreateJunction(string junction, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(junction);
        psi.ArgumentList.Add(target);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException(
            "could not start junction helper");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"mklink /J failed ({process.ExitCode}): {stdout} {stderr}");
    }

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

    private static void Git(string dir, string args) => TestGit.Run(dir, args); // n7ly: loud + retried

    /// <summary>BeforeAnchoredInstallForTest is process-global and test classes run in
    /// parallel — a closure must act only on ITS OWN worktree's install, or a foreign Ensure
    /// call mutates this test's destination at an unpinned moment (the 684 flake).</summary>
    private static bool IsInstallFor(string installDbPath, string worktreeRoot) =>
        string.Equals(Path.GetFullPath(installDbPath),
            Path.GetFullPath(IndexBuilder.DefaultDbPath(worktreeRoot)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Asserts an index_worktree response carries 'action' — error envelopes do not,
    /// and the raw payload (with its detail) is the forensic that names the actual failure.</summary>
    private static string ActionOf(JsonElement result, string context)
    {
        if (!result.TryGetProperty("action", out JsonElement action))
            Assert.Fail($"{context}: response has no 'action' — raw payload: {result.GetRawText()}");
        return action.GetString()!;
    }

    private static string DirectoryEntryNames(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? string.Join(", ",
                    Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName))
                : "<absent>";
        }
        catch (Exception ex) { return $"<{ex.GetType().Name}>"; }
    }

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

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    private static void CreateHardLinkForTest(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
            Assert.True(CreateHardLinkW(linkPath, existingPath, IntPtr.Zero));
        else
            Assert.Equal(0, link(existingPath, linkPath));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName,
        string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }

    private static void CleanupWorktree(string mainRoot, string wt)
    {
        TestWorkspaceCleanup.ClearIndexPools(wt);
        try { Git(mainRoot, $"worktree remove --force \"{wt}\""); } catch { }
        try { Directory.Delete(wt, recursive: true); } catch { /* already removed / locks */ }
    }
}
