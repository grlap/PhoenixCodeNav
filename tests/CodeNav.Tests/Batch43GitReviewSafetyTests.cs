using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodeNav.Tests;

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
            var result = GitInfo.ReviewDiff(root, head, gitExe).Diff;

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

            var review = GitInfo.ReviewDiff(root, head, gitExe);

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

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, theirsLink, gitExe);
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
            var overwritten = GitInfo.ReviewDiff(root, head, gitExe);
            Assert.Equal("layered_changes", standalone.Status);
            Assert.Null(standalone.Files);
            Assert.Equal("layered_changes", overwritten.Diff.Status);
            Assert.Null(overwritten.Diff.Files);
            Assert.Null(overwritten.Dirty);

            Git(root, gitExe, "reset -q --hard HEAD");
            Git(root, gitExe, "rm -q Source.cs");
            File.WriteAllText(source, "class RecreatedPayload { }\n");

            var recreated = GitInfo.ReviewDiff(root, head, gitExe);
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

            var review = GitInfo.ReviewDiff(root, head, gitExe);

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

            var review = GitInfo.ReviewDiff(root, head, gitExe);

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

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(
                target, targetHead, gitExe, inherited);

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

            var review = GitInfo.ReviewDiff(root, head, gitExe);

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

            var review = GitInfo.ReviewDiff(root, head, wrapper);

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
            var review = GitInfo.ReviewDiff(root, head, wrapper);
            sw.Stop();

            Assert.Equal("config_failed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                $"early checker exit was not drained/cut promptly: {sw.Elapsed}");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewExcludesSubmoduleWorktreeDirtAndReportsCoverageWithoutExecutingChildFilter()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-submodule-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-submodule-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            File.WriteAllText(Path.Combine(origin, ".gitattributes"),
                "Child.cs filter=childsentinel\n");
            Git(origin, gitExe, "config filter.childsentinel.clean cat");
            Git(origin, gitExe, "config filter.childsentinel.required true");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");

            InitRepo(root, gitExe);
            File.WriteAllText(Path.Combine(root, "Root.cs"), "class Root { }\n");
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            File.WriteAllText(Path.Combine(root, ".gitattributes"),
                "Sub filter=parentsentinel\n");
            Git(root, gitExe, "config -f .gitmodules submodule.Sub.ignore all");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            Git(root, gitExe, "config diff.ignoreSubmodules all");
            Git(root, gitExe, "config submodule.recurse true");
            Git(root, gitExe, "config diff.submodule diff");
            string tools = Path.Combine(root, "Sub", "helper-tools");
            Directory.CreateDirectory(tools);
            string marker = Path.Combine(tools, "child-filter-ran.txt");
            string helper = Path.Combine(tools,
                OperatingSystem.IsWindows() ? "filter.cmd" : "filter.sh");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(helper,
                    $"@echo off\r\n>\"{marker}\" echo ran\r\nexit /b 1\r\n");
            }
            else
            {
                File.WriteAllText(helper,
                    "#!/bin/sh\nprintf ran > \"$(dirname \"$0\")/child-filter-ran.txt\"\nexit 1\n");
                File.SetUnixFileMode(helper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Git(Path.Combine(root, "Sub"), gitExe,
                $"config filter.childsentinel.clean \"{helper.Replace('\\', '/')}\"");
            Git(Path.Combine(root, "Sub"), gitExe,
                "config filter.childsentinel.required true");
            Git(root, gitExe,
                $"config filter.parentsentinel.clean \"{helper.Replace('\\', '/')}\"");
            Git(root, gitExe, "config filter.parentsentinel.required true");
            File.SetLastWriteTimeUtc(Path.Combine(root, "Sub", "Child.cs"),
                DateTime.UtcNow.AddMinutes(2));

            var review = GitInfo.ReviewDiff(root, head, gitExe);
            var result = review.Diff;
            var dirty = review.Dirty;
            var standaloneDirty = GitInfo.DirtyFiles(root, gitExe);
            var standaloneDetailed = GitInfo.DiffHunksDetailed(root, head, gitExe);
            var standaloneSimple = GitInfo.DiffHunks(root, head, gitExe);

            Assert.False(File.Exists(marker),
                "parent review executed a child repository clean filter");
            Assert.Equal("ok", result.Status);
            Assert.Empty(Assert.IsType<List<GitInfo.DiffFile>>(result.Files));
            Assert.DoesNotContain("Sub", Assert.IsType<List<string>>(dirty));
            var coverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                review.ExcludedSubmoduleWorktrees);
            Assert.Equal(1, coverage.Count);
            Assert.Equal("Sub", Assert.Single(coverage.SamplePaths));
            Assert.Null(review.ExcludedUntrackedRepositories);
            var resultCoverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                result.ExcludedSubmoduleWorktrees);
            Assert.Equal(coverage.Count, resultCoverage.Count);
            Assert.Equal(coverage.SamplePaths, resultCoverage.SamplePaths);
            Assert.Equal("ok", standaloneDetailed.Status);
            var standaloneCoverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                standaloneDetailed.ExcludedSubmoduleWorktrees);
            Assert.Equal(coverage.Count, standaloneCoverage.Count);
            Assert.Equal(coverage.SamplePaths, standaloneCoverage.SamplePaths);
            Assert.Null(standaloneSimple); // simple API cannot silently discard partial coverage
            Assert.Null(standaloneDirty); // caller must take its honest full-sweep fallback
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void ReviewStillReportsAStagedGitlinkChangeWhileChildDirtIsExcluded()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");

            InitRepo(root, gitExe);
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            File.AppendAllText(Path.Combine(origin, "Child.cs"), "// next\n");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m next");
            string next = GitOutput(origin, gitExe, "rev-parse HEAD").Trim();
            string child = Path.Combine(root, "Sub");
            Git(child, gitExe, "-c protocol.file.allow=always fetch -q origin");
            Git(child, gitExe, $"checkout -q {next}");
            Git(root, gitExe, "add Sub");

            var review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            var link = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Sub");
            Assert.True(link.SubmoduleLink);
            Assert.Equal("Sub", Assert.Single(review.ChangedSubmoduleLinks));
            Assert.Contains("Sub", Assert.IsType<List<string>>(review.Dirty));
            Assert.Equal("Sub", Assert.Single(Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                review.ExcludedSubmoduleWorktrees).SamplePaths));
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void ReviewReportsADeletedGitlinkEvenAfterItLeavesTheCurrentIndex()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-delete-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-delete-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");
            InitRepo(root, gitExe);
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            Git(root, gitExe, "rm -q -f Sub");
            var review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            var deleted = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Sub");
            Assert.True(deleted.Deleted);
            Assert.True(deleted.SubmoduleLink);
            Assert.Equal("Sub", Assert.Single(review.ChangedSubmoduleLinks));
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void IndexOnlySubmoduleRemovalIsLayeredAndStillReportsExcludedChildCoverage()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-retained-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-retained-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            File.WriteAllText(Path.Combine(origin, ".gitattributes"),
                "Child.cs filter=childsentinel\n");
            Git(origin, gitExe, "config filter.childsentinel.clean cat");
            Git(origin, gitExe, "config filter.childsentinel.required true");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");

            InitRepo(root, gitExe);
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            string child = Path.Combine(root, "Sub");
            string marker = Path.Combine(child, "child-filter-ran.txt");
            string helper = Path.Combine(child,
                OperatingSystem.IsWindows() ? "filter.cmd" : "filter.sh");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(helper,
                    $"@echo off\r\n>\"{marker}\" echo ran\r\nexit /b 1\r\n");
            }
            else
            {
                File.WriteAllText(helper,
                    "#!/bin/sh\nprintf ran > \"$(dirname \"$0\")/child-filter-ran.txt\"\nexit 1\n");
                File.SetUnixFileMode(helper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Git(child, gitExe,
                $"config filter.childsentinel.clean \"{helper.Replace('\\', '/')}\"");
            Git(child, gitExe, "config filter.childsentinel.required true");
            File.AppendAllText(Path.Combine(child, "Child.cs"), "// dirty\n");
            Git(root, gitExe, "rm --cached -q -f Sub");

            var review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.False(File.Exists(marker),
                "parent review executed a retained child repository clean filter");
            Assert.Equal("layered_changes", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Equal("Sub", Assert.Single(review.ChangedSubmoduleLinks));
            var coverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                review.ExcludedSubmoduleWorktrees);
            Assert.Equal(1, coverage.Count);
            Assert.Equal("Sub", Assert.Single(coverage.SamplePaths));
            Assert.Null(review.ExcludedUntrackedRepositories);
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void DiffHunksReturnsAnEmptyListForACleanTree()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-clean").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Assert.Empty(Assert.IsType<List<GitInfo.DiffFile>>(
                GitInfo.DiffHunks(root, head, gitExe)));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DirtyFilesSupportsAnUnbornRepositoryInsideTheFilterSandbox()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-unborn-dirt").FullName;
        try
        {
            InitRepo(root, gitExe);
            Directory.CreateDirectory(Path.Combine(root, "New"));
            File.WriteAllText(Path.Combine(root, "New", "Source.cs"), "class Unborn { }\n");

            Assert.Contains("New/Source.cs",
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(root, gitExe)));

            Git(root, gitExe, "add New/Source.cs");
            Assert.Contains("New/Source.cs",
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(root, gitExe)));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilterSandboxDoesNotDependOnTheRepositoriesRefStorageBackend()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-reftable").FullName;
        try
        {
            var initialized = GitInfo.RunProcessEx(gitExe, root,
                "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " +
                "init -q -b main --ref-format=reftable", waitMs: 20_000);
            if (initialized.Status != "ok") return; // Older Git builds do not expose reftable.
            Git(root, gitExe, "config user.email test@example.com");
            Git(root, gitExe, "config user.name CodeNavTest");
            Git(root, gitExe, "config commit.gpgsign false");
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Initial { }\n");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Changed { }\n");

            var review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
            Assert.Contains("Source.cs", Assert.IsType<List<string>>(review.Dirty));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilterSandboxPreservesTheRepositoriesObjectFormat()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-sha256").FullName;
        try
        {
            var initialized = GitInfo.RunProcessEx(gitExe, root,
                "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " +
                "init -q -b main --object-format=sha256", waitMs: 20_000);
            if (initialized.Status != "ok") return; // Older Git builds may omit SHA-256 repos.
            Git(root, gitExe, "config user.email test@example.com");
            Git(root, gitExe, "config user.name CodeNavTest");
            Git(root, gitExe, "config commit.gpgsign false");
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Initial256 { }\n");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            Assert.Equal(64, head.Length);
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Second256 { }\n");
            Git(root, gitExe, "add Source.cs");
            Git(root, gitExe, "commit -q -m second");
            string second = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            string fortyHex = head[..40];
            Git(root, gitExe, $"branch {fortyHex} {second}");

            Assert.Null(GitInfo.ResolveRef(root, fortyHex, gitExe));
            Assert.Equal(head, GitInfo.ResolveRef(root, head, gitExe));

            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Changed256 { }\n");

            var review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
            Assert.Contains("Source.cs", Assert.IsType<List<string>>(review.Dirty));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilterSandboxTreatsAMissingRepositoryFormatKeyAsLegacyVersionZero()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-implicit-format0").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config --local --unset core.repositoryformatversion");
            EditSource(root);

            var review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
            Assert.Contains("Source.cs", Assert.IsType<List<string>>(review.Dirty));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task UnixMetadataFifoIsRejectedWithoutWaitingForAWriter()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-fifo").FullName;
        string fifo = Path.Combine(root, "metadata");
        try
        {
            Assert.Equal(0, MakeFifo(fifo, 0x180)); // 0600
            Task<byte[]?> read = Task.Run(() =>
                GitInfo.ReadBoundedRegularFile(fifo, 1024, root));
            try
            {
                Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            catch (TimeoutException)
            {
                // Rescue the deliberately reintroduced buggy implementation: its blocking FIFO
                // reader is released by one writer byte, so a red test never leaks a stuck task.
                await Task.Run(() =>
                {
                    using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite);
                    writer.WriteByte(1);
                }).WaitAsync(TimeSpan.FromSeconds(2));
                await read.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Fail("repository metadata reads must not block on a FIFO");
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GitConfigQuotingPreservesLiteralBackslashes()
    {
        Assert.Equal("\"/tmp/repo\\\\name/config\"",
            GitInfo.QuoteGitConfigValue("/tmp/repo\\name/config"));
    }

    [Fact]
    public void WindowsGitPathsRejectBackslashesBeforeFilesystemUse()
    {
        const string hostile = @"safe\..\outside.cs";
        Assert.Equal(!OperatingSystem.IsWindows(), GitInfo.IsSafeRelativeGitPath(hostile));
        Assert.True(GitInfo.IsSafeRelativeGitPath("safe/outside.cs"));
    }

    [Fact]
    public void MetadataSnapshotAcceptsOnlyBoundedRegularFiles()
    {
        string root = Directory.CreateTempSubdirectory("codenav-y8e-metadata-kind").FullName;
        try
        {
            string missing = Path.Combine(root, "missing");
            Assert.Empty(Assert.IsType<byte[]>(
                GitInfo.ReadBoundedRegularFile(missing, 4, root)));
            string directory = Directory.CreateDirectory(Path.Combine(root, "directory")).FullName;
            Assert.Null(GitInfo.ReadBoundedRegularFile(directory, 4, root));

            string regular = Path.Combine(root, "regular");
            File.WriteAllBytes(regular, [1, 2, 3, 4]);
            Assert.Equal(new byte[] { 1, 2, 3, 4 },
                GitInfo.ReadBoundedRegularFile(regular, 4, root));
            Assert.Null(GitInfo.ReadBoundedRegularFile(regular, 3, root));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MetadataSnapshotRejectsAnAncestorDirectoryLink()
    {
        string root = Directory.CreateTempSubdirectory("codenav-y8e-metadata-root").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-y8e-metadata-outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "attributes"), "*.cs filter=outside\n");
            string linked = Path.Combine(root, "info");
            CreateDirectoryLink(linked, outside, root);

            Assert.Null(GitInfo.ReadBoundedRegularFile(
                Path.Combine(linked, "attributes"), 1024, root));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(root, "info")); } catch { }
            Cleanup(root);
            Cleanup(outside);
        }
    }

    [Fact]
    public void MetadataSnapshotRejectsALinkInsideTheAllowedRootPath()
    {
        string root = Directory.CreateTempSubdirectory("codenav-y8e-anchor-root").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-y8e-anchor-outside").FullName;
        string linked = Path.Combine(root, "linked-common");
        try
        {
            string realAnchor = Directory.CreateDirectory(Path.Combine(outside, "repo")).FullName;
            string realInfo = Directory.CreateDirectory(Path.Combine(realAnchor, "info")).FullName;
            File.WriteAllText(Path.Combine(realInfo, "attributes"), "*.cs filter=outside\n");
            CreateDirectoryLink(linked, outside, root);
            string redirectedAnchor = Path.Combine(linked, "repo");

            Assert.Null(GitInfo.ReadBoundedRegularFile(
                Path.Combine(redirectedAnchor, "info", "attributes"), 1024,
                redirectedAnchor));
        }
        finally
        {
            try { Directory.Delete(linked); } catch { }
            Cleanup(root);
            Cleanup(outside);
        }
    }

    [Fact]
    public void MetadataSnapshotPreservesAFileSystemRootAnchor()
    {
        string volumeRoot = Assert.IsType<string>(
            Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())));
        string missing = Path.Combine(volumeRoot,
            "codenav-definitely-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(Assert.IsType<byte[]>(
            GitInfo.ReadBoundedRegularFile(missing, 4, volumeRoot)));
    }

    [Fact]
    public void DirtyFilesExpandsOrdinaryDirectoriesButExcludesEmbeddedRepositoriesAtomically()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-qwn-core-nested").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Directory.CreateDirectory(Path.Combine(root, "Ordinary"));
            File.WriteAllText(Path.Combine(root, "Ordinary", "Fresh.cs"), "class Fresh { }\n");
            Assert.Contains("Ordinary/Fresh.cs",
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(root, gitExe)));

            string nested = Path.Combine(root, "NestedRepo");
            Directory.CreateDirectory(nested);
            InitRepo(nested, gitExe);
            File.WriteAllText(Path.Combine(nested, "Child.cs"), "class Child { }\n");
            Git(nested, gitExe, "add -A");
            Git(nested, gitExe, "commit -q -m child");

            Assert.Null(GitInfo.DirtyFiles(root, gitExe));
            var review = GitInfo.ReviewDiff(root, head, gitExe);
            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains("Ordinary/Fresh.cs", Assert.IsType<List<string>>(review.Dirty));
            var coverage = Assert.IsType<GitInfo.UntrackedRepositoryCoverage>(
                review.ExcludedUntrackedRepositories);
            Assert.Equal(1, coverage.Count);
            Assert.Equal("NestedRepo", Assert.Single(coverage.SamplePaths));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DirtyFilesDoesNotCaptureASecondHugePatchJustToFindOnePath()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-huge-dirt").FullName;
        try
        {
            CreateRepo(root, gitExe);
            File.WriteAllText(Path.Combine(root, "Source.cs"),
                "class Huge { string Value = \"" + new string('x', 9 * 1024 * 1024) + "\"; }\n");

            var dirty = GitInfo.DirtyFiles(root, gitExe);

            Assert.Equal("Source.cs", Assert.Single(Assert.IsType<List<string>>(dirty)));
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public void RealBatchWrappersResolveRefsAndReadExactSpecialPathBlobs(string extension)
    {
        if (!OperatingSystem.IsWindows()) return;
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-bhd-wrapper").FullName;
        try
        {
            string relativePath = "Lib/%TEMP% & bang! caret^ (x).cs";
            string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "initial");
            InitRepo(root, realGit);

            string wrapperDir = Path.Combine(root, "wrapper tools");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "wrapper tools/\n");
            Directory.CreateDirectory(wrapperDir);
            string wrapper = Path.Combine(wrapperDir, "git" + extension);
            File.WriteAllText(wrapper,
                $"@echo off\r\n>>\"%~dp0args.txt\" echo(%*\r\n\"{realGit}\" %*\r\n");

            string[] contents = { "", "plain", "one newline\n", "two newlines\n\n", "Zażółć 🐦\n\n" };
            string latest = "";
            for (int i = 0; i < contents.Length; i++)
            {
                File.WriteAllText(fullPath, contents[i]);
                Git(root, realGit, "add -A");
                Git(root, realGit, $"commit -q -m blob-{i}");
                latest = GitOutput(root, realGit, "rev-parse HEAD").Trim();
                Assert.Equal(contents[i], GitInfo.ShowFile(root, latest, relativePath, wrapper));
            }

            Git(root, realGit, "tag release-light");
            Git(root, realGit, "tag -a release-annotated -m tag");
            Assert.Equal(latest, GitInfo.ResolveRef(root, "HEAD", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, "main", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, "release-light", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, "release-annotated", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, latest, wrapper));
            Assert.Null(GitInfo.ResolveRef(root, "missing-ref", wrapper));
            Assert.Null(GitInfo.ShowFile(root, latest, "Lib/missing.cs", wrapper));

            string argsLog = File.ReadAllText(Path.Combine(wrapperDir, "args.txt"));
            Assert.Contains("cat-file --batch-check", argsLog);
            Assert.Contains("cat-file --batch", argsLog);
            Assert.DoesNotContain("%TEMP%", argsLog, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("release-annotated", argsLog, StringComparison.Ordinal);
            Assert.DoesNotContain("HEAD", argsLog, StringComparison.Ordinal);
            Assert.DoesNotContain(latest, argsLog, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ShowFileAcceptsQuotedAndLongSafePathsBecauseTheyTravelOnlyOnStdin()
    {
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-bhd-stdin-path").FullName;
        try
        {
            string head = CreateRepo(root, realGit);
            string expected = File.ReadAllText(Path.Combine(root, "Source.cs"));
            string wrapper;
            if (OperatingSystem.IsWindows())
            {
                wrapper = Path.Combine(root, "git-fixed.cmd");
                File.WriteAllText(wrapper,
                    $"@echo off\r\necho %*|findstr /c:\"rev-parse --show-prefix\" >nul && (echo. & exit /b 0)\r\necho %*|findstr /c:\"cat-file --batch-check\" >nul && (echo {head}:Source.cs|\"{realGit}\" -C \"{root}\" cat-file --batch-check & exit /b 0)\r\necho {head}:Source.cs|\"{realGit}\" -C \"{root}\" cat-file --batch\r\n");
            }
            else
            {
                wrapper = Path.Combine(root, "git-fixed.sh");
                File.WriteAllText(wrapper,
                    $"#!/bin/sh\ncase \"$*\" in *\"rev-parse --show-prefix\"*) printf '\\n'; exit 0;; *\"cat-file --batch-check\"*) printf '%s\\n' '{head}:Source.cs' | '{realGit}' -C '{root}' cat-file --batch-check; exit $?;; esac\nprintf '%s\\n' '{head}:Source.cs' | '{realGit}' -C '{root}' cat-file --batch\n");
                File.SetUnixFileMode(wrapper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            string longPath = string.Join('/', Enumerable.Range(0, 6)
                .Select(i => new string((char)('a' + i), 90))) + "/Long.cs";

            Assert.Equal(expected, GitInfo.ShowFile(root, head, "Quoted\"Path.cs", wrapper));
            Assert.Equal(expected, GitInfo.ShowFile(root, head, longPath, wrapper));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffRejectsASamePathBinaryRewriteDuringSnapshotCapture()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-snapshot-race").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            string binary = Path.Combine(root, "Payload.bin");
            File.WriteAllBytes(binary, [0, 1, 2, 3, 4, 5, 6, 7]);
            Git(root, gitExe, "add Payload.bin");
            Git(root, gitExe, "commit -q -m binary-baseline");
            head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            File.WriteAllBytes(binary, [10, 11, 12, 13, 14, 15, 16, 17]);

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe,
                afterInitialSnapshot: () =>
                    File.WriteAllBytes(binary, [20, 21, 22, 23, 24, 25, 26, 27]));

            Assert.Equal("snapshot_changed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Null(review.UntrackedFiles);
            Assert.Empty(review.ChangedSubmoduleLinks);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffRejectsAnABATransientDuringTheFirstCapture()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-snapshot-aba").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            string binary = Path.Combine(root, "Payload.bin");
            File.WriteAllBytes(binary, [0, 1, 2, 3, 4, 5, 6, 7]);
            Git(root, gitExe, "add Payload.bin");
            Git(root, gitExe, "commit -q -m binary-baseline");
            head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            byte[] stateA = [10, 11, 12, 13, 14, 15, 16, 17];
            byte[] transientB = [20, 21, 22, 23, 24, 25, 26, 27];
            File.WriteAllBytes(binary, stateA);

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe,
                beforeFirstDiff: () => File.WriteAllBytes(binary, transientB),
                afterFirstDiff: () => File.WriteAllBytes(binary, stateA));

            Assert.Equal("snapshot_changed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Null(review.UntrackedFiles);
            Assert.Empty(review.ChangedSubmoduleLinks);
            Assert.Equal(stateA, File.ReadAllBytes(binary));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffRejectsASamePathRewriteAfterTheSecondRawCapture()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-snapshot-final-gap").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            string source = Path.Combine(root, "Source.cs");
            File.WriteAllText(source, "class StateA { }\n");

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe,
                afterSecondDiff: () => File.WriteAllText(source, "class StateB { }\n"));

            Assert.Equal("snapshot_changed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Null(review.UntrackedFiles);
            Assert.Empty(review.ChangedSubmoduleLinks);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProcessRunnerRejectsMissingAndNonDirectoryWorkingDirectories()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-invalid-cwd").FullName;
        try
        {
            string missing = Path.Combine(root, "missing");
            string file = Path.Combine(root, "not-a-directory.txt");
            File.WriteAllText(file, "sentinel");

            Assert.Equal("spawn_failed",
                GitInfo.RunProcessEx(gitExe, missing, "--version").Status);
            Assert.Equal("spawn_failed",
                GitInfo.RunProcessEx(gitExe, file, "--version").Status);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ResolveRefValidatesHexRefsAbbreviationsAndFullObjectIdsThroughStdin()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-bhd-hex-refs").FullName;
        try
        {
            string first = CreateRepo(root, gitExe);
            const string hexBranch = "aaaaaaaa";
            const string hexTag = "bbbbbbbb";
            Git(root, gitExe, $"branch {hexBranch} {first}");
            Git(root, gitExe, $"tag -a {hexTag} {first} -m hex-tag");
            EditSource(root);
            Git(root, gitExe, "add Source.cs");
            Git(root, gitExe, "commit -q -m second");
            string second = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            string abbreviation = second[..12];
            string ambiguousHex = first[..12];
            Git(root, gitExe, $"branch {ambiguousHex} {second}");
            Git(root, gitExe, $"branch {first} {second}");
            string blob = GitOutput(root, gitExe, "rev-parse HEAD:Source.cs").Trim();

            Assert.Equal(first, GitInfo.ResolveRef(root, hexBranch, gitExe));
            Assert.Equal(first, GitInfo.ResolveRef(root, hexTag, gitExe));
            Assert.Equal(second, GitInfo.ResolveRef(root, abbreviation, gitExe));
            Assert.Null(GitInfo.ResolveRef(root, ambiguousHex, gitExe));
            Assert.Equal(first, GitInfo.ResolveRef(root, first, gitExe));
            Assert.Equal(second, GitInfo.ResolveRef(root, second, gitExe));
            Assert.Null(GitInfo.ResolveRef(root, new string('0', second.Length), gitExe));
            Assert.Null(GitInfo.ResolveRef(root, blob, gitExe));
            Assert.Null(GitInfo.ResolveRef(root, "cccccccccccc", gitExe));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RepositoryFormatParserIgnoresUnrelatedValuelessLocalKeys()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-valueless-config").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            File.AppendAllText(Path.Combine(root, ".git", "config"),
                "\n[review]\n\tvalueless\n");
            EditSource(root);

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RepositoryFormatParserRejectsAnInvalidRepositoryFormatVersion()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-invalid-format").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config --local core.repositoryformatversion invalid");
            EditSource(root);

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe);

            Assert.Equal("config_failed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
        }
        finally { Cleanup(root); }
    }

    private const string OldOid = "1111111111111111111111111111111111111111";
    private const string NewOid = "2222222222222222222222222222222222222222";

    private sealed record RawEntry(string OldMode, string NewMode, char Status, string Path);

    private static byte[] DiffOutput(RawEntry entry, string patch) =>
        DiffOutput(new[] { entry }, patch);

    private static byte[] DiffOutput(IEnumerable<RawEntry> entries, string patch)
    {
        using var output = new MemoryStream();
        foreach (RawEntry entry in entries)
        {
            byte[] record = DiffOutput(entry.OldMode, entry.NewMode, entry.Status,
                Utf8(entry.Path), patch: []);
            output.Write(record, 0, record.Length - 1); // Keep each path terminator; omit separator.
        }
        output.WriteByte(0); // Empty raw record separates the manifest from the patch.
        byte[] patchBytes = Utf8(patch);
        output.Write(patchBytes);
        return output.ToArray();
    }

    private static byte[] DiffOutput(
        string oldMode, string newMode, char status, byte[] path, byte[] patch)
    {
        using var output = new MemoryStream();
        byte[] header = Ascii($":{oldMode} {newMode} {OldOid} {NewOid} {status}");
        output.Write(header);
        output.WriteByte(0);
        output.Write(path);
        output.WriteByte(0);
        output.WriteByte(0);
        output.Write(patch);
        return output.ToArray();
    }

    private static byte[] BatchBlob(string oid, byte[] content)
    {
        using var output = new MemoryStream();
        output.Write(Ascii($"{oid} blob {content.Length}\n"));
        output.Write(content);
        output.WriteByte((byte)'\n');
        return output.ToArray();
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static string GitQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static int FindSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }

    private sealed class RepeatedThenTailStream(
        byte[] repeated, int repetitions, byte[] tail) : Stream
    {
        private long _position;
        private readonly long _repeatedLength = (long)repeated.Length * repetitions;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _repeatedLength + tail.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        private int ReadCore(Span<byte> destination)
        {
            int written = 0;
            while (written < destination.Length && _position < Length)
            {
                if (_position < _repeatedLength)
                {
                    int sourceOffset = (int)(_position % repeated.Length);
                    int take = Math.Min(destination.Length - written,
                        repeated.Length - sourceOffset);
                    repeated.AsSpan(sourceOffset, take).CopyTo(destination[written..]);
                    written += take;
                    _position += take;
                }
                else
                {
                    int sourceOffset = (int)(_position - _repeatedLength);
                    int take = Math.Min(destination.Length - written, tail.Length - sourceOffset);
                    tail.AsSpan(sourceOffset, take).CopyTo(destination[written..]);
                    written += take;
                    _position += take;
                }
            }
            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;
        public override void WriteByte(byte value) => BytesWritten++;
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class TrackingReadStream(byte[] bytes, int maxChunk) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            int count = Math.Min(Math.Min(destination.Length, maxChunk), bytes.Length - BytesRead);
            if (count <= 0) return 0;
            bytes.AsSpan(BytesRead, count).CopyTo(destination);
            BytesRead += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static string CreateRepo(string root, string gitExe, string? attributes = null)
    {
        File.WriteAllText(Path.Combine(root, "Source.cs"),
            "namespace Demo\n{\n    class Source\n    {\n        int Value() => 1;\n    }\n}\n");
        if (attributes is not null) File.WriteAllText(Path.Combine(root, ".gitattributes"), attributes);
        InitRepo(root, gitExe);
        Git(root, gitExe, "add -A");
        Git(root, gitExe, "commit -q -m initial");
        return GitOutput(root, gitExe, "rev-parse HEAD").Trim();
    }

    private static void InitRepo(string root, string gitExe)
    {
        Git(root, gitExe, "init -q -b main");
        Git(root, gitExe, "config user.email test@example.com");
        Git(root, gitExe, "config user.name CodeNavTest");
        Git(root, gitExe, "config commit.gpgsign false");
    }

    private static void EditSource(string root)
    {
        string path = Path.Combine(root, "Source.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Value() => 1", "Value() => 2"));
    }

    private static string? FindRealGitExe()
    {
        if (!OperatingSystem.IsWindows()) return GitInfo.ResolveGitExeFrom(null,
            Environment.GetEnvironmentVariable("PATH"));
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(entry.Trim('"'), "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    private static void Git(string root, string gitExe, string args)
    {
        string? output = GitInfo.RunProcess(gitExe, root,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);
        Assert.NotNull(output);
    }

    private static string GitOutput(string root, string gitExe, string args)
    {
        string? output = GitInfo.RunProcess(gitExe, root,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);
        Assert.NotNull(output);
        return output!;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* Windows process handles */ }
    }

    private static void CreateDirectoryLink(string link, string target, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var junction = GitInfo.RunProcessEx(cmd, workingDirectory,
            $"/d /c mklink /J \"{link}\" \"{target}\"", waitMs: 5_000);
        Assert.Equal("ok", junction.Status);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
