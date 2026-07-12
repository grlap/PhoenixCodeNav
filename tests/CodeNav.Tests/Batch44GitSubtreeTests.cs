using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

[CollectionDefinition("Batch44 SQLite pool isolation", DisableParallelization = true)]
public sealed class Batch44SqlitePoolIsolationCollection { }

/// <summary>Regression coverage for review workspaces rooted below Git's toplevel. Git paths
/// returned to indexing/MCP callers stay workspace-relative, while object lookups translate back
/// to tree-root paths and untracked provenance remains distinct from the all-dirt reconcile union.
/// </summary>
[Collection("Batch44 SQLite pool isolation")]
public sealed class Batch44GitSubtreeTests
{
    [Fact]
    public void ReviewDiffAndDirtyFilesScopeASubtreeAndPreserveTypedUntrackedProvenance()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-subtree-core").FullName;
        string workspace = Path.Combine(root, "Workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(root, "OutsideTracked.cs"), "class OutsideTracked44 { }\n");
            File.WriteAllText(Path.Combine(workspace, "Tracked.cs"), "class Tracked44 { }\n");
            InitRepo(root, gitExe);
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            File.WriteAllText(Path.Combine(root, "OutsideTracked.cs"), "class OutsideEdited44 { }\n");
            File.WriteAllText(Path.Combine(root, "OutsideFresh.cs"), "class OutsideFresh44 { }\n");
            File.WriteAllText(Path.Combine(workspace, "Tracked.cs"), "class TrackedEdited44 { }\n");
            File.WriteAllText(Path.Combine(workspace, "Fresh.cs"), "class Fresh44 { }\n");

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(workspace, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            Assert.Equal(new[] { "Tracked.cs" },
                Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files).Select(file => file.Path));
            Assert.Equal(new[] { "Fresh.cs", "Tracked.cs" },
                Assert.IsType<List<string>>(review.Dirty));
            Assert.Equal(new[] { "Fresh.cs" },
                Assert.IsType<List<string>>(review.UntrackedFiles));
            Assert.DoesNotContain(review.Dirty!, path => path.Contains("Outside", StringComparison.Ordinal));

            Assert.Equal(new[] { "Fresh.cs", "Tracked.cs" },
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(workspace, gitExe)));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ShowFileFromSubtreeReadsTheSubtreeBlobWhenRootHasTheSameName()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-subtree-blob").FullName;
        string workspace = Path.Combine(root, "Workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            const string rootContent = "class RootCollision44 { }\n";
            const string workspaceContent = "class WorkspaceCollision44 { }\n";
            File.WriteAllText(Path.Combine(root, "Same.cs"), rootContent);
            File.WriteAllText(Path.Combine(workspace, "Same.cs"), workspaceContent);
            InitRepo(root, gitExe);
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            Assert.Equal(workspaceContent,
                GitInfo.ShowFile(workspace, head, "Same.cs", gitExe));
            Assert.Equal(rootContent,
                GitInfo.ShowFile(root, head, "Same.cs", gitExe));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ShowFileHonorsSmallBlobByteLimitWithoutReturningAPartialDecode()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-show-limit").FullName;
        try
        {
            const int nearLimitBytes = (512 * 1024) - 1;
            const string prefix = "public class ByteBound44 { }\n// ";
            string content = prefix +
                             new string('x', nearLimitBytes -
                                 System.Text.Encoding.UTF8.GetByteCount(prefix) - 3) +
                             "\u20ac";
            Assert.Equal(nearLimitBytes,
                System.Text.Encoding.UTF8.GetByteCount(content));
            File.WriteAllText(Path.Combine(root, "Large.cs"), content);
            InitRepo(root, gitExe);
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            string? bounded = GitInfo.ShowFile(root, head, "Large.cs", gitExe,
                maxBlobBytes: 64);
            stopwatch.Stop();
            Assert.Null(bounded);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"bounded blob read took {stopwatch.Elapsed}");

            int exactBytes = System.Text.Encoding.UTF8.GetByteCount(content);
            Assert.Equal(content,
                GitInfo.ShowFile(root, head, "Large.cs", gitExe, exactBytes));
            Assert.Equal(content,
                GitInfo.ShowFile(root, head, "Large.cs", gitExe, exactBytes + 1));
            Assert.Null(GitInfo.ShowFile(root, head, "Large.cs", gitExe, exactBytes - 1));
            Assert.Null(GitInfo.ShowFile(root, head, "Large.cs", gitExe, 0));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewPackFromSubtreeCountsOnlyTrueUntrackedAndReadsDeletedFormerType()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-subtree-pack").FullName;
        string workspace = Path.Combine(root, "Workspace");
        try
        {
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "Deleted.cs"),
                "public class RootDeletedCollision44 { }\n");
            File.WriteAllText(Path.Combine(root, "Outside.cs"),
                "public class Outside44 { }\n");
            File.WriteAllText(Path.Combine(workspace, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(workspace, "Tracked.cs"),
                "namespace App44; public class Tracked44 { public int Value => 1; }\n");
            File.WriteAllText(Path.Combine(workspace, "Deleted.cs"),
                "namespace App44; public class Deleted44 { public void Gone() { } }\n");
            File.WriteAllText(Path.Combine(workspace, "Consumer.cs"),
                "namespace App44; public class Consumer44 { public Deleted44? Value; }\n");
            InitRepo(root, gitExe);

            string dbPath = IndexBuilder.DefaultDbPath(workspace);
            IndexBuilder.Build(workspace, dbPath);
            using var manager = new IndexManager(workspace, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(
                    () => manager.IsQueryable && manager.Health().IndexedCommit is not null, 30_000),
                "subtree manager did not become queryable with a Git baseline");
            var tools = new NavigationTools(manager, semantic);

            File.WriteAllText(Path.Combine(root, "Outside.cs"),
                "public class OutsideEdited44 { }\n");
            File.WriteAllText(Path.Combine(root, "OutsideFresh.cs"),
                "public class OutsideFresh44 { }\n");
            File.WriteAllText(Path.Combine(workspace, "Tracked.cs"),
                "namespace App44; public class Tracked44 { public int Value => 2; }\n");
            File.Delete(Path.Combine(workspace, "Deleted.cs"));
            File.WriteAllText(Path.Combine(workspace, "Fresh.cs"),
                "namespace App44; public class Fresh44 { }\n");
            manager.RequestRefresh(new[] { "Tracked.cs", "Deleted.cs", "Fresh.cs" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.SearchSymbols("Fresh44", "exact", null, 2).Count == 1 &&
                       queries.SearchSymbols("Deleted44", "exact", null, 2).Count == 0;
            }, 20_000), "subtree index did not reflect the deletion and untracked addition");

            JsonElement pack = Parse(tools.ReviewPack());

            Assert.False(pack.TryGetProperty("error", out JsonElement error),
                error.ValueKind == JsonValueKind.Undefined ? "review_pack failed" : error.ToString());
            JsonElement changed = pack.GetProperty("changedFiles");
            Assert.Equal(3, changed.GetProperty("total").GetInt32());
            Assert.Equal(1, changed.GetProperty("untracked").GetInt32());
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), symbol =>
                symbol.GetProperty("symbol").GetProperty("name").GetString() == "Fresh44");
            Assert.DoesNotContain(pack.GetProperty("symbols").EnumerateArray(), symbol =>
                symbol.GetProperty("symbol").GetProperty("name").GetString() == "OutsideEdited44");

            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                item => item.GetProperty("path").GetString() == "Deleted.cs");
            List<string?> formerNames = deleted.GetProperty("formerTypes").EnumerateArray()
                .Select(type => type.GetProperty("name").GetString()).ToList();
            Assert.Contains("Deleted44", formerNames);
            Assert.DoesNotContain("RootDeletedCollision44", formerNames);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DiffParserRetainsBothHunkSidesAndCorrelatesExactBlobMoves()
    {
        const string oldOid = "1111111111111111111111111111111111111111";
        const string newOid = "2222222222222222222222222222222222222222";
        byte[] modified = RawPatch(
            new[] { ($":100644 100644 {oldOid} {newOid} M", "Source.cs") },
            $$"""
            diff --git a/Source.cs b/Source.cs
            index {{oldOid}}..{{newOid}} 100644
            --- a/Source.cs
            +++ b/Source.cs
            @@ -10,2 +20,3 @@
            -old one
            -old two
            +new one
            +new two
            +new three
            """);

        GitInfo.DiffFile file = Assert.Single(
            Assert.IsType<List<GitInfo.DiffFile>>(GitInfo.ParseDiffOutput(modified).Files));
        Assert.Equal(new GitInfo.DiffHunk(10, 2, 20, 3), Assert.Single(file.Hunks));
        Assert.Equal((20, 22), Assert.Single(file.Ranges));
        Assert.Equal(oldOid, file.OldObjectId);
        Assert.Equal(newOid, file.NewObjectId);

        byte[] move = RawPatch(
            new[]
            {
                ($":100644 000000 {oldOid} 0000000000000000000000000000000000000000 D", "Old.cs"),
                ($":000000 100644 0000000000000000000000000000000000000000 {oldOid} A", "New.cs"),
            },
            $$"""
            diff --git a/Old.cs b/Old.cs
            deleted file mode 100644
            index {{oldOid}}..0000000
            --- a/Old.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -class Moved44 { }
            diff --git a/New.cs b/New.cs
            new file mode 100644
            index 0000000..{{oldOid}}
            --- /dev/null
            +++ b/New.cs
            @@ -0,0 +1 @@
            +class Moved44 { }
            """);
        List<GitInfo.DiffFile> moved = Assert.IsType<List<GitInfo.DiffFile>>(
            GitInfo.ParseDiffOutput(move).Files);
        Assert.Equal("New.cs", moved.Single(item => item.Path == "Old.cs").MovedToPath);
        Assert.Equal("Old.cs", moved.Single(item => item.Path == "New.cs").MovedFromPath);
    }

    [Fact]
    public void ReviewPackReportsDeletedAndRenamedMembersInSurvivingFiles()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-former-members").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            string source = Path.Combine(root, "Source.cs");
            File.WriteAllText(source,
                """
                public class Subject44
                {
                    public void A() { }
                    public void B() { }
                    public void OldName() { }
                }
                public class Consumer44
                {
                    public void Use(Subject44 value) { value.B(); value.OldName(); }
                }
                """);
            InitRepo(root, gitExe);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);

            File.WriteAllText(source,
                """
                public class Subject44
                {
                    public void A() { }
                    public void NewName() { }
                }
                public class Consumer44
                {
                    public void Use(Subject44 value) { value.B(); value.OldName(); }
                }
                """);
            manager.RequestRefresh(new[] { "Source.cs" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.SearchSymbols("NewName", "exact", null, 2).Count == 1 &&
                       queries.SearchSymbols("B", "exact", null, 2).Count == 0;
            }, 20_000));

            JsonElement pack = Parse(tools.ReviewPack());
            List<string?> current = pack.GetProperty("symbols").EnumerateArray()
                .Select(item => item.GetProperty("symbol").GetProperty("name").GetString())
                .ToList();
            Assert.Contains("NewName", current);
            Assert.DoesNotContain("A", current);
            List<string?> former = pack.GetProperty("formerSymbols").EnumerateArray()
                .SelectMany(file => file.GetProperty("formerSymbols").EnumerateArray())
                .Select(symbol => symbol.GetProperty("name").GetString())
                .ToList();
            Assert.Contains("B", former);
            Assert.Contains("OldName", former);
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.former_symbol_dangling");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewPackTreatsAnExactStagedFileMoveAsAMoveNotADeletion()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-exact-move").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Old.cs"),
                "public class Moved44 { public void Stay() { } }\n");
            File.WriteAllText(Path.Combine(root, "Consumer.cs"),
                "public class Consumer44 { public Moved44? Value; }\n");
            InitRepo(root, gitExe);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);

            File.Move(Path.Combine(root, "Old.cs"), Path.Combine(root, "New.cs"));
            Git(root, gitExe, "add -A");
            manager.RequestRefresh(new[] { "Old.cs", "New.cs" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.Outline("Old.cs").Count == 0 &&
                       queries.Outline("New.cs").Any(symbol => symbol.Name == "Moved44");
            }, 20_000));

            JsonElement pack = Parse(tools.ReviewPack());
            JsonElement moves = pack.GetProperty("movedFiles");
            Assert.Equal(1, moves.GetProperty("total").GetInt32());
            JsonElement move = Assert.Single(moves.GetProperty("items").EnumerateArray());
            Assert.Equal("Old.cs", move.GetProperty("from").GetString());
            Assert.Equal("New.cs", move.GetProperty("to").GetString());
            Assert.False(pack.TryGetProperty("deletedFiles", out _));
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewPackTreatsAnExactUnstagedCSharpMoveAsAMoveNotADeletion()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-unstaged-move").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Old.cs"),
                "public class UnstagedMoved44 { public void Stay() { } }\n");
            File.WriteAllText(Path.Combine(root, "Consumer.cs"),
                "public class Consumer44 { public UnstagedMoved44? Value; }\n");
            InitRepo(root, gitExe);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);

            File.Move(Path.Combine(root, "Old.cs"), Path.Combine(root, "New.cs"));
            manager.RequestRefresh(new[] { "Old.cs", "New.cs" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.Outline("Old.cs").Count == 0 &&
                       queries.Outline("New.cs").Any(symbol =>
                           symbol.Name == "UnstagedMoved44");
            }, 20_000));

            JsonElement pack = Parse(tools.ReviewPack());
            JsonElement moves = pack.GetProperty("movedFiles");
            Assert.Equal(1, moves.GetProperty("total").GetInt32());
            JsonElement move = Assert.Single(moves.GetProperty("items").EnumerateArray());
            Assert.Equal("Old.cs", move.GetProperty("from").GetString());
            Assert.Equal("New.cs", move.GetProperty("to").GetString());
            Assert.False(pack.TryGetProperty("deletedFiles", out _));
            Assert.False(pack.TryGetProperty("formerSymbols", out _));
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() is "review.deleted_dangling" or
                    "review.former_symbol_dangling");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewPackTreatsAnExactSymbolLessCSharpMoveAsAMoveNotADeletion()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-symbol-less-move").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Old.cs"), "global using System;\n");
            InitRepo(root, gitExe);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);

            File.Move(Path.Combine(root, "Old.cs"), Path.Combine(root, "New.cs"));
            manager.RequestRefresh(new[] { "Old.cs", "New.cs" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.ContentByPath("Old.cs") is null &&
                       queries.ContentByPath("New.cs") == "global using System;\n";
            }, 20_000));

            JsonElement pack = Parse(tools.ReviewPack());
            JsonElement moves = pack.GetProperty("movedFiles");
            Assert.Equal(1, moves.GetProperty("total").GetInt32());
            JsonElement move = Assert.Single(moves.GetProperty("items").EnumerateArray());
            Assert.Equal("Old.cs", move.GetProperty("from").GetString());
            Assert.Equal("New.cs", move.GetProperty("to").GetString());
            Assert.False(pack.TryGetProperty("deletedFiles", out _));
            Assert.False(pack.TryGetProperty("formerSymbols", out _));
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() is "review.deleted_dangling" or
                    "review.former_symbol_dangling");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewPackConservativelyLeavesAnEolOnlyUnstagedMoveUncorrelated()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-eol-move").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, ".gitattributes"), "*.cs text eol=lf\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            string oldPath = Path.Combine(root, "Old.cs");
            string newPath = Path.Combine(root, "New.cs");
            File.WriteAllText(oldPath,
                "public class NormalizedMoved44\n{\n    public void Stay() { }\n}\n");
            File.WriteAllText(Path.Combine(root, "Consumer.cs"),
                "public class Consumer44 { public NormalizedMoved44? Value; }\n");
            InitRepo(root, gitExe);
            string committedOid = GitOutput(root, gitExe, "rev-parse HEAD:Old.cs").Trim();
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);

            File.WriteAllText(oldPath,
                "public class NormalizedMoved44\r\n{\r\n    public void Stay() { }\r\n}\r\n");
            File.Move(oldPath, newPath);
            string rawOid = GitOutput(root, gitExe,
                "hash-object --no-filters -- New.cs").Trim();
            string normalizedOid = GitOutput(root, gitExe,
                "hash-object --path=New.cs -- New.cs").Trim();
            Assert.NotEqual(committedOid, rawOid);
            Assert.Equal(committedOid, normalizedOid);

            manager.RequestRefresh(new[] { "Old.cs", "New.cs" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.Outline("Old.cs").Count == 0 &&
                       queries.Outline("New.cs").Any(symbol =>
                           symbol.Name == "NormalizedMoved44");
            }, 20_000));

            JsonElement pack = Parse(tools.ReviewPack());
            Assert.False(pack.TryGetProperty("movedFiles", out _));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), deleted =>
                deleted.GetProperty("path").GetString() == "Old.cs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewPackDoesNotTreatAnExactCSharpToTextMoveAsCSharpPreservation()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-cs-to-text").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Old.cs"),
                "public class Retyped44 { public void GoneFromCompilation() { } }\n");
            File.WriteAllText(Path.Combine(root, "Consumer.cs"),
                "public class Consumer44 { public Retyped44? Value; }\n");
            InitRepo(root, gitExe);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);

            File.Move(Path.Combine(root, "Old.cs"), Path.Combine(root, "Old.txt"));
            Git(root, gitExe, "add -A");
            manager.RequestRefresh(new[] { "Old.cs", "Old.txt" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.Outline("Old.cs").Count == 0 &&
                       queries.SearchSymbols("Retyped44", "exact", null, 2).Count == 0;
            }, 20_000));

            JsonElement pack = Parse(tools.ReviewPack());
            Assert.False(pack.TryGetProperty("movedFiles", out _));
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                item => item.GetProperty("path").GetString() == "Old.cs");
            JsonElement former = Assert.Single(deleted.GetProperty("formerTypes").EnumerateArray(),
                item => item.GetProperty("name").GetString() == "Retyped44");
            Assert.True(former.GetProperty("danglingCandidates").GetInt32() >= 1);
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewRejectsExactMoveCandidateReachedThroughExternalJunction()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-junction-move").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-44-junction-target").FullName;
        string junction = Path.Combine(root, "Linked");
        const string oldContent =
            "public class JunctionMoved44 { public void MustRemainDeleted() { } }\n";
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Old.cs"), oldContent);
            InitRepo(root, gitExe);
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);

            File.Delete(Path.Combine(root, "Old.cs"));
            File.WriteAllText(Path.Combine(outside, "Moved.cs"), oldContent);
            if (!TryCreateJunction(junction, outside, root)) return;
            Assert.True(CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(root,
                Path.Combine(junction, "Moved.cs")));

            manager.RequestRefresh(new[] { "Old.cs" });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.ContentByPath("Old.cs") is null;
            }, 20_000));

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe);
            Assert.Equal("ok", review.Diff.Status);
            GitInfo.DiffFile deletedDiff = Assert.Single(
                Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Old.cs");
            Assert.True(deletedDiff.Deleted);
            Assert.Null(deletedDiff.MovedToPath);
            Assert.DoesNotContain(review.Diff.Files!, file =>
                file.MovedFromPath is not null || file.MovedToPath is not null);
            Assert.DoesNotContain(Assert.IsType<List<string>>(review.UntrackedFiles), path =>
                path == "Linked/Moved.cs");
            GitInfo.UntrackedLinkCoverage excluded =
                Assert.IsType<GitInfo.UntrackedLinkCoverage>(review.ExcludedUntrackedLinks);
            Assert.Equal(1, excluded.Count);
            Assert.Equal(new[] { "Linked/Moved.cs" }, excluded.SamplePaths);
            Assert.False(excluded.SamplesTruncated);

            string json = tools.ReviewPack(maxBytes: 24576);
            JsonElement pack = Parse(json);
            Assert.False(pack.TryGetProperty("movedFiles", out _), json);
            if (pack.TryGetProperty("unmappedChanges", out JsonElement unmapped))
            {
                Assert.DoesNotContain(unmapped.GetProperty("items").EnumerateArray(), item =>
                    item.GetProperty("path").GetString() == "Linked/Moved.cs");
            }
            JsonElement changed = pack.GetProperty("changedFiles");
            Assert.Equal(1, changed.GetProperty("deleted").GetInt32());
            Assert.True(!changed.TryGetProperty("untracked", out JsonElement untracked) ||
                        untracked.GetInt32() == 0, changed.GetRawText());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                item => item.GetProperty("path").GetString() == "Old.cs");
            Assert.Contains(deleted.GetProperty("formerTypes").EnumerateArray(), type =>
                type.GetProperty("name").GetString() == "JunctionMoved44");
            JsonElement linkCoverage = pack.GetProperty("coverage")
                .GetProperty("untrackedLinks");
            Assert.Equal("excluded", linkCoverage.GetProperty("status").GetString());
            Assert.Equal(1, linkCoverage.GetProperty("count").GetInt32());
            Assert.Equal(new[] { "Linked/Moved.cs" }, linkCoverage.GetProperty("samplePaths")
                .EnumerateArray().Select(path => path.GetString()));
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.untracked_links_excluded");

            string boundedJson = tools.ReviewPack(maxBytes: 2048);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(boundedJson) <= 2048,
                boundedJson);
            JsonElement bounded = Parse(boundedJson);
            Assert.False(bounded.TryGetProperty("error", out JsonElement error),
                error.ValueKind == JsonValueKind.Undefined ? boundedJson : error.GetString());
            JsonElement boundedLinks = bounded.GetProperty("coverage")
                .GetProperty("untrackedLinks");
            Assert.Equal("excluded", boundedLinks.GetProperty("status").GetString());
            Assert.Equal(1, boundedLinks.GetProperty("count").GetInt32());
            Assert.False(boundedLinks.TryGetProperty("samplePaths", out _));
            Assert.True(boundedLinks.GetProperty("samplesTruncated").GetBoolean());
        }
        finally
        {
            RemoveJunction(junction);
            Cleanup(root);
            Cleanup(outside);
        }
    }

    [Fact]
    public void ExactMoveCorrelationDoesNotReopenACandidateSwappedToAnExternalJunction()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-move-swap").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-44-move-swap-target").FullName;
        string swapDirectory = Path.Combine(root, "Swap");
        const string oldContent = "public class OriginalMoved44 { }\n";
        try
        {
            File.WriteAllText(Path.Combine(root, "Old.cs"), oldContent);
            InitRepo(root, gitExe);
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            File.Delete(Path.Combine(root, "Old.cs"));
            Directory.CreateDirectory(swapDirectory);
            File.WriteAllText(Path.Combine(swapDirectory, "Moved.cs"),
                "public class DifferentCandidate44 { }\n");
            File.WriteAllText(Path.Combine(outside, "Moved.cs"), oldContent);
            bool swapped = false;

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe,
                afterUntrackedMoveRead: () =>
                {
                    File.Delete(Path.Combine(swapDirectory, "Moved.cs"));
                    Directory.Delete(swapDirectory);
                    swapped = TryCreateJunction(swapDirectory, outside, root);
                });

            Assert.True(swapped);
            Assert.Equal("ok", review.Diff.Status);
            GitInfo.DiffFile deleted = Assert.Single(
                Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Old.cs");
            Assert.True(deleted.Deleted);
            Assert.Null(deleted.MovedToPath);
            Assert.DoesNotContain(review.Diff.Files!, file =>
                file.MovedFromPath is not null || file.MovedToPath is not null);
        }
        finally
        {
            RemoveJunction(swapDirectory);
            Cleanup(root);
            Cleanup(outside);
        }
    }

    [Fact]
    public void ReviewPackRejectsAnUntrackedContentRewriteAfterItsLastAggregationRead()
    {
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-final-review-epoch").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Stable.cs"),
                "public class StableReviewEpoch44 { }\n");
            InitRepo(root, gitExe);
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() =>
            {
                if (!manager.IsQueryable) return false;
                var (currentHead, status) = manager.CurrentHeadCommitEx();
                return status == "ok" && string.Equals(currentHead, head, StringComparison.Ordinal);
            }, 20_000), "manager did not attach the expected Git baseline");

            string untracked = Path.Combine(root, "Late.cs");
            File.WriteAllText(untracked, "public class LateReviewEpoch44 { }\n");
            var tools = new NavigationTools(manager, semantic)
            {
                ReviewBeforeFinalWorkspaceValidationForTest = () => File.WriteAllText(untracked,
                    "public class RewrittenReviewEpoch44 { }\n"),
            };

            string json = tools.ReviewPack(baseRef: head, maxBytes: 24576);
            JsonElement response = Parse(json);
            Assert.True(response.GetProperty("error").GetString() == "git_worktree_changed",
                json);
            Assert.False(response.TryGetProperty("changedFiles", out _), json);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PlatformPathConversionOnlyRewritesTheActualDirectorySeparator()
    {
        const string unixPath = "Folder/Literal\\Name.cs";
        const string windowsPath = "Folder\\Literal\\Name.cs";

        Assert.Equal(unixPath, CodeNav.Core.WorkspacePaths.ToGitPath(unixPath, '/'));
        Assert.Equal("Folder/Literal/Name.cs",
            CodeNav.Core.WorkspacePaths.ToGitPath(windowsPath, '\\'));
    }

    [Fact]
    public void WorktreePorcelainNulParserPreservesExactUnixPathsAndFailsClosed()
    {
        const string oid = "0123456789abcdef0123456789abcdef01234567";
        const string path = "/tmp/Twin\\Review\nLine";
        string payload =
            $"worktree {path}\0HEAD {oid}\0branch refs/heads/review\0locked reason\0\0" +
            "worktree /tmp/bare\0bare\0\0";

        List<GitInfo.Worktree> worktrees = Assert.IsType<List<GitInfo.Worktree>>(
            GitInfo.ParseWorktreePorcelainZ(payload, '/'));
        Assert.Equal(2, worktrees.Count);
        Assert.Equal(path, worktrees[0].Path);
        Assert.Equal(oid, worktrees[0].Head);
        Assert.Equal("review", worktrees[0].Branch);
        Assert.Equal("/tmp/bare", worktrees[1].Path);
        Assert.Null(worktrees[1].Head);
        Assert.Null(worktrees[1].Branch);

        Assert.Null(GitInfo.ParseWorktreePorcelainZ(
            $"worktree {path}\0HEAD {oid}\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ($"worktree {path}", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ("worktree \0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ("worktree relative/path\0bare\0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ($"worktree {path}\0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ(
            $"worktree {path}\0HEAD {oid}\0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ(
            $"worktree {path}\0HEAD {oid}\0branch refs/heads/review\0detached\0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ(
            $"worktree {path}\0HEAD {oid}\0bare\0branch refs/heads/review\0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ(
            $"worktree {path}\0HEAD {oid}\0branch review\0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ(
            $"worktree {path}\0HEAD not-an-oid\0\0", '/'));
        Assert.Null(GitInfo.ParseWorktreePorcelainZ(payload + "\0", '/'));
    }

    [Fact]
    public void ReviewPackPreservesUnixLiteralBackslashSurvivorPathEndToEnd()
    {
        if (OperatingSystem.IsWindows()) return;
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-backslash-survivor").FullName;
        const string deletedPath = "App/Deleted.cs";
        const string survivorPath = "App/Literal\\Twin.cs";
        const string typeName = "LiteralBackslashSurvivor44";
        const string content = "namespace App; public class LiteralBackslashSurvivor44 { }\n";
        try
        {
            string app = Path.Combine(root, "App");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(app, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(app, "Deleted.cs"), content);
            File.WriteAllText(Path.Combine(app, "Literal\\Twin.cs"), content);
            InitRepo(root, gitExe);
            File.Delete(Path.Combine(app, "Deleted.cs"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));

            using (IndexQueries queries = manager.OpenQueries())
            {
                SymbolHit survivor = Assert.Single(queries.SearchSymbols(typeName, "exact",
                    null, 4));
                Assert.Equal(survivorPath, survivor.FilePath);
            }

            var tools = new NavigationTools(manager, semantic);
            string json = tools.ReviewPack(maxBytes: 24576);
            JsonElement pack = Parse(json);
            Assert.False(pack.TryGetProperty("error", out JsonElement error),
                error.ValueKind == JsonValueKind.Undefined ? json : error.GetString());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                item => item.GetProperty("path").GetString() == deletedPath);
            JsonElement former = Assert.Single(deleted.GetProperty("formerTypes").EnumerateArray(),
                item => item.GetProperty("name").GetString() == typeName);
            Assert.Equal("project_candidate_survivor",
                former.GetProperty("danglingStatus").GetString());
            Assert.Equal(0, former.GetProperty("danglingCandidates").GetInt32());
            Assert.Contains(former.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                path => path.GetString() == survivorPath);
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReviewDiffAndShowFilePreserveUnixLeadingLiteralBackslashGitPath()
    {
        if (OperatingSystem.IsWindows()) return;
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory(
            "codenav-44-leading-backslash").FullName;
        const string gitPath = "\\Leading.cs";
        const string baseline = "public class LeadingBackslash44 { public int Value => 1; }\n";
        const string edited = "public class LeadingBackslash44 { public int Value => 2; }\n";
        try
        {
            string fullPath = Path.Combine(root, gitPath);
            File.WriteAllText(fullPath, baseline);
            InitRepo(root, gitExe);
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            File.WriteAllText(fullPath, edited);

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            GitInfo.DiffFile changed = Assert.Single(
                Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files));
            Assert.Equal(gitPath, changed.Path);
            Assert.Contains(gitPath, Assert.IsType<List<string>>(review.Dirty));
            Assert.Equal(baseline, GitInfo.ShowFile(root, head, gitPath, gitExe));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void UnixLiteralBackslashSurvivesWatcherAndTargetedDeltaRefresh()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-backslash-refresh").FullName;
        const string gitPath = "Folder/Literal\\Refresh.cs";
        try
        {
            string directory = Path.Combine(root, "Folder");
            Directory.CreateDirectory(directory);
            string fullPath = Path.Combine(directory, "Literal\\Refresh.cs");
            File.WriteAllText(fullPath, "public class BeforeBackslashRefresh44 { }\n");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));

            File.WriteAllText(fullPath, "public class WatcherBackslashRefresh44 { }\n");
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.SearchSymbols("WatcherBackslashRefresh44", "exact", null, 2)
                    .Any(hit => hit.FilePath == gitPath);
            }, 20_000), "watcher did not preserve the literal-backslash path");

            File.WriteAllText(fullPath, "public class TargetedBackslashRefresh44 { }\n");
            manager.RequestRefresh(new[] { gitPath });
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                List<SymbolHit> hits = queries.SearchSymbols("TargetedBackslashRefresh44",
                    "exact", null, 2);
                return hits.Count == 1 && hits[0].FilePath == gitPath;
            }, 20_000), "targeted delta did not preserve the literal-backslash path");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ChangedFileNulParserPreservesBackslashesAndNewlinesExactly()
    {
        const string backslash = "Folder/Literal\\Changed.cs";
        const string newline = "Folder/Literal\nChanged.cs";

        Assert.Equal(new[] { backslash, newline },
            GitInfo.ParseNulPathList(backslash + "\0" + newline + "\0"));
        Assert.Empty(Assert.IsType<List<string>>(GitInfo.ParseNulPathList("")));
        Assert.Null(GitInfo.ParseNulPathList(backslash));
        Assert.Null(GitInfo.ParseNulPathList("\0"));
    }

    [Fact]
    public void LeadingLiteralBackslashIsSafeOnlyUnderUnixGitPathSemantics()
    {
        Assert.True(GitInfo.IsSafeRelativeGitPath("\\Leading.cs",
            backslashIsSeparator: false));
        Assert.False(GitInfo.IsSafeRelativeGitPath("\\Leading.cs",
            backslashIsSeparator: true));
    }

    [Fact]
    public void ChangedFilesPreservesUnixLiteralBackslashGitPath()
    {
        if (OperatingSystem.IsWindows()) return;
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-backslash-changed").FullName;
        const string gitPath = "Folder/Literal\\Changed.cs";
        try
        {
            File.WriteAllText(Path.Combine(root, "Baseline.cs"), "class Baseline44 { }\n");
            InitRepo(root, gitExe);
            string first = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            string directory = Path.Combine(root, "Folder");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Literal\\Changed.cs"),
                "class LiteralBackslashChanged44 { }\n");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m literal-backslash");
            string second = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            Assert.Equal(new[] { gitPath }, GitInfo.ChangedFiles(root, first, second));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void GitPathResolutionOnUnixPreservesLiteralBackslashAndRejectsEscapes()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-git-path").FullName;
        try
        {
            string directory = Path.Combine(root, "Folder");
            Directory.CreateDirectory(directory);
            const string gitPath = "Folder/Literal\\Name.cs";

            Assert.True(CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root, gitPath,
                out string fullPath));
            Assert.Equal(Path.GetFullPath(Path.Combine(directory, "Literal\\Name.cs")), fullPath);
            Assert.Equal("Literal\\Name.cs", Path.GetFileName(fullPath));
            File.WriteAllText(fullPath, "class LiteralBackslash44 { }\n");
            Assert.True(File.Exists(fullPath));
            Assert.False(File.Exists(Path.Combine(directory, "Literal", "Name.cs")));

            Assert.False(CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root,
                "../escape.cs", out _));
            Assert.False(CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root,
                "Folder/../../escape.cs", out _));
            Assert.False(CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root,
                "/rooted.cs", out _));
            Assert.False(CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root, "", out _));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void UnixLiteralBackslashAncestorSurvivesProjectAndSolutionParsing()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-backslash-project").FullName;
        const string directoryPath = "Folder\\Literal";
        const string projectPath = "Folder\\Literal/App.csproj";
        const string childPath = "Folder\\Literal/Child.cs";
        const string solutionPath = "Folder\\Literal/App.sln";
        const string filterPath = "Folder\\Literal/Slice.slnf";
        try
        {
            string directory = Path.Combine(root, directoryPath);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "App.csproj"), """
                <Project ToolsVersion="15.0">
                  <PropertyGroup>
                    <AssemblyName>LiteralBackslashProject44</AssemblyName>
                    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Child.cs" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory, "Child.cs"),
                "namespace BackslashProject44 { public class Child44 { } }\n");
            File.WriteAllText(Path.Combine(directory, "App.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App.csproj", "{11111111-2222-3333-4444-555555555555}"
                EndProject
                Global
                EndGlobal
                """);
            File.WriteAllText(Path.Combine(directory, "Slice.slnf"), """
                {
                  "solution": {
                    "path": "Nested\\All.sln",
                    "projects": [ "App\\App.csproj" ]
                  }
                }
                """);

            var project = CodeNav.Core.Discovery.ProjectFileParser.Parse(root, projectPath);
            Assert.Equal(new[] { childPath }, project.ExplicitCompileItems);
            Assert.Equal(projectPath, project.RelPath);

            var solution = CodeNav.Core.Discovery.SolutionParser.Parse(root, solutionPath);
            Assert.Equal(new[] { projectPath }, solution.ProjectRelPaths);
            var filter = CodeNav.Core.Discovery.SolutionParser.Parse(root, filterPath);
            Assert.Equal(new[] { "Folder\\Literal/Nested/App/App.csproj" },
                filter.ProjectRelPaths);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var queries = new IndexQueries(dbPath);
            Assert.Contains(queries.ProjectsContaining(childPath),
                owner => owner.Name == "LiteralBackslashProject44");
            Assert.NotNull(queries.FileByPath(projectPath));
            Assert.NotNull(queries.FileByPath(solutionPath));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task UnixLiteralBackslashPathResolvesThroughTheSemanticCallerBoundary()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-44-backslash-semantic").FullName;
        const string gitPath = "App/Literal\\Semantic.cs";
        const string typeName = "LiteralBackslashSemantic44";
        try
        {
            string app = Path.Combine(root, "App");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(app, "Literal\\Semantic.cs"), """
                namespace BackslashSemantic44;
                public class LiteralBackslashSemantic44 { }
                public class Consumer44
                {
                    public LiteralBackslashSemantic44 Value { get; } = new();
                }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            Assert.True(semantic.FrameworkRefsAvailable);

            var (definition, definitionReason) = await semantic.DefinitionAsync(
                gitPath, 2, null, typeName, 30_000);
            Assert.True(definition is not null,
                $"semantic definition failed: {definitionReason}");
            Assert.Contains(definition!.Declarations,
                declaration => declaration.Path == gitPath);

            var (references, referencesReason) = await semantic.ReferencesAsync(
                gitPath, 2, null, typeName, maxProjects: 4, samplesPerGroup: 4,
                timeoutMs: 30_000);
            Assert.True(references is not null,
                $"semantic references failed: {referencesReason}");
            Assert.Contains(references!.Symbol.Declarations,
                declaration => declaration.Path == gitPath);
            Assert.Contains(references.Groups.SelectMany(group => group.Samples),
                sample => sample.Path == gitPath);

            var tools = new NavigationTools(manager, semantic);
            JsonElement response = Parse(tools.Definition(path: gitPath, line: 2,
                mode: "semantic", timeoutMs: 30_000, includeBody: true));
            Assert.False(response.TryGetProperty("error", out _));
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal(gitPath,
                response.GetProperty("body").GetProperty("path").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void UnixLiteralBackslashWorktreeRootDoesNotAliasItsSlashTwin()
    {
        if (OperatingSystem.IsWindows()) return;
        string? gitExe = FindGit();
        if (gitExe is null) return;
        string container = Directory.CreateTempSubdirectory(
            "codenav-44-backslash-worktree").FullName;
        string main = Path.Combine(container, "Twin", "Review");
        string sibling = Path.Combine(container, "Twin\\Review");
        try
        {
            Directory.CreateDirectory(main);
            File.WriteAllText(Path.Combine(main, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(main, "Main.cs"), "class MainWorktree44 { }\n");
            InitRepo(main, gitExe);
            Git(main, gitExe,
                $"worktree add -q -b literal-backslash-root-44 \"{sibling}\"");

            string expectedMain = Path.GetFullPath(main);
            string expectedSibling = Path.GetFullPath(sibling);
            List<GitInfo.Worktree> gitWorktrees = Assert.IsType<List<GitInfo.Worktree>>(
                GitInfo.Worktrees(main));
            Assert.Contains(gitWorktrees, worktree => worktree.Path == expectedMain);
            Assert.Contains(gitWorktrees, worktree => worktree.Path == expectedSibling);

            string dbPath = IndexBuilder.DefaultDbPath(main);
            IndexBuilder.Build(main, dbPath);
            List<WorktreeIndexStatus> statuses =
                Assert.IsType<List<WorktreeIndexStatus>>(WorktreeIndexer.Status(main));
            Assert.Single(statuses, status => status.IsThisWorkspace);
            Assert.True(statuses.Single(status => status.IsThisWorkspace).Path == expectedMain);

            using var manager = new IndexManager(main, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);
            JsonElement result = Parse(tools.IndexWorktree(expectedSibling, "refresh"));
            Assert.Equal("worktree_index_missing",
                result.GetProperty("error").GetString());
        }
        finally
        {
            try
            {
                if (Directory.Exists(main))
                    Git(main, gitExe, $"worktree remove --force \"{sibling}\"");
            }
            catch { /* best effort */ }
            Cleanup(container);
        }
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static byte[] RawPatch(IEnumerable<(string Header, string Path)> entries,
        string patch)
    {
        using var output = new MemoryStream();
        foreach ((string header, string path) in entries)
        {
            output.Write(System.Text.Encoding.ASCII.GetBytes(header));
            output.WriteByte(0);
            output.Write(System.Text.Encoding.UTF8.GetBytes(path));
            output.WriteByte(0);
        }
        output.WriteByte(0);
        string normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimStart('\n');
        if (!normalized.EndsWith('\n')) normalized += "\n";
        output.Write(System.Text.Encoding.UTF8.GetBytes(normalized));
        return output.ToArray();
    }

    private static string? FindGit() => GitInfo.ResolveGitExeFrom(
        Environment.GetEnvironmentVariable("PHOENIX_GIT"),
        Environment.GetEnvironmentVariable("PATH"));

    private static bool TryCreateJunction(string link, string target, string workingDirectory)
    {
        try
        {
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            GitInfo.ProcessResult result = GitInfo.RunProcessEx(cmd, workingDirectory,
                $"/d /c mklink /J \"{link}\" \"{target}\"", waitMs: 5_000);
            return result.Status == "ok" && Directory.Exists(link) &&
                   CodeNav.Core.WorkspacePaths.IsReparsePoint(link);
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveJunction(string path)
    {
        try
        {
            if (Directory.Exists(path) && CodeNav.Core.WorkspacePaths.IsReparsePoint(path))
                Directory.Delete(path);
        }
        catch { /* best effort: remove the link, never recurse into its target */ }
    }

    private static void InitRepo(string root, string gitExe)
    {
        Git(root, gitExe, "init -q -b main");
        Git(root, gitExe, "config user.email test@example.com");
        Git(root, gitExe, "config user.name CodeNavTest");
        Git(root, gitExe, "config commit.gpgsign false");
        Git(root, gitExe, "add -A");
        Git(root, gitExe, "commit -q -m initial");
    }

    private static void Git(string root, string gitExe, string args)
    {
        var (exe, wrapped) = GitInfo.Invocation(gitExe,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args);
        GitInfo.ProcessResult result = GitInfo.RunProcessEx(exe, root, wrapped, waitMs: 20_000);
        Assert.Equal("ok", result.Status);
    }

    private static string GitOutput(string root, string gitExe, string args)
    {
        var (exe, wrapped) = GitInfo.Invocation(gitExe,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args);
        GitInfo.ProcessResult result = GitInfo.RunProcessEx(exe, root, wrapped, waitMs: 20_000);
        Assert.Equal("ok", result.Status);
        return Assert.IsType<string>(result.Output);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { /* Windows pooled handles */ }
    }
}
