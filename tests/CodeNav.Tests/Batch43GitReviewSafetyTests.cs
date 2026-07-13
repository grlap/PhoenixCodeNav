using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using System.Diagnostics;
using System.Text.Json;
using static CodeNav.Tests.Batch43Support;

namespace CodeNav.Tests;

// SPLIT (PhoenixCodeNav-6zdy): slice 1 of 2. Shared helpers moved to Batch43Support.cs;
// sibling slice: Batch43GitReviewSafetyTestsPart2.cs — duration-balanced so xUnit runs two
// classes in parallel instead of one ~48s serial class. Pure move; no test bodies changed.
/// <summary>Batch 43 (v0.11.1): deterministic, helper-free review diffs and wrapper-safe
/// ref/blob reads whose dynamic values travel over stdin instead of cmd.exe arguments.</summary>
public class Batch43GitReviewSafetyTests
{
    [Fact]
    public void StatefulPatchParserKeepsHeaderLookingSourceLinesAndLaterHunks()
    {
        const string patch = """
            diff --git a/Source.cs b/Source.cs
            index 1111111..2222222 100644
            --- a/Source.cs
            +++ b/Source.cs
            @@ -3,2 +3,2 @@
            --- counter;
            -Old();
            +++ counter;
            +New();
            @@ -10 +10 @@
            -Old2();
            +New2();
            """;

        var result = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', "Source.cs"), patch + "\n"));

        Assert.Equal("ok", result.Status);
        var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(result.Files));
        Assert.Equal("Source.cs", file.Path);
        Assert.False(file.Deleted);
        Assert.Equal(new[] { (3, 4), (10, 10) }, file.Ranges);
    }

    [Fact]
    public void PatchParserHandlesAddedDeletedUnicodeSpacePathsAndMultipleFiles()
    {
        string patch = """
            diff --git a/Ünicode Space.cs b/Ünicode Space.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/Ünicode Space.cs{TAB}
            @@ -0,0 +1,2 @@
            +first
            +second
            diff --git a/Old.cs b/Old.cs
            deleted file mode 100644
            index 2222222..0000000
            --- a/Old.cs
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -old one
            -old two
            """.Replace("{TAB}", "\t", StringComparison.Ordinal)
               .Replace("\n", "\r\n", StringComparison.Ordinal);

        var result = GitInfo.ParseDiffOutput(DiffOutput(new[]
        {
            new RawEntry("000000", "100644", 'A', "Ünicode Space.cs"),
            new RawEntry("100644", "000000", 'D', "Old.cs"),
        }, patch + "\n"));
        Assert.Equal("ok", result.Status);
        var files = Assert.IsType<List<GitInfo.DiffFile>>(result.Files);

        Assert.Equal(2, files.Count);
        Assert.Equal(new[] { "Old.cs", "Ünicode Space.cs" }, files.Select(f => f.Path));
        var added = files.Single(f => f.Path == "Ünicode Space.cs");
        Assert.False(added.Deleted);
        Assert.Equal((1, 2), Assert.Single(added.Ranges));
        var deleted = files.Single(f => f.Path == "Old.cs");
        Assert.True(deleted.Deleted);
        Assert.Equal((1, 1), Assert.Single(deleted.Ranges));
    }

    [Fact]
    public void PatchParserTreatsEmptyAsCleanAndMalformedAsUnknown()
    {
        Assert.Empty(Assert.IsType<List<GitInfo.DiffFile>>(
            GitInfo.ParseDiffOutput(Array.Empty<byte>()).Files));

        string[] malformed =
        {
            "not a patch\n",
            "diff --git a/X.cs b/X.cs\nindex 1..2 100644\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n",
            "diff --git a/X.cs b/X.cs\n+++ b/X.cs\n",
            "diff --git a/X.cs b/X.cs\n--- c/X.cs\n+++ b/X.cs\n",
            "diff --git a/X.cs b/Y.cs\n--- a/X.cs\n+++ b/Y.cs\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ bad @@\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@garbage\n-old\n+new\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1,2 +1 @@\n-old\n+new\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\nsource outside a hunk\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -999999999999 +1 @@\n-old\n+new\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n same\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -0,0 +0,0 @@\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n\\ No newline at end of file\n-old\n+new\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n\\ No newline at end of file\n+new\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n-old\n+new\n" +
                "diff --git a/Y.cs b/Y.cs\n--- a/Y.cs\n",
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n-old\n+new\n" +
                "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -2 +2 @@\n-old2\n+new2\n",
        };

        foreach (string patch in malformed)
        {
            var result = GitInfo.ParseDiffOutput(DiffOutput(
                new RawEntry("100644", "100644", 'M', "X.cs"), patch));
            Assert.True(result.Status == "malformed",
                $"Expected malformed patch, got '{result.Status}': {patch.Replace("\n", "\\n")}");
            Assert.Null(result.Files);
        }
    }

    [Fact]
    public void PatchParserRejectsOverflowingOldSideHunkRange()
    {
        const string patch =
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n" +
            "@@ -2147483647,2 +1 @@\n-old one\n-old two\n+new\n";

        var result = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', "X.cs"), patch));

        Assert.Equal("malformed", result.Status);
        Assert.Null(result.Files);
    }

    [Fact]
    public void PatchParserAcceptsWellPlacedNoNewlineMarkers()
    {
        const string patch =
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n" +
            "-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n";

        var result = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', "X.cs"), patch));
        Assert.Equal("ok", result.Status);
        var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(result.Files));
        Assert.Equal((1, 1), Assert.Single(file.Ranges));
    }

    [Fact]
    public void BatchBlobParserRejectsMalformedFramingAndPreservesExactContent()
    {
        const string oid = "0123456789abcdef0123456789abcdef01234567";
        Assert.Null(GitInfo.ParseBatchBlob($"{oid} blob 0\n"));
        Assert.Null(GitInfo.ParseBatchBlob($"{oid} blob 1\nx"));
        Assert.Null(GitInfo.ParseBatchBlob($"{oid} blob nope\nx\n"));
        Assert.Null(GitInfo.ParseBatchBlob($"{oid} blob 2\nx\n"));
        Assert.Null(GitInfo.ParseBatchBlob("noise\n"));
        Assert.Equal("", GitInfo.ParseBatchBlob($"{oid} blob 0\n\n"));
        Assert.Equal("x\n\n", GitInfo.ParseBatchBlob($"{oid} blob 3\nx\n\n\n"));
        Assert.Equal("Zażółć", GitInfo.ParseBatchBlob(
            $"{oid} blob {System.Text.Encoding.UTF8.GetByteCount("Zażółć")}\nZażółć\n"));
    }

    [Fact]
    public void RawManifestKeepsHunklessBinaryModeEmptyAndTypeChangesVisible()
    {
        const string patch = """
            diff --git a/Text.cs b/Text.cs
            index 1111111..2222222 100644
            --- a/Text.cs
            +++ b/Text.cs
            @@ -4 +4 @@
            -old
            +new
            diff --git a/Binary.bin b/Binary.bin
            index 1111111..2222222 100644
            Binary files a/Binary.bin and b/Binary.bin differ
            diff --git a/Mode.cs b/Mode.cs
            old mode 100644
            new mode 100755
            diff --git a/EmptyAdded.cs b/EmptyAdded.cs
            new file mode 100644
            index 0000000..e69de29
            diff --git a/EmptyDeleted.cs b/EmptyDeleted.cs
            deleted file mode 100644
            index e69de29..0000000
            diff --git a/Type.cs b/Type.cs
            deleted file mode 100644
            index 1111111..0000000
            --- a/Type.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -old target
            diff --git a/Type.cs b/Type.cs
            new file mode 120000
            index 0000000..2222222
            --- /dev/null
            +++ b/Type.cs
            @@ -0,0 +1 @@
            +new target
            """ + "\n";
        var entries = new[]
        {
            new RawEntry("100644", "100644", 'M', "Text.cs"),
            new RawEntry("100644", "100644", 'M', "Binary.bin"),
            new RawEntry("100644", "100755", 'M', "Mode.cs"),
            new RawEntry("000000", "100644", 'A', "EmptyAdded.cs"),
            new RawEntry("100644", "000000", 'D', "EmptyDeleted.cs"),
            new RawEntry("100644", "120000", 'T', "Type.cs"),
        };

        var result = GitInfo.ParseDiffOutput(DiffOutput(entries, patch));

        Assert.Equal("ok", result.Status);
        var files = Assert.IsType<List<GitInfo.DiffFile>>(result.Files);
        Assert.Equal(entries.Select(entry => entry.Path).OrderBy(path => path, StringComparer.Ordinal),
            files.Select(file => file.Path));
        Assert.Equal((4, 4), Assert.Single(files.Single(file => file.Path == "Text.cs").Ranges));
        Assert.All(files.Where(file => file.Path != "Text.cs"), file => Assert.Empty(file.Ranges));
        Assert.True(files.Single(file => file.Path == "EmptyDeleted.cs").Deleted);
        Assert.All(files.Where(file => file.Path != "EmptyDeleted.cs"), file => Assert.False(file.Deleted));
    }

    [Fact]
    public void RealGitHunklessBinaryModeEmptyAndTypeChangesRemainReviewable()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-real-hunkless").FullName;
        try
        {
            InitRepo(root, gitExe);
            Git(root, gitExe, "config core.symlinks false");
            File.WriteAllBytes(Path.Combine(root, "Binary.bin"), [0, 1, 2, 3]);
            File.WriteAllText(Path.Combine(root, "Mode.cs"), "class Mode { }\n");
            File.WriteAllText(Path.Combine(root, "EmptyDeleted.cs"), "");
            File.WriteAllText(Path.Combine(root, "Type.cs"), "old target");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            File.WriteAllBytes(Path.Combine(root, "Binary.bin"), [0, 1, 2, 4]);
            File.WriteAllText(Path.Combine(root, "EmptyAdded.cs"), "");
            File.Delete(Path.Combine(root, "EmptyDeleted.cs"));
            Git(root, gitExe, "add Binary.bin EmptyAdded.cs EmptyDeleted.cs");
            Git(root, gitExe, "update-index --chmod=+x Mode.cs");
            File.WriteAllText(Path.Combine(root, "LinkTarget.txt"), "new target");
            string linkBlob = GitOutput(root, gitExe, "hash-object -w LinkTarget.txt").Trim();
            File.Delete(Path.Combine(root, "LinkTarget.txt"));
            Git(root, gitExe, $"update-index --add --cacheinfo 120000,{linkBlob},Type.cs");
            Git(root, gitExe, "checkout-index -f -- Type.cs");
            var result = SemanticRetry.Until( // n7ly sweep
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok").Diff;

            Assert.Equal("ok", result.Status);
            var files = Assert.IsType<List<GitInfo.DiffFile>>(result.Files);
            string[] expected =
            [
                "Binary.bin", "EmptyAdded.cs", "EmptyDeleted.cs", "Mode.cs", "Type.cs",
            ];
            Assert.Equal(expected, files.Select(file => file.Path));
            Assert.All(files, file => Assert.Empty(file.Ranges));
            Assert.True(files.Single(file => file.Path == "EmptyDeleted.cs").Deleted);
            Assert.False(files.Single(file => file.Path == "Type.cs").Deleted);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RawManifestRejectsMalformedFramingAndReportsUnmergedState()
    {
        var statOnly = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', "StatOnly.cs"), ""));
        Assert.Equal("ok", statOnly.Status);
        Assert.Empty(Assert.IsType<List<GitInfo.DiffFile>>(statOnly.Files));

        var rawOnlyAdd = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("000000", "100644", 'A', "MissingPatch.cs"), ""));
        Assert.Equal("malformed", rawOnlyAdd.Status);
        Assert.Null(rawOnlyAdd.Files);

        byte[] truncated = Ascii(
            $":100644 100644 {OldOid} {NewOid} M\0X.cs");
        var malformed = GitInfo.ParseDiffOutput(truncated);
        Assert.Equal("malformed", malformed.Status);
        Assert.Null(malformed.Files);

        byte[] missingSeparator =
        [.. Ascii($":100644 100644 {OldOid} {NewOid} M\0X.cs"), 0];
        var separatorResult = GitInfo.ParseDiffOutput(missingSeparator);
        Assert.Equal("malformed", separatorResult.Status);
        Assert.Null(separatorResult.Files);

        const string unknownPatch =
            "diff --git a/Y.cs b/Y.cs\n--- a/Y.cs\n+++ b/Y.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var unknownPath = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', "X.cs"), unknownPatch));
        Assert.Equal("malformed", unknownPath.Status);
        Assert.Null(unknownPath.Files);

        const string unknownHunklessPatch =
            "diff --git a/Y.bin b/Y.bin\nindex 1111111..2222222 100644\n" +
            "Binary files a/Y.bin and b/Y.bin differ\n";
        var unknownHunklessPath = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', "X.bin"), unknownHunklessPatch));
        Assert.Equal("malformed", unknownHunklessPath.Status);
        Assert.Null(unknownHunklessPath.Files);

        var unmerged = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("000000", "000000", 'U', "Conflict.cs"), patch: ""));
        Assert.Equal("unmerged", unmerged.Status);
        Assert.Null(unmerged.Files);
    }

    [Fact]
    public void RealMergeConflictReturnsUnmergedInsteadOfAnOrdinaryReview()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-unmerged").FullName;
        try
        {
            InitRepo(root, gitExe);
            string path = Path.Combine(root, "Conflict.cs");
            File.WriteAllText(path, "class Conflict { int Value => 0; }\n");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            Git(root, gitExe, "checkout -q -b other");
            File.WriteAllText(path, "class Conflict { int Value => 1; }\n");
            Git(root, gitExe, "commit -qam other");
            Git(root, gitExe, "checkout -q main");
            File.WriteAllText(path, "class Conflict { int Value => 2; }\n");
            Git(root, gitExe, "commit -qam main");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            var merge = GitInfo.RunProcessEx(gitExe, root,
                "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " +
                "merge --no-edit other", waitMs: 20_000);
            Assert.Equal("exit_nonzero", merge.Status);

            var indexingDirty = GitInfo.DirtyFiles(root, gitExe);
            Assert.Contains("Conflict.cs", Assert.IsType<List<string>>(indexingDirty));

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "unmerged", r => r.Diff.Status ?? "<null>", "ReviewDiff status unmerged");

            Assert.Equal("unmerged", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void StageOnlyUnmergedGitlinkPathIsDecodedAndReportedAsUnmerged()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-unmerged").FullName;
        try
        {
            string baseLink = CreateRepo(root, gitExe);
            Git(root, gitExe, "commit --allow-empty -q -m link-ours");
            string oursLink = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            Git(root, gitExe, "commit --allow-empty -q -m link-theirs");
            string theirsLink = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            Git(root, gitExe,
                $"update-index --add --cacheinfo 160000,{baseLink},Modules/Child");
            string baseTree = GitOutput(root, gitExe, "write-tree").Trim();
            Git(root, gitExe,
                $"update-index --add --cacheinfo 160000,{oursLink},Modules/Child");
            string oursTree = GitOutput(root, gitExe, "write-tree").Trim();
            Git(root, gitExe,
                $"update-index --add --cacheinfo 160000,{theirsLink},Modules/Child");
            string theirsTree = GitOutput(root, gitExe, "write-tree").Trim();
            Git(root, gitExe, $"read-tree {oursTree}");
            Git(root, gitExe, $"read-tree -i -m {baseTree} {oursTree} {theirsTree}");

            string unmerged = GitOutput(root, gitExe, "ls-files --unmerged");
            Assert.Contains("\tModules/Child", unmerged, StringComparison.Ordinal);
            Assert.Contains("Modules/Child",
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(root, gitExe)));

            GitInfo.ReviewDiffResult review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, theirsLink, gitExe),
                r => r.Diff.Status == "unmerged", r => r.Diff.Status ?? "<null>", "ReviewDiff status unmerged");
            Assert.Equal("unmerged", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewRefusesDifferentIndexAndWorktreePayloadsForTheSamePath()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-layered").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            string source = Path.Combine(root, "Source.cs");
            string original = File.ReadAllText(source);

            File.WriteAllText(source, "class StagedPayload { }\n");
            Git(root, gitExe, "add Source.cs");
            File.WriteAllText(source, original);

            var indexingDirty = GitInfo.DirtyFiles(root, gitExe);
            Assert.Contains("Source.cs", Assert.IsType<List<string>>(indexingDirty));

            var standalone = GitInfo.DiffHunksDetailed(root, head, gitExe);
            var overwritten = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "layered_changes", r => r.Diff.Status ?? "<null>", "ReviewDiff status layered_changes");
            Assert.Equal("layered_changes", standalone.Status);
            Assert.Null(standalone.Files);
            Assert.Equal("layered_changes", overwritten.Diff.Status);
            Assert.Null(overwritten.Diff.Files);
            Assert.Null(overwritten.Dirty);

            Git(root, gitExe, "reset -q --hard HEAD");
            Git(root, gitExe, "rm -q Source.cs");
            File.WriteAllText(source, "class RecreatedPayload { }\n");

            var recreated = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "layered_changes", r => r.Diff.Status ?? "<null>", "ReviewDiff status layered_changes");
            Assert.Equal("layered_changes", recreated.Diff.Status);
            Assert.Null(recreated.Diff.Files);
            Assert.Null(recreated.Dirty);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void PatchParserAcceptsLocalizedStructuralNoNewlineMarkers()
    {
        const string patch =
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n" +
            "-old\n\\ Kein Zeilenumbruch am Dateiende\n" +
            "+new\n\\ Kein Zeilenumbruch am Dateiende\n";

        var result = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', "X.cs"), patch));

        Assert.Equal("ok", result.Status);
        var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(result.Files));
        Assert.Equal((1, 1), Assert.Single(file.Ranges));
    }

    [Fact]
    public void PatchParserDecodesGitCQuotedQuoteAndBackslashPaths()
    {
        const string path = "A\"\\B.cs";
        string oldSide = GitQuote("a/" + path);
        string newSide = GitQuote("b/" + path);
        string patch =
            $"diff --git {oldSide} {newSide}\n" +
            "index 1111111..2222222 100644\n" +
            $"--- {oldSide}\n+++ {newSide}\n" +
            "@@ -1 +1 @@\n-old\n+new\n";

        var result = GitInfo.ParseDiffOutput(DiffOutput(
            new RawEntry("100644", "100644", 'M', path), patch));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("malformed", result.Status);
            Assert.Null(result.Files);
            return;
        }
        Assert.Equal("ok", result.Status);
        Assert.Equal(path, Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(
            result.Files)).Path);
    }

    [Fact]
    public void UnixGitPreservesQuoteAndLiteralBackslashPathIdentityEndToEnd()
    {
        if (OperatingSystem.IsWindows()) return;
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-unix-paths").FullName;
        try
        {
            InitRepo(root, gitExe);
            const string quotePath = "A\"B.cs";
            const string slashPath = "A\\B.cs";
            const string untrackedPath = "U\\N.cs";
            string longPath = string.Join('/', Enumerable.Range(0, 6)
                .Select(i => new string((char)('a' + i), 90))) + "/Long.cs";
            const string quoteContents = "class Quote { }\n";
            const string longContents = "class LongFormer { }\n";
            File.WriteAllText(Path.Combine(root, quotePath), quoteContents);
            File.WriteAllText(Path.Combine(root, slashPath), "class Slash { }\n");
            string longFullPath = Path.Combine(root, longPath);
            Directory.CreateDirectory(Path.GetDirectoryName(longFullPath)!);
            File.WriteAllText(longFullPath, longContents);
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            Assert.Equal(quoteContents, GitInfo.ShowFile(root, head, quotePath, gitExe));
            Assert.Equal(longContents, GitInfo.ShowFile(root, head, longPath, gitExe));
            File.Delete(Path.Combine(root, quotePath));
            File.Delete(longFullPath);
            File.AppendAllText(Path.Combine(root, slashPath), "// changed\n");
            File.WriteAllText(Path.Combine(root, untrackedPath), "class Untracked { }\n");

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            var files = Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files);
            Assert.Contains(files, file => file.Path == quotePath && file.Deleted);
            Assert.Contains(files, file => file.Path == longPath && file.Deleted);
            Assert.Contains(files, file => file.Path == slashPath);
            var dirty = Assert.IsType<List<string>>(review.Dirty);
            Assert.Contains(quotePath, dirty);
            Assert.Contains(longPath, dirty);
            Assert.Contains(slashPath, dirty);
            Assert.Contains(untrackedPath, dirty);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffParserAllowsNonUtf8HunkBodiesButRejectsNonUtf8Paths()
    {
        const string patch =
            "diff --git a/X.cs b/X.cs\n--- a/X.cs\n+++ b/X.cs\n@@ -1 +1 @@\n-old\n+?\n";
        byte[] body = DiffOutput(new RawEntry("100644", "100644", 'M', "X.cs"), patch);
        int bodyByte = FindSequence(body, Ascii("+?\n")) + 1;
        Assert.True(bodyByte > 0);
        body[bodyByte] = 0xff;

        var bodyResult = GitInfo.ParseDiffOutput(body);
        Assert.Equal("ok", bodyResult.Status);
        Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(bodyResult.Files));

        byte[] badPath = DiffOutput(
            "100644", "100644", 'M', [0x58, 0xff, 0x2e, 0x63, 0x73], patch: []);
        var pathResult = GitInfo.ParseDiffOutput(badPath);
        Assert.Equal("malformed", pathResult.Status);
        Assert.Null(pathResult.Files);
    }

    [Fact]
    public void RawManifestPreservesCaseDistinctGitPaths()
    {
        var result = GitInfo.ParseDiffOutput(DiffOutput(
            new[]
            {
                new RawEntry("100644", "100644", 'M', "Foo.cs"),
                new RawEntry("100644", "100644", 'M', "foo.cs"),
            }, "diff --git a/Foo.cs b/Foo.cs\nindex 1111111..2222222 100644\n" +
               "Binary files a/Foo.cs and b/Foo.cs differ\n" +
               "diff --git a/foo.cs b/foo.cs\nindex 1111111..2222222 100644\n" +
               "Binary files a/foo.cs and b/foo.cs differ\n"));

        Assert.Equal("ok", result.Status);
        var files = Assert.IsType<List<GitInfo.DiffFile>>(result.Files);
        Assert.Equal(new[] { "Foo.cs", "foo.cs" }, files.Select(file => file.Path));
    }

    [Fact]
    public void ReviewAggregationPreservesCaseDistinctGitPaths()
    {
        var changed = NavigationTools.NewReviewPathMap();

        changed["Foo.cs"] = [(1, 1)];
        changed["foo.cs"] = [(2, 2)];

        Assert.Equal(2, changed.Count);
        Assert.Equal((1, 1), Assert.Single(changed["Foo.cs"]));
        Assert.Equal((2, 2), Assert.Single(changed["foo.cs"]));
    }

    [Fact]
    public void ReviewSubmodulePathSamplesStayInsideTheirFixedMetadataBudget()
    {
        string huge = new string('x', 32_000);
        List<string> sample = NavigationTools.BoundedReviewPathSample(
            [huge, "Short/Sub"]);

        Assert.DoesNotContain(huge, sample);
        Assert.Contains("Short/Sub", sample);
        Assert.True(sample.Sum(path =>
            JsonSerializer.SerializeToUtf8Bytes(path).Length + 1) <= 512);

        string escaped = string.Concat(Enumerable.Repeat("<é\\\"", 80));
        List<string> escapedSample = NavigationTools.BoundedReviewPathSample(
            [escaped, "Still/Short"]);
        Assert.DoesNotContain(escaped, escapedSample);
        Assert.Contains("Still/Short", escapedSample);
        Assert.True(escapedSample.Sum(path =>
            JsonSerializer.SerializeToUtf8Bytes(path).Length + 1) <= 512);
    }

    [Fact]
    public void ByteBatchBlobParserPreservesFramingAndAllowsLegacyBlobContent()
    {
        const string oid = "0123456789abcdef0123456789abcdef01234567";
        const string unicode = "Zażółć";
        Assert.Equal(unicode, GitInfo.ParseBatchBlob(BatchBlob(oid, Utf8(unicode))));
        Assert.Null(GitInfo.ParseBatchBlob(Utf8($"{oid} blob 1\nx")));
        Assert.Null(GitInfo.ParseBatchBlob(Utf8($"{oid} blob nope\nx\n")));
        Assert.Equal("caf\uFFFD",
            GitInfo.ParseBatchBlob(BatchBlob(oid, [0x63, 0x61, 0x66, 0xe9])));
    }

    [Fact]
    public void StreamingUtf8ReaderRetainsOnlyTheCharacterCap()
    {
        const int maxChars = 1_000_000;
        byte[] bytes = Enumerable.Repeat((byte)'A', maxChars * 3).ToArray();
        _ = GitInfo.ReadUtf8Bounded(new MemoryStream([0x41]), maxChars: 1);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();

        var result = GitInfo.ReadUtf8Bounded(new MemoryStream(bytes, writable: false), maxChars);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(result.ValidUtf8);
        Assert.True(result.Truncated);
        Assert.Equal(maxChars, result.Text.Length);
        Assert.True(allocated < 12_000_000,
            $"bounded reader allocated {allocated:n0} bytes for a {maxChars:n0}-character cap");
    }

    [Fact]
    public void RunnerDrainsInvalidUtf8BeforeReportingFailure()
    {
        byte[] bytes = [0xff, .. Enumerable.Repeat((byte)'A', 200_000)];
        using var stream = new TrackingReadStream(bytes, maxChunk: 127);

        var result = GitInfo.ReadUtf8Bounded(stream, maxChars: 1024);

        Assert.False(result.ValidUtf8);
        Assert.Equal(bytes.Length, stream.BytesRead);
    }

    [Fact]
    public void RunnerCharacterCapDoesNotPrematurelyTruncateMultibyteUtf8()
    {
        if (!OperatingSystem.IsWindows()) return;
        string script =
            "$b=[Text.Encoding]::UTF8.GetBytes('€€€€');" +
            "$s=[Console]::OpenStandardOutput();$s.Write($b,0,$b.Length)";

        var result = GitInfo.RunProcessEx("powershell.exe", Path.GetTempPath(),
            $"-NoProfile -NonInteractive -Command \"{script}\"", maxOutputChars: 4);

        Assert.Equal("ok", result.Status);
        Assert.False(result.Truncated);
        Assert.Equal("€€€€", result.Output);
    }

    [Fact]
    public void ByteCaptureCapsRetentionWhileDrainingTheChildToCompletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script =
            "$b=[Text.Encoding]::ASCII.GetBytes(('A'*200000));" +
            "$s=[Console]::OpenStandardOutput();$s.Write($b,0,$b.Length)";

        var result = GitInfo.RunProcessEx("powershell.exe", Path.GetTempPath(),
            $"-NoProfile -NonInteractive -Command \"{script}\"",
            waitMs: 10_000, drainMs: 2_000, maxOutputChars: 1024,
            captureBytes: true);

        Assert.Equal("ok", result.Status);
        Assert.True(result.Truncated);
        Assert.Equal(1024, result.OutputBytes.Length);
        Assert.True(result.OutputBytes.ToArray().All(value => value == (byte)'A'));
    }

    [Fact]
    public void FilterSafetyStreamParsersHandleAggregateOutputBeyondTheOldEightMiBCap()
    {
        string path = new string('p', 48) + ".cs";
        byte[] indexRecord = Utf8(
            $"100644 {OldOid} 0\t{path}\0");
        const int repetitions = 170_000;
        using var indexSource = new RepeatedThenTailStream(indexRecord, repetitions, []);
        using var forwarded = new CountingWriteStream();
        var index = GitInfo.PumpTrackedIndex(
            indexSource, forwarded);

        Assert.True(index.Valid);
        Assert.Equal(repetitions, index.Records);
        Assert.True(indexSource.Length > 8L * 1024 * 1024);
        Assert.True(forwarded.BytesWritten > 8L * 1024 * 1024);

        byte[] safe = Utf8($"{path}\0filter\0unspecified\0");
        using var matchedAttributeSource = new RepeatedThenTailStream(safe, repetitions, []);
        var matchedAttributes = GitInfo.ReadFilterAttributes(matchedAttributeSource,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(matchedAttributes.Valid);
        Assert.Equal(index.Records, matchedAttributes.Records);
        Assert.Equal(index.Digest, matchedAttributes.Digest);

        byte[] unsafeTail = Utf8($"{path}\0filter\0sentinel\0");
        using var attributeSource = new RepeatedThenTailStream(safe, repetitions, unsafeTail);
        var unsafePaths = new HashSet<string>(StringComparer.Ordinal);
        var attributes = GitInfo.ReadFilterAttributes(attributeSource,
            new HashSet<string>(["sentinel"], StringComparer.Ordinal), unsafePaths);

        Assert.True(attributes.Valid);
        Assert.Equal(repetitions + 1L, attributes.Records);
        Assert.True(attributeSource.Length > 8L * 1024 * 1024);
        Assert.Equal(path, Assert.Single(unsafePaths));
    }

    [Theory]
    [InlineData("diff.noprefix")]
    [InlineData("diff.mnemonicPrefix")]
    public void DiffHunksForcesCanonicalPrefixesDespiteUserConfig(string setting)
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-prefix").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, $"config {setting} true");
            EditSource(root);

            var result = GitInfo.DiffHunksDetailed(root, head, gitExe);
            Assert.Equal("ok", result.Status);
            var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(result.Files));
            Assert.Equal("Source.cs", file.Path);
            Assert.Equal((5, 5), Assert.Single(file.Ranges));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffPinsReadOnlyAutoRefreshAndDropsOnlyStatOnlyDirt()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-8he-autorefresh").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config diff.autoRefreshIndex false");
            string source = Path.Combine(root, "Source.cs");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(2));
            string indexPath = Path.Combine(root, ".git", "index");
            byte[] indexBefore = File.ReadAllBytes(indexPath);
            DateTime indexWriteBefore = File.GetLastWriteTimeUtc(indexPath);

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            Assert.Empty(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files));
            Assert.Empty(Assert.IsType<List<string>>(review.Dirty));
            Assert.Equal(indexBefore, File.ReadAllBytes(indexPath));
            Assert.Equal(indexWriteBefore, File.GetLastWriteTimeUtc(indexPath));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffHunksForcesZeroInterHunkContextDespiteUserConfig()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-context").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Source.cs"),
                string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n");
            InitRepo(root, gitExe);
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            Git(root, gitExe, "config diff.interHunkContext 99");
            string path = Path.Combine(root, "Source.cs");
            File.WriteAllText(path, File.ReadAllText(path)
                .Replace("line 3", "edited 3").Replace("line 18", "edited 18"));

            var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(
                GitInfo.DiffHunks(root, head, gitExe)));
            Assert.Equal(new[] { (3, 3), (18, 18) }, file.Ranges);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffHunksIgnoresInheritedGitDiffOptsThatOverrideUnifiedContext()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-env-context").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            EditSource(root);
            var inherited = new Dictionary<string, string?>
            {
                ["GIT_DIFF_OPTS"] = "--unified=3",
            };

            var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(
                GitInfo.DiffHunks(root, head, gitExe, inherited)));
            Assert.Equal((5, 5), Assert.Single(file.Ranges));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffClearsInheritedRepositoryObjectAndIndexSelection()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string container = Directory.CreateTempSubdirectory(
            "codenav-y8e-inherited-selection").FullName;
        string target = Path.Combine(container, "target");
        string decoy = Path.Combine(container, "decoy");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(decoy);
            string targetHead = CreateRepo(target, gitExe);
            _ = CreateRepo(decoy, gitExe);
            File.WriteAllText(Path.Combine(decoy, "Decoy.cs"), "class Decoy { }\n");
            Git(decoy, gitExe, "add -A");
            Git(decoy, gitExe, "commit -q -m decoy");
            EditSource(target);

            string decoyGitDir = Path.Combine(decoy, ".git");
            var inherited = new Dictionary<string, string?>
            {
                ["GIT_DIR"] = decoyGitDir,
                ["GIT_WORK_TREE"] = decoy,
                ["GIT_COMMON_DIR"] = decoyGitDir,
                ["GIT_INDEX_FILE"] = Path.Combine(decoyGitDir, "index"),
                ["GIT_OBJECT_DIRECTORY"] = Path.Combine(decoyGitDir, "objects"),
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(decoyGitDir, "objects"),
            };

            GitInfo.ReviewDiffResult review = SemanticRetry.Until( // n7ly sweep
                () => GitInfo.ReviewDiff(target, targetHead, gitExe, inherited),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            GitInfo.DiffFile changed = Assert.Single(
                Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files));
            Assert.Equal("Source.cs", changed.Path);
            Assert.DoesNotContain(Assert.IsType<List<string>>(review.Dirty),
                path => path == "Decoy.cs");
        }
        finally { Cleanup(container); }
    }

    [Fact]
    public void HeadCommitExClearsInheritedRepositoryObjectAndIndexSelection()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string container = Directory.CreateTempSubdirectory(
            "codenav-y8e-head-inherited-selection").FullName;
        string target = Path.Combine(container, "target");
        string decoy = Path.Combine(container, "decoy");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(decoy);
            string targetHead = CreateRepo(target, gitExe);
            _ = CreateRepo(decoy, gitExe);
            Git(decoy, gitExe, "commit --allow-empty -q -m distinct-decoy-head");
            string decoyHead = GitOutput(decoy, gitExe, "rev-parse HEAD").Trim();
            Assert.NotEqual(targetHead, decoyHead);

            string decoyGitDir = Path.Combine(decoy, ".git");
            var inherited = new Dictionary<string, string?>
            {
                ["GIT_DIR"] = decoyGitDir,
                ["GIT_WORK_TREE"] = decoy,
                ["GIT_COMMON_DIR"] = decoyGitDir,
                ["GIT_INDEX_FILE"] = Path.Combine(decoyGitDir, "index"),
                ["GIT_OBJECT_DIRECTORY"] = Path.Combine(decoyGitDir, "objects"),
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(decoyGitDir, "objects"),
            };

            var (value, status) = GitInfo.HeadCommitEx(target, gitExe, inherited);

            Assert.Equal("ok", status);
            Assert.Equal(targetHead, value);
            Assert.NotEqual(decoyHead, value);
        }
        finally { Cleanup(container); }
    }

    [Fact]
    public void DiffHunksForcesStableGitLocaleAfterCallerOverrides()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-locale").FullName;
        try
        {
            string head = CreateRepo(root, realGit);
            EditSource(root);
            string wrapper = Path.Combine(root, "git-locale.cmd");
            string log = Path.Combine(root, "locale.txt");
            File.WriteAllText(wrapper,
                $"@echo off\r\n>>\"{log}\" echo %LC_ALL%^|%LANG%^|%LANGUAGE%^|%GIT_NO_LAZY_FETCH%^|%GIT_TERMINAL_PROMPT%^|%GIT_ALLOW_PROTOCOL%\r\n\"{realGit}\" %*\r\n");
            var inherited = new Dictionary<string, string?>
            {
                ["LC_ALL"] = "de_DE.UTF-8",
                ["LANG"] = "de_DE.UTF-8",
                ["LANGUAGE"] = "de_DE",
                ["GIT_NO_LAZY_FETCH"] = "0",
                ["GIT_TERMINAL_PROMPT"] = "1",
                ["GIT_ALLOW_PROTOCOL"] = "file:ext",
            };

            var result = GitInfo.DiffHunksDetailed(root, head, wrapper, inherited);

            Assert.Equal("ok", result.Status);
            Assert.All(File.ReadAllLines(log), line => Assert.Equal("C|C|C|1|0|:", line));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffHunksOverridesConfiguredOrderFile()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-order").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config diff.orderFile missing-order-file");
            EditSource(root);

            var result = GitInfo.DiffHunksDetailed(root, head, gitExe);
            Assert.Equal("ok", result.Status);
            var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(result.Files));
            Assert.Equal("Source.cs", file.Path);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffHunksDisablesExternalDiffHelpers()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-ext").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config diff.external codenav-command-that-must-not-run");
            EditSource(root);

            var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(
                GitInfo.DiffHunks(root, head, gitExe)));
            Assert.Equal("Source.cs", file.Path);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffHunksDisablesGitExternalDiffInheritedByAWrapper()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-env-helper").FullName;
        try
        {
            string head = CreateRepo(root, realGit);
            EditSource(root);
            string tools = Path.Combine(root, "helper-tools");
            Directory.CreateDirectory(tools);
            string marker = Path.Combine(tools, "external-ran.txt");
            string helper = Path.Combine(tools, "external.cmd");
            File.WriteAllText(helper, $"@echo ran>\"{marker}\"\r\nexit /b 1\r\n");
            string wrapper = Path.Combine(tools, "git.cmd");
            File.WriteAllText(wrapper,
                $"@echo off\r\nset \"GIT_EXTERNAL_DIFF={helper}\"\r\n\"{realGit}\" %*\r\n");

            var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(
                GitInfo.DiffHunks(root, head, wrapper)));
            Assert.Equal("Source.cs", file.Path);
            Assert.False(File.Exists(marker), "GIT_EXTERNAL_DIFF executed despite --no-ext-diff");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DiffHunksDisablesTextconvHelpers()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-textconv").FullName;
        try
        {
            string head = CreateRepo(root, gitExe, "*.cs diff=codenav\n");
            Git(root, gitExe, "config diff.codenav.textconv codenav-command-that-must-not-run");
            EditSource(root);

            var file = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(
                GitInfo.DiffHunks(root, head, gitExe)));
            Assert.Equal("Source.cs", file.Path);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("clean", false, false)]
    [InlineData("process", false, false)]
    [InlineData("clean", true, false)]
    [InlineData("clean", false, true)]
    public void DiffHunksRefusesRequiredContentFiltersWithoutExecutingThem(
        string filterKind, bool inheritedConfig, bool infoAttributes)
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-filter").FullName;
        try
        {
            string? attributes = infoAttributes ? null : "*.cs filter=codenavsentinel\n";
            string head = CreateRepo(root, gitExe, attributes);
            if (infoAttributes)
            {
                File.WriteAllText(Path.Combine(root, ".git", "info", "attributes"),
                    "*.cs filter=codenavsentinel\n");
            }

            string tools = Path.Combine(root, "helper-tools");
            Directory.CreateDirectory(tools);
            string marker = Path.Combine(tools, "filter-ran.txt");
            File.WriteAllText(Path.Combine(tools, "filter.cmd"),
                $"@echo off\r\n>\"{marker}\" echo ran\r\nexit /b 1\r\n");

            Dictionary<string, string?>? environment = null;
            if (inheritedConfig)
            {
                environment = new Dictionary<string, string?>
                {
                    ["GIT_CONFIG_COUNT"] = "2",
                    ["GIT_CONFIG_KEY_0"] = $"filter.codenavsentinel.{filterKind}",
                    ["GIT_CONFIG_VALUE_0"] = "helper-tools/filter.cmd",
                    ["GIT_CONFIG_KEY_1"] = "filter.codenavsentinel.required",
                    ["GIT_CONFIG_VALUE_1"] = "true",
                };
            }
            else
            {
                Git(root, gitExe,
                    $"config filter.codenavsentinel.{filterKind} helper-tools/filter.cmd");
                Git(root, gitExe, "config filter.codenavsentinel.required true");
            }

            var cleanResult = GitInfo.DiffHunksDetailed(root, head, gitExe, environment);
            Assert.False(File.Exists(marker), $"Git executed the configured {filterKind} filter");
            Assert.Equal("filter_unsafe", cleanResult.Status);
            Assert.Null(cleanResult.Files);

            EditSource(root);
            var result = GitInfo.DiffHunksDetailed(root, head, gitExe, environment);
            var dirty = GitInfo.DirtyFiles(root, gitExe, environment);

            Assert.False(File.Exists(marker), $"Git executed the configured {filterKind} filter");
            Assert.Equal("filter_unsafe", result.Status);
            Assert.Null(result.Files);
            Assert.Null(dirty);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void InactiveFilterWithSlashInDriverNameDoesNotBlockReview()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-inactive-filter").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config filter.media/lfs.clean command-that-must-not-run");
            Git(root, gitExe, "config filter.media/lfs.required true");
            EditSource(root);

            var result = GitInfo.DiffHunksDetailed(root, head, gitExe);

            Assert.Equal("ok", result.Status);
            Assert.Equal("Source.cs", Assert.Single(
                Assert.IsType<List<GitInfo.DiffFile>>(result.Files)).Path);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ActiveAttributeWithAnEffectivelyEmptyFilterCommandDoesNotBlockReview()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-empty-filter").FullName;
        try
        {
            string head = CreateRepo(root, gitExe, "*.cs filter=emptycommand\n");
            EditSource(root);
            var environment = new Dictionary<string, string?>
            {
                ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "filter.emptycommand.clean",
                ["GIT_CONFIG_VALUE_0"] = "",
                ["GIT_CONFIG_KEY_1"] = "filter.emptycommand.required",
                ["GIT_CONFIG_VALUE_1"] = "true",
            };

            var result = GitInfo.DiffHunksDetailed(root, head, gitExe, environment);

            Assert.Equal("ok", result.Status);
            Assert.Equal("Source.cs", Assert.Single(
                Assert.IsType<List<GitInfo.DiffFile>>(result.Files)).Path);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ActiveFilterOnATrackedSymlinkRepresentationFailsClosedWithoutExecution()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-symlink-filter").FullName;
        try
        {
            InitRepo(root, gitExe);
            Git(root, gitExe, "config core.symlinks false");
            File.WriteAllText(Path.Combine(root, "Type.cs"), "old target");
            File.WriteAllText(Path.Combine(root, ".gitattributes"),
                "Type.cs filter=symlinksentinel\n");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            string target = Path.Combine(root, "LinkTarget.txt");
            File.WriteAllText(target, "new target");
            string blob = GitOutput(root, gitExe, "hash-object -w LinkTarget.txt").Trim();
            File.Delete(target);
            Git(root, gitExe, $"update-index --add --cacheinfo 120000,{blob},Type.cs");
            File.WriteAllText(Path.Combine(root, "Type.cs"), "new target");
            string marker = Path.Combine(root, "filter-ran.txt");
            string markerForGit = marker.Replace('\\', '/');
            Git(root, gitExe,
                $"config filter.symlinksentinel.clean \"echo ran > {markerForGit}; cat\"");
            Git(root, gitExe, "config filter.symlinksentinel.required true");

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "filter_unsafe", r => r.Diff.Status ?? "<null>", "ReviewDiff status filter_unsafe");

            Assert.False(File.Exists(marker));
            Assert.Equal("filter_unsafe", review.Diff.Status);
            Assert.Null(review.Diff.Files);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LateFilterDriverCannotExecuteAfterTheSafetyPreflight()
    {
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-late-filter").FullName;
        try
        {
            string nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "Source.cs"),
                "namespace Demo { class LateFilter { int Value => 1; } }\n");
            File.WriteAllText(Path.Combine(root, ".gitattributes"),
                "[attr]late-filter filter=late\nnested/*.cs late-filter\n");
            InitRepo(root, realGit);
            Git(root, realGit, "add -A");
            Git(root, realGit, "commit -q -m initial");
            string head = GitOutput(root, realGit, "rev-parse HEAD").Trim();
            File.WriteAllText(Path.Combine(nested, "Source.cs"),
                "namespace Demo { class LateFilter { int Value => 2; } }\n");

            string tools = Path.Combine(root, "helper-tools");
            Directory.CreateDirectory(tools);
            File.AppendAllText(Path.Combine(root, ".git", "info", "exclude"),
                "helper-tools/\n");
            string marker = Path.Combine(tools, "late-filter-ran.txt");
            string markerForGit = marker.Replace('\\', '/');
            string triggered = Path.Combine(tools, "late-filter-injected.txt");
            string wrapper;
            if (OperatingSystem.IsWindows())
            {
                wrapper = Path.Combine(tools, "git-late-filter.cmd");
                File.WriteAllText(wrapper,
                    "@echo off\r\n" +
                    "set \"args=%*\"\r\n" +
                    "if \"%args:diff --raw -z --patch=%\"==\"%args%\" goto ordinary\r\n" +
                    $">\"{triggered}\" echo injected\r\n" +
                    $"\"{realGit}\" -c \"filter.late.clean=echo ran > {markerForGit}; cat\" -c filter.late.required=true %*\r\n" +
                    "exit /b %errorlevel%\r\n" +
                    ":ordinary\r\n" +
                    $"\"{realGit}\" %*\r\n");
            }
            else
            {
                wrapper = Path.Combine(tools, "git-late-filter.sh");
                File.WriteAllText(wrapper,
                    "#!/bin/sh\n" +
                    "case \" $* \" in\n" +
                    "  *\" diff --raw -z --patch \"*)\n" +
                    $"    printf injected > \"{triggered}\"\n" +
                    $"    exec \"{realGit}\" -c 'filter.late.clean=echo ran > {markerForGit}; cat' -c filter.late.required=true \"$@\";;\n" +
                    "esac\n" +
                    $"exec \"{realGit}\" \"$@\"\n");
                File.SetUnixFileMode(wrapper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, wrapper),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.True(File.Exists(triggered), "the wrapper did not inject the late driver");
            Assert.False(File.Exists(marker),
                "a filter driver introduced after preflight must remain unselectable");
            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "nested/Source.cs");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffReusesOneFilterSafetyPreflightAcrossSnapshotSandwich()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-one-preflight").FullName;
        try
        {
            string head = CreateRepo(root, realGit);
            Git(root, realGit, "config filter.media/lfs.clean command-that-must-not-run");
            EditSource(root);
            string wrapper = Path.Combine(root, "git-count.cmd");
            string log = Path.Combine(root, "git-args.txt");
            File.AppendAllText(Path.Combine(root, ".git", "info", "exclude"),
                "git-count.cmd\ngit-args.txt\n");
            File.WriteAllText(wrapper,
                $"@echo off\r\n>>\"{log}\" echo(%*\r\n\"{realGit}\" %*\r\n");

            var review = GitInfo.ReviewDiff(root, head, wrapper);

            Assert.Equal("ok", review.Diff.Status);
            Assert.NotNull(review.Dirty);
            string[] invocations = File.ReadAllLines(log);
            Assert.Single(invocations, line => line.Contains(
                "config --includes --null --get-regexp filter[.]",
                StringComparison.Ordinal));
            Assert.Single(invocations, line => line.Contains(
                "ls-files -z --cached --stage", StringComparison.Ordinal));
            Assert.Single(invocations, line => line.Contains(
                "check-attr -z --stdin filter", StringComparison.Ordinal));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "diff --raw -z --patch", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "diff --raw -z --numstat", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "diff --cached --name-only -z", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "ls-files -z --others --exclude-standard", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "ls-files -z --unmerged", StringComparison.Ordinal)));
            Assert.DoesNotContain(invocations, line => line.Contains(
                "status --porcelain", StringComparison.Ordinal));
            Assert.All(invocations, line =>
                Assert.Contains("-c submodule.recurse=false", line, StringComparison.Ordinal));
            Assert.All(invocations, line =>
                Assert.Contains("-c protocol.allow=never", line, StringComparison.Ordinal));
            Assert.All(invocations, line =>
                Assert.Contains("-c diff.autoRefreshIndex=false", line, StringComparison.Ordinal));
            Assert.Contains(invocations, line => line.Contains(
                "diff --raw -z --patch", StringComparison.Ordinal) &&
                line.Contains("--ignore-submodules=dirty", StringComparison.Ordinal));
            Assert.Contains(invocations, line => line.Contains(
                "diff --cached --name-only -z", StringComparison.Ordinal) &&
                line.Contains("--ignore-submodules=dirty", StringComparison.Ordinal));
            Assert.Contains(invocations, line => line.Contains(
                "diff --raw -z --numstat", StringComparison.Ordinal) &&
                line.Contains("--ignore-submodules=dirty", StringComparison.Ordinal));
            Assert.DoesNotContain(invocations, line => line.Contains(
                "--ignore-submodules=none", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilterSafetyPipelineFailsClosedWhenAttributeCheckerExitsEarlyWithoutHanging()
    {
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-bbm-checker-exit").FullName;
        try
        {
            string head = CreateRepo(root, realGit);
            Git(root, realGit, "config filter.pipeline.clean cat");
            EditSource(root);
            string wrapper;
            if (OperatingSystem.IsWindows())
            {
                wrapper = Path.Combine(root, "git-checker-exit.cmd");
                File.WriteAllText(wrapper,
                    $"@echo off\r\nset \"args=%*\"\r\nif not \"%args:check-attr=%\"==\"%args%\" exit /b 17\r\n\"{realGit}\" %*\r\n");
            }
            else
            {
                wrapper = Path.Combine(root, "git-checker-exit.sh");
                File.WriteAllText(wrapper,
                    $"#!/bin/sh\ncase \" $* \" in *\" check-attr \"*) exit 17;; esac\nexec '{realGit}' \"$@\"\n");
                File.SetUnixFileMode(wrapper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var sw = Stopwatch.StartNew();
            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => { sw.Restart(); return GitInfo.ReviewDiff(root, head, wrapper); }, // review C3: the promptness bound measures ONE attempt, not retries+sleeps
                r => r.Diff.Status == "config_failed", r => r.Diff.Status ?? "<null>", "ReviewDiff status config_failed");
            sw.Stop();

            Assert.Equal("config_failed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"early checker exit was not drained/cut promptly: {sw.Elapsed}");
        }
        finally { Cleanup(root); }
    }
}
