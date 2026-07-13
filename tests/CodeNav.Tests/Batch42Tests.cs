using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using static CodeNav.Tests.Batch42Support;

namespace CodeNav.Tests;

// SPLIT (PhoenixCodeNav-6zdy): slice 1 of 3. Fixture + helpers moved to Batch42Support.cs;
// sibling slices: Batch42TestsPart2.cs / Batch42TestsPart3.cs — duration-balanced so xUnit runs
// three classes in parallel instead of one ~98s serial class. Pure move; no test bodies changed.
/// <summary>
/// Batch 42 (v0.11.0) — the review-system centerpiece:
/// 91u — review_pack: one budget-bounded call from diff to impact (hunks -> symbol-span
///       intersection with the innermost policy -> per-symbol digests with handles, dependent
///       splits, test signal, risks), untracked/whole-file handling, project-file visibility,
///       baseRef validation, and DELETION honesty (former top-level types of a deleted file
///       re-parsed from the base blob, dangling reference candidates reported);
/// a0b — stable machine-matchable note ids: native {id, text} notes on review_pack, additive
///       noteId retrofits on the hot search_text / type_hierarchy notes (prose untouched).
/// Git-driven tests env-guarded on GitInfo.GitAvailable (Batch 5/25/41 pattern).
/// </summary>
public class Batch42Tests
{

    [Fact]
    public void ReviewPackDigestsUncommittedEditsAtMemberGranularity()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-42-dirt").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Edit ONE method body, uncommitted; let the index reflect it before packing.
            string widgetPath = Path.Combine(root, "Lib", "Widget.cs");
            File.WriteAllText(widgetPath, File.ReadAllText(widgetPath)
                .Replace("// body-marker", "System.Console.WriteLine(\"edited\");"));
            m.RequestRefresh(new[] { "Lib/Widget.cs" });
            // n7ly: the old RefreshAndWait keyed on the INVARIANT class name "Widget" — an
            // already-true condition gates nothing; wait for the EDIT to be visible instead.
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return (q.ContentByPath("Lib/Widget.cs") ?? "").Contains("edited");
            }, 60_000), "index did not reflect the Widget body edit");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            var symbols = pack.GetProperty("symbols").EnumerateArray().ToList();
            // Innermost policy: the edited METHOD is the reviewable unit, not its class.
            Assert.Contains(symbols, s => s.GetProperty("symbol").GetProperty("name").GetString() == "DoWork");
            Assert.DoesNotContain(symbols, s => s.GetProperty("symbol").GetProperty("name").GetString() == "Widget");

            var digest = symbols.First(s => s.GetProperty("symbol").GetProperty("name").GetString() == "DoWork");
            Assert.StartsWith("idx:", digest.GetProperty("symbol").GetProperty("symbolId").GetString());
            Assert.Equal("Lib", digest.GetProperty("owningProject").GetString());
            Assert.True(digest.GetProperty("directDependentProjects").GetProperty("total").GetInt32() >= 1);
            Assert.True(digest.GetProperty("transitiveDependentProjects").GetInt32() >= 1);
            Assert.True(digest.GetProperty("referenceCandidates").GetInt32() >= 1);
            Assert.True(digest.GetProperty("publicApi").GetBoolean());

            // The honesty note rides every pack, with its stable id.
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(),
                n => n.GetProperty("id").GetString() == "review.indexed_only");
            Assert.Equal("indexed", pack.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackRefusesLayeredIndexAndWorktreePayloads()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-layered").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string widget = Path.Combine(root, "Lib", "Widget.cs");
            string worktreeBytes = File.ReadAllText(widget);
            File.WriteAllText(widget, "namespace Lib { public class StagedOnlyPayload { } }\n");
            Git(root, "add Lib/Widget.cs");
            File.WriteAllText(widget, worktreeBytes);

            var result = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("error", out var pv) && pv.GetString() == "git_layered_changes", "review_pack with error == git_layered_changes (the DELIBERATE degrade)");

            Assert.Equal("git_layered_changes", result.GetProperty("error").GetString());
            string detail = result.GetProperty("detail").GetString()!;
            Assert.Contains("different staged and worktree payloads", detail);
            Assert.DoesNotContain("explicit 'paths'", detail, StringComparison.Ordinal);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackBaseRefReviewsABranchAndValidatesInput()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-42-base").FullName);
        try
        {
            WriteReviewRepo(root);
            // A branch with a COMMITTED new class — invisible to a dirt-only pack.
            Git(root, "checkout -q -b feature");
            File.WriteAllText(Path.Combine(root, "Lib", "Branchy.cs"),
                "namespace Lib { public class Branchy42 { public void Run() { } } }");
            Git(root, "add -A");
            Git(root, "commit -q -m feature-change");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            var dirtOnly = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            Assert.DoesNotContain(dirtOnly.GetProperty("symbols").EnumerateArray(),
                s => s.GetProperty("symbol").GetProperty("name").GetString() == "Branchy42");

            var branch = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(baseRef: "main"),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            // A wholly-new file surfaces the TYPE as the reviewable unit (it swallows its
            // members) — 'new public class Branchy42', not 'method Run touched'.
            Assert.Contains(branch.GetProperty("symbols").EnumerateArray(),
                s => s.GetProperty("symbol").GetProperty("name").GetString() == "Branchy42");
            Assert.DoesNotContain(branch.GetProperty("symbols").EnumerateArray(),
                s => s.GetProperty("symbol").GetProperty("name").GetString() == "Run");
            // The resolved base is echoed as a sha, never the raw ref name.
            Assert.Matches("^[0-9a-f]{7,64}$", branch.GetProperty("baseRef").GetString()!);

            // Validation: shell-unsafe charset AND safe-but-nonexistent both refuse cleanly.
            Assert.Equal("bad_request", Parse(tools.ReviewPack(baseRef: "no;pe")).GetProperty("error").GetString());
            Assert.Equal("bad_request", Parse(tools.ReviewPack(baseRef: "nope-branch")).GetProperty("error").GetString());

            string attackerRef = "UNBOUNDED_REF_" + new string('x', 32 * 1024);
            string boundedError = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(baseRef: attackerRef, maxBytes: 2048),
                j => j.TryGetProperty("error", out var pv) && pv.GetString() == "bad_request", "review_pack with error == bad_request (the DELIBERATE degrade)").GetRawText();
            Assert.Equal("bad_request", Parse(boundedError).GetProperty("error").GetString());
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(boundedError) <= 2048,
                $"invalid-base response exceeded maxBytes: {System.Text.Encoding.UTF8.GetByteCount(boundedError)}");
            Assert.DoesNotContain("UNBOUNDED_REF_", boundedError, StringComparison.Ordinal);
            JsonElement boundedMeta = Parse(boundedError).GetProperty("meta");
            Assert.Equal("indexed", boundedMeta.GetProperty("confidence").GetString());
            Assert.Equal("text", boundedMeta.GetProperty("navigationLayer").GetString());

            const string staleIndexedCommit = "0123456789abcdef0123456789abcdef01234567";
            var indexedCommitField = Assert.IsAssignableFrom<System.Reflection.FieldInfo>(
                typeof(IndexManager).GetField("_indexedCommit",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic));
            indexedCommitField.SetValue(m, staleIndexedCommit);

            string defaultError = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 2048),
                j => j.TryGetProperty("error", out var pv) && pv.GetString() == "git_index_baseline_unavailable", "review_pack with error == git_index_baseline_unavailable (the DELIBERATE degrade)").GetRawText();
            JsonElement defaultFailure = Parse(defaultError);
            Assert.Equal("git_index_baseline_unavailable",
                defaultFailure.GetProperty("error").GetString());
            string defaultDetail = defaultFailure.GetProperty("detail").GetString()!;
            Assert.Contains("refresh_index", defaultDetail, StringComparison.Ordinal);
            Assert.Contains("explicit baseRef", defaultDetail, StringComparison.Ordinal);
            Assert.DoesNotContain(staleIndexedCommit, defaultError, StringComparison.Ordinal);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(defaultError) <= 2048,
                $"default-base response exceeded maxBytes: {System.Text.Encoding.UTF8.GetByteCount(defaultError)}");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewGitFailureMappingPinsEveryCoreStatus()
    {
        (string Status, string Error, string DetailToken)[] cases =
        [
            ("ok", "git_diff_malformed", "file manifest"),
            ("invalid_commit", "bad_request", "valid commit"),
            ("config_failed", "git_config_failed", "configuration"),
            ("filter_unsafe", "git_filter_unsafe", "filters"),
            ("process_failed", "git_diff_failed", "process"),
            ("malformed", "git_diff_malformed", "deterministic diff format"),
            ("unmerged", "git_unmerged", "unmerged paths"),
            ("layered_changes", "git_layered_changes", "both byte layers"),
            ("snapshot_changed", "git_worktree_changed", "stable workspace"),
            ("status_failed", "git_status_failed", "bounded subprocess contract"),
        ];

        foreach (var testCase in cases)
        {
            var (error, detail) = NavigationTools.ReviewGitFailure(testCase.Status);
            Assert.Equal(testCase.Error, error);
            Assert.Contains(testCase.DetailToken, detail, StringComparison.Ordinal);
        }

        var (unknownError, unknownDetail) = NavigationTools.ReviewGitFailure("future_status");
        Assert.Equal("git_diff_failed", unknownError);
        Assert.Contains("future_status", unknownDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewNotReadyPreservesNormalProgressAndBoundsOversizedHealth()
    {
        var normalHealth = new IndexHealth("building", "11", "indexed", "refreshed", 0,
            null, 0, "C:/workspace", "index.db",
            Progress: new IndexProgress("scanning", 7, 20, 123));
        JsonElement normal = Parse(NavigationTools.BoundedReviewNotReady(normalHealth, 2048));
        Assert.Equal("index_building", normal.GetProperty("error").GetString());
        Assert.Equal("building", normal.GetProperty("state").GetString());
        JsonElement progress = normal.GetProperty("progress");
        Assert.Equal("scanning", progress.GetProperty("phase").GetString());
        Assert.Equal(7, progress.GetProperty("filesIndexed").GetInt32());
        Assert.Equal(20, progress.GetProperty("filesTotal").GetInt32());

        string oversized = "HEALTH_DETAIL_MUST_NOT_LEAK_" + new string('x', 32 * 1024);
        var oversizedHealth = new IndexHealth("building", oversized, oversized, oversized, 0,
            oversized, long.MaxValue, oversized, oversized,
            Progress: new IndexProgress(oversized, int.MaxValue, int.MaxValue, long.MaxValue));
        string boundedJson = NavigationTools.BoundedReviewNotReady(oversizedHealth, 2048);
        Assert.True(Json.Utf8Bytes(boundedJson) <= 2048,
            $"not-ready response exceeded maxBytes: {Json.Utf8Bytes(boundedJson)}");
        Assert.DoesNotContain("HEALTH_DETAIL_MUST_NOT_LEAK_", boundedJson,
            StringComparison.Ordinal);
        JsonElement bounded = Parse(boundedJson);
        Assert.Equal("index_building", bounded.GetProperty("error").GetString());
        Assert.Equal("building", bounded.GetProperty("state").GetString());
        Assert.False(bounded.TryGetProperty("progress", out _));
        Assert.Contains("omitted to satisfy maxBytes", bounded.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        JsonElement meta = bounded.GetProperty("meta");
        Assert.Equal("indexed", meta.GetProperty("confidence").GetString());
        Assert.Equal("text", meta.GetProperty("navigationLayer").GetString());
        Assert.Equal(BuildInfo.Stamp, meta.GetProperty("build").GetString());
        Assert.Equal(BuildInfo.IndexSchema, meta.GetProperty("indexSchema").GetString());
    }

    [Fact]
    public void ReviewPackReportsDeletedFormerTypesWithDanglingCandidates()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-42-del").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Delete a committed file whose type is still NAMED elsewhere (Consumer uses it).
            File.Delete(Path.Combine(root, "Lib", "Old.cs"));
            m.RequestRefresh(new[] { "Lib/Old.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.SearchSymbols("OldThing", "exact", null, 2).Count == 0;
            }, 20000), "index did not drop the deleted file");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            var deleted = pack.GetProperty("deletedFiles").EnumerateArray()
                .First(d => d.GetProperty("path").GetString() == "Lib/Old.cs");
            var former = deleted.GetProperty("formerTypes").EnumerateArray().First();
            Assert.Equal("OldThing", former.GetProperty("name").GetString());
            Assert.True(former.GetProperty("danglingCandidates").GetInt32() >= 1,
                "the consumer still names OldThing — the dangling candidates must say so");
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(),
                n => n.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackListsProjectFileChangesAndUntrackedFiles()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-42-proj").FullName);
        try
        {
            WriteReviewRepo(root);
            const string deletedProjectShape = "Lib/Shared.projitems";
            File.WriteAllText(Path.Combine(root, deletedProjectShape.Replace('/',
                Path.DirectorySeparatorChar)), "<Project />\n");
            Git(root, "add -A");
            Git(root, "commit -q -m project-shape-baseline");
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // An untracked new class + a modified csproj, both uncommitted.
            File.WriteAllText(Path.Combine(root, "Lib", "Fresh.cs"),
                "namespace Lib { public class Fresh42 { } }");
            string csproj = Path.Combine(root, "Lib", "Lib.csproj");
            File.WriteAllText(csproj, File.ReadAllText(csproj).Replace("</Project>",
                "  <!-- reviewed change -->\n</Project>"));
            File.Delete(Path.Combine(root, deletedProjectShape.Replace('/',
                Path.DirectorySeparatorChar)));
            RefreshAndWait(m, "Lib/Fresh.cs", "Fresh42");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            string?[] changedProjectFiles = pack.GetProperty("changedProjectFiles")
                .EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Contains("Lib/Lib.csproj", changedProjectFiles);
            Assert.Contains(deletedProjectShape, changedProjectFiles);
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(),
                n => n.GetProperty("id").GetString() == "review.project_files_changed");
            // Untracked file: whole-file symbol mapping.
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(),
                s => s.GetProperty("symbol").GetProperty("name").GetString() == "Fresh42");
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("untracked").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackReportsExcludedSubmoduleCoverageAndChangedGitlinks()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-submodule-root").FullName);
        string origin = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-submodule-origin").FullName);
        try
        {
            Git(origin, "init -q -b main");
            Git(origin, "config user.email test@example.com");
            Git(origin, "config user.name CodeNavTest");
            Git(origin, "config commit.gpgsign false");
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            Git(origin, "add -A");
            Git(origin, "commit -q -m initial");

            WriteReviewRepo(root);
            string submodulePath = "Sub" + new string('x', 80);
            Git(root,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" {submodulePath}");
            Git(root, "add -A");
            Git(root, "commit -q -m submodule");
            File.AppendAllText(Path.Combine(origin, "Child.cs"), "// next\n");
            Git(origin, "add -A");
            Git(origin, "commit -q -m next");
            string next = GitOutput(origin, "rev-parse HEAD").Trim();
            string child = Path.Combine(root, submodulePath);
            Git(child, "-c protocol.file.allow=always fetch -q origin");
            Git(child, $"checkout -q {next}");
            Git(root, $"add {submodulePath}");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            var full = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("coverage", out _), "review_pack with coverage");
            Assert.Equal(submodulePath, Assert.Single(full.GetProperty("coverage")
                .GetProperty("submoduleWorktrees").GetProperty("samplePaths")
                .EnumerateArray()).GetString());
            Assert.Equal(submodulePath, Assert.Single(full.GetProperty("changedSubmoduleLinks")
                .GetProperty("samplePaths").EnumerateArray()).GetString());

            string json = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 2048),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles").GetRawText();
            var pack = Parse(json);

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 2048);
            var coverage = pack.GetProperty("coverage").GetProperty("submoduleWorktrees");
            Assert.Equal("excluded", coverage.GetProperty("status").GetString());
            Assert.Equal(1, coverage.GetProperty("count").GetInt32());
            Assert.True(coverage.GetProperty("samplesTruncated").GetBoolean());
            Assert.False(coverage.TryGetProperty("samplePaths", out _));
            var links = pack.GetProperty("changedSubmoduleLinks");
            Assert.Equal(1, links.GetProperty("count").GetInt32());
            Assert.True(links.GetProperty("samplesTruncated").GetBoolean());
            Assert.False(links.TryGetProperty("samplePaths", out _));
            Assert.Equal(1, pack.GetProperty("changedFiles")
                .GetProperty("submoduleLinks").GetInt32());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(),
                note => note.GetProperty("id").GetString() ==
                        "review.submodule_worktrees_excluded");
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void ReviewPackDisclosesUntrackedNestedRepositoriesWithoutExecutingChildHelpers()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-nested-repo").FullName);
        try
        {
            WriteReviewRepo(root);
            File.WriteAllText(Path.Combine(root, "Lib", "FreshNestedCoverage.cs"),
                "namespace Lib { public class FreshNestedCoverage { } }\n");

            string nested = Path.Combine(root, "NestedRepo");
            Directory.CreateDirectory(nested);
            Git(nested, "init -q -b main");
            Git(nested, "config user.email test@example.com");
            Git(nested, "config user.name CodeNavTest");
            Git(nested, "config commit.gpgsign false");
            File.WriteAllText(Path.Combine(nested, ".gitattributes"),
                "Child.cs filter=nestedguard\n");
            string childPath = Path.Combine(nested, "Child.cs");
            File.WriteAllText(childPath,
                "namespace Nested { public class NestedChild { } }\n");
            Git(nested, "add -A");
            Git(nested, "commit -q -m initial");

            string marker = Path.Combine(nested, "nested-filter-ran");
            Git(nested, "config filter.nestedguard.required true");
            const string filterCommand = "echo invoked > nested-filter-ran && exit 1";
            Git(nested, $"config filter.nestedguard.clean \"{filterCommand}\"");
            Assert.Equal(filterCommand,
                GitOutput(nested, "config --get filter.nestedguard.clean").Trim());
            File.AppendAllText(childPath, "// child dirt\n");

            using var m = StartManager(root);
            Assert.False(File.Exists(marker),
                "workspace startup must not execute a nested repository's clean filter");
            var tools = new NavigationTools(m, new SemanticService(m));
            string fullJson = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles").GetRawText();
            var full = Parse(fullJson);

            Assert.False(File.Exists(marker),
                "review_pack must not execute an untracked nested repository's filter");
            Assert.Equal(1, full.GetProperty("changedFiles")
                .GetProperty("untracked").GetInt32());
            Assert.Contains(full.GetProperty("symbols").EnumerateArray(),
                symbol => symbol.GetProperty("symbol").GetProperty("name").GetString() ==
                          "FreshNestedCoverage");
            Assert.DoesNotContain(full.GetProperty("symbols").EnumerateArray(),
                symbol => symbol.GetProperty("symbol").GetProperty("name").GetString() ==
                          "NestedChild");
            var fullCoverage = full.GetProperty("coverage")
                .GetProperty("untrackedRepositories");
            Assert.Equal("excluded", fullCoverage.GetProperty("status").GetString());
            Assert.Equal(1, fullCoverage.GetProperty("count").GetInt32());
            Assert.Equal("NestedRepo", Assert.Single(fullCoverage.GetProperty("samplePaths")
                .EnumerateArray()).GetString()?.TrimEnd('/'));
            Assert.Contains(full.GetProperty("notes").EnumerateArray(),
                note => note.GetProperty("id").GetString() ==
                        "review.untracked_repositories_excluded");

            string boundedJson = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 2048),
                j => j.TryGetProperty("coverage", out _), "review_pack with coverage").GetRawText();
            var bounded = Parse(boundedJson);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(boundedJson) <= 2048);
            var boundedCoverage = bounded.GetProperty("coverage")
                .GetProperty("untrackedRepositories");
            Assert.Equal("excluded", boundedCoverage.GetProperty("status").GetString());
            Assert.Equal(1, boundedCoverage.GetProperty("count").GetInt32());
            Assert.True(boundedCoverage.GetProperty("samplesTruncated").GetBoolean());
            Assert.False(boundedCoverage.TryGetProperty("samplePaths", out _));
            Assert.Contains(bounded.GetProperty("notes").EnumerateArray(),
                note => note.GetProperty("id").GetString() ==
                        "review.untracked_repositories_excluded");
            Assert.False(File.Exists(marker),
                "bounded review_pack must not execute the nested repository filter either");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NoteIdsAreMachineMatchableAcrossTheRetrofits()
    {
        string root = Directory.CreateTempSubdirectory("codenav-42-notes").FullName;
        try
        {
            // search_text trio (no git needed): a spelling target, and an orphan-only
            // interface with a compiled base-list namer for the hierarchy fallback.
            string dir = Path.Combine(root, "Lab");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Lab.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "WidgetFactory.cs"),
                "namespace Lab { public class WidgetFactory { public void Build() { } } }");
            File.WriteAllText(Path.Combine(dir, "GhostImpl.cs"),
                "namespace Lab { public class GhostImpl : IGhost42 { } }");
            string orphanDir = Path.Combine(root, "Orphan");
            Directory.CreateDirectory(orphanDir);
            File.WriteAllText(Path.Combine(orphanDir, "IGhost42.cs"),
                "namespace GhostNs { public interface IGhost42 { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000));
            var tools = new NavigationTools(m, new SemanticService(m));

            Assert.Equal("search_text.did_you_mean",
                Parse(tools.SearchText("WdgetFactory")).GetProperty("noteId").GetString());
            Assert.Equal("search_text.absent_everywhere",
                Parse(tools.SearchText("Zqxjklmv")).GetProperty("noteId").GetString());
            Assert.Equal("search_text.elsewhere_matches",
                Parse(tools.SearchText("WidgetFactory", pathGlob: "nowhere/**")).GetProperty("noteId").GetString());
            // type_hierarchy result-null degrade (orphan-only target — deterministic).
            Assert.Equal("type_hierarchy.heuristic_fallback",
                Parse(tools.TypeHierarchy("IGhost42")).GetProperty("noteId").GetString());
        }
        finally { Cleanup(root); }
    }

    // Review F1 (reproduced): git appends a TAB terminator to diff-header paths CONTAINING
    // SPACES; the un-stripped tab made ShowFile reject the deleted path (deletion honesty
    // silently lost), and a modified space-path file became a ghost entry re-added at
    // whole-file granularity and miscounted as untracked.
    [Fact]
    public void SpacePathsSurviveTheDiffHeaderTabTerminator()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-42-space").FullName);
        try
        {
            WriteReviewRepo(root);
            // Committed files with spaces in the name — everyday monolith reality.
            File.WriteAllText(Path.Combine(root, "Lib", "Sp ace.cs"),
                """
                namespace Lib
                {
                    public class SpaceWidget42
                    {
                        public void Touched()
                        {
                            // sp-marker
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Lib", "Old Space.cs"),
                "namespace Lib { public class OldSpace42 { } }");
            File.WriteAllText(Path.Combine(root, "Consumer", "UseSpace.cs"),
                "namespace Consumer { public class UseSpace { public Lib.OldSpace42? S; } }");
            Git(root, "add -A");
            Git(root, "commit -q -m spaces");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Modified tracked space-path file: MEMBER granularity, not a ghost whole-file,
            // and never miscounted as untracked.
            string spPath = Path.Combine(root, "Lib", "Sp ace.cs");
            File.WriteAllText(spPath, File.ReadAllText(spPath)
                .Replace("// sp-marker", "System.Console.WriteLine(\"sp\");"));
            // Deleted space-path file whose type is still referenced: deletion honesty.
            File.Delete(Path.Combine(root, "Lib", "Old Space.cs"));
            m.RequestRefresh(new[] { "Lib/Sp ace.cs", "Lib/Old Space.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.SearchSymbols("OldSpace42", "exact", null, 2).Count == 0;
            }, 20000));

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            var symbols = pack.GetProperty("symbols").EnumerateArray().ToList();
            Assert.Contains(symbols, s => s.GetProperty("symbol").GetProperty("name").GetString() == "Touched");
            Assert.DoesNotContain(symbols, s => s.GetProperty("symbol").GetProperty("name").GetString() == "SpaceWidget42");
            Assert.False(pack.GetProperty("changedFiles").TryGetProperty("untracked", out _),
                "a tracked modification must not be miscounted as untracked (the tab-ghost symptom)");

            var deleted = pack.GetProperty("deletedFiles").EnumerateArray()
                .First(d => d.GetProperty("path").GetString() == "Lib/Old Space.cs");
            var former = deleted.GetProperty("formerTypes").EnumerateArray().First();
            Assert.Equal("OldSpace42", former.GetProperty("name").GetString());
            Assert.True(former.GetProperty("danglingCandidates").GetInt32() >= 1);
        }
        finally { Cleanup(root); }
    }

    // Review F2 (reproduced): 65+ hunks in one file blew SymbolsIntersecting's 64-range cap
    // and the tail hunks' symbols vanished with NO flag. The whole-file fallback now applies:
    // a 65+-hunk file is effectively a rewrite, digested honestly at TYPE level.
    [Fact]
    public void SixtyFivePlusHunksFallBackToWholeFileNotSilentLoss()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-42-hunks").FullName);
        try
        {
            WriteReviewRepo(root);
            // A class of 140 one-line methods, committed; then edit 68 of them with unchanged
            // lines between — 68 separate -U0 hunks.
            string bigPath = Path.Combine(root, "Lib", "Big.cs");
            var src = new System.Text.StringBuilder("namespace Lib\n{\n    public class Big42\n    {\n");
            for (int i = 0; i < 140; i++) src.Append($"        public void M{i:D3}() {{ }} // m{i:D3}\n");
            src.Append("    }\n}\n");
            File.WriteAllText(bigPath, src.ToString());
            Git(root, "add -A");
            Git(root, "commit -q -m big");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            string text = File.ReadAllText(bigPath);
            for (int i = 0; i < 136; i += 2) // every other method: 68 hunks
            {
                text = text.Replace($"// m{i:D3}", $"/* e{i:D3} */");
            }
            File.WriteAllText(bigPath, text);
            m.RequestRefresh(new[] { "Lib/Big.cs" });
            // n7ly: keyed on invariant "Big42" — gate nothing; wait for the edit markers.
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return (q.ContentByPath("Lib/Big.cs") ?? "").Contains("/* e000 */");
            }, 60_000), "index did not reflect the Big.cs comment edits");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxSymbols: 100),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            // The whole-file fallback makes the TYPE the digest (fully covered swallows) —
            // the change is visible, not silently half-dropped.
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(),
                s => s.GetProperty("symbol").GetProperty("name").GetString() == "Big42");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SixtyFivePlusSymbolLessHunksPreservePreciseUnmappedCoverage()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unmapped-hunks").FullName);
        try
        {
            WriteReviewRepo(root);
            string path = Path.Combine(root, "Lib", "ManyDirectives.cs");
            var original = new System.Text.StringBuilder();
            for (int i = 0; i < 136; i++)
            {
                original.Append($"// directive-marker-{i:D3}\n");
            }
            File.WriteAllText(path, original.ToString());
            Git(root, "add -A");
            Git(root, "commit -q -m many-file-level-hunks");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            string edited = File.ReadAllText(path);
            for (int i = 0; i < 136; i += 2)
            {
                edited = edited.Replace($"directive-marker-{i:D3}",
                    $"edited-directive-{i:D3}", StringComparison.Ordinal);
            }
            File.WriteAllText(path, edited);
            m.RequestRefresh(new[] { "Lib/ManyDirectives.cs" });
            // n7ly: RequestRefresh is ASYNC — packing before the pump lands races the index
            // under load (this wait was missing; the pack below then raced a mid-refresh index).
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return (q.ContentByPath("Lib/ManyDirectives.cs") ?? "").Contains("edited-directive-000");
            }, 60_000), "index did not reflect the directive edits");

            var pack = SemanticRetry.ParseWithRetry( // n7ly: ride out transient git/index degrades
                () => tools.ReviewPack(maxSymbols: 100, maxBytes: 24576),
                j => j.TryGetProperty("unmappedChanges", out _), "review_pack with unmappedChanges");
            var unmapped = pack.GetProperty("unmappedChanges");
            Assert.Equal(68, unmapped.GetProperty("total").GetInt32());
            Assert.Equal(68, unmapped.GetProperty("returned").GetInt32());
            Assert.False(unmapped.TryGetProperty("truncated", out _));
            Assert.All(unmapped.GetProperty("items").EnumerateArray(), item =>
            {
                Assert.Equal("Lib/ManyDirectives.cs", item.GetProperty("path").GetString());
                Assert.Equal("file_level", item.GetProperty("reason").GetString());
                Assert.Equal(item.GetProperty("start").GetInt32(),
                    item.GetProperty("end").GetInt32());
            });
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SameNameSignatureReplacementKeepsBodyCallButNotDeclarationAsDangling()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-signature-replacement").FullName);
        try
        {
            WriteReviewRepo(root);
            string path = Path.Combine(root, "Lib", "Replacement.cs");
            File.WriteAllText(path,
                """
                namespace Lib
                {
                    public class Replacement42
                    {
                        public void ReplaceMe(int value) { }
                    }
                }
                """);
            Git(root, "add -A");
            Git(root, "commit -q -m signature-replacement-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                """
                namespace Lib
                {
                    public class Replacement42
                    {
                        public void ReplaceMe(string value)
                        {
                            ReplaceMe(1);
                        }
                    }
                }
                """);
            m.RequestRefresh(new[] { "Lib/Replacement.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline("Lib/Replacement.cs").Any(symbol =>
                    symbol.Name == "ReplaceMe" &&
                    symbol.Signature.Contains("string", StringComparison.Ordinal));
            }, 20_000), "index did not reflect the replacement signature");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("formerSymbols", out _), "review_pack with formerSymbols");
            Assert.True(pack.TryGetProperty("formerSymbols", out var formerSymbols),
                pack.GetRawText());
            var formerFile = formerSymbols.EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == "Lib/Replacement.cs");
            var former = formerFile.GetProperty("formerSymbols").EnumerateArray()
                .Single(symbol => symbol.GetProperty("name").GetString() == "ReplaceMe");
            Assert.Equal(1, former.GetProperty("danglingCandidates").GetInt32());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.former_symbol_dangling");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OperatorSignatureReplacementDoesNotCountItsDeclarationAsDangling()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-operator-replacement").FullName);
        try
        {
            WriteReviewRepo(root);
            string path = Path.Combine(root, "Lib", "OperatorReplacement.cs");
            File.WriteAllText(path,
                """
                namespace Lib
                {
                    public class OperatorReplacement42
                    {
                        public static OperatorReplacement42 operator +(OperatorReplacement42 left, int right) => left;
                    }
                }
                """);
            Git(root, "add -A");
            Git(root, "commit -q -m operator-signature-replacement-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                """
                namespace Lib
                {
                    public class OperatorReplacement42
                    {
                        public static OperatorReplacement42 operator +(OperatorReplacement42 left, string right) => left;
                    }
                }
                """);
            m.RequestRefresh(new[] { "Lib/OperatorReplacement.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline("Lib/OperatorReplacement.cs").Any(symbol =>
                    symbol.Kind == "operator" &&
                    symbol.Signature.Contains("string", StringComparison.Ordinal));
            }, 20_000), "index did not reflect the replacement operator signature");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("formerSymbols", out _), "review_pack with formerSymbols");
            var formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() ==
                                "Lib/OperatorReplacement.cs");
            var former = formerFile.GetProperty("formerSymbols").EnumerateArray()
                .Single(symbol => symbol.GetProperty("name").GetString() == "operator +");
            Assert.Equal(0, former.GetProperty("danglingCandidates").GetInt32());
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.former_symbol_dangling");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void CheckedAndExplicitInterfaceOperatorsHaveDistinctDeclarationKeys()
    {
        ParsedCsFile checkedPair = SyntaxIndexer.Parse("CheckedOperators.cs",
            "public readonly struct Number42 { " +
            "public static Number42 operator +(Number42 left, Number42 right) => left; " +
            "public static Number42 operator checked +(Number42 left, Number42 right) => left; }");
        SymbolRow[] checkedOperators = checkedPair.Symbols
            .Where(symbol => symbol.Kind == "operator").ToArray();
        Assert.Equal(new[] { "operator +", "operator checked +" },
            checkedOperators.Select(symbol => symbol.Name));
        Assert.Equal(2, checkedOperators.Select(symbol => symbol.DeclarationKey)
            .Distinct(StringComparer.Ordinal).Count());

        ParsedCsFile explicitPair = SyntaxIndexer.Parse("ExplicitOperators.cs",
            "public interface IFoo42<T> { static abstract T operator +(T left, T right); } " +
            "public interface IBar42<T> { static abstract T operator +(T left, T right); } " +
            "public sealed class ExplicitNumber42 : IFoo42<ExplicitNumber42>, IBar42<ExplicitNumber42> { " +
            "static ExplicitNumber42 IFoo42<ExplicitNumber42>.operator +(ExplicitNumber42 left, ExplicitNumber42 right) => left; " +
            "static ExplicitNumber42 IBar42<ExplicitNumber42>.operator +(ExplicitNumber42 left, ExplicitNumber42 right) => left; }");
        SymbolRow[] implementations = explicitPair.Symbols
            .Where(symbol => symbol.Kind == "operator" &&
                             symbol.Container == "ExplicitNumber42").ToArray();
        Assert.Equal(2, implementations.Length);
        Assert.Equal(2, implementations.Select(symbol => symbol.DeclarationKey)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(implementations, symbol =>
            symbol.Signature.Contains("IFoo42<ExplicitNumber42>.operator +",
                StringComparison.Ordinal));
        Assert.Contains(implementations, symbol =>
            symbol.Signature.Contains("IBar42<ExplicitNumber42>.operator +",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RemovingCheckedOperatorKeepsUncheckedTwinAndReportsFormerSymbol()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-checked-operator").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/CheckedOperator.cs";
            string path = Path.Combine(root, relativePath.Replace('/',
                Path.DirectorySeparatorChar));
            File.WriteAllText(path,
                "namespace Lib;\n" +
                "public readonly struct CheckedNumber42\n" +
                "{\n" +
                "    public static CheckedNumber42 operator +(CheckedNumber42 left, CheckedNumber42 right) => left;\n" +
                "    public static CheckedNumber42 operator checked +(CheckedNumber42 left, CheckedNumber42 right) => left;\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m checked-operator-baseline");

            using var manager = StartManager(root);
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);
            File.WriteAllText(path,
                "namespace Lib;\n" +
                "public readonly struct CheckedNumber42\n" +
                "{\n" +
                "    public static CheckedNumber42 operator +(CheckedNumber42 left, CheckedNumber42 right) => left;\n" +
                "}\n");
            manager.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var queries = manager.OpenQueries();
                SymbolHit[] current = queries.Outline(relativePath)
                    .Where(symbol => symbol.Kind == "operator").ToArray();
                return current.Length == 1 && current[0].Name == "operator +";
            }, 20_000), "index did not remove the checked operator");

            JsonElement pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("formerSymbols", out _), "review_pack with formerSymbols");
            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == relativePath);
            JsonElement[] former = formerFile.GetProperty("formerSymbols")
                .EnumerateArray().ToArray();
            Assert.Contains(former, symbol =>
                symbol.GetProperty("name").GetString() == "operator checked +");
            Assert.DoesNotContain(former, symbol =>
                symbol.GetProperty("name").GetString() == "operator +");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NonNamespaceLinesInsideNamespaceScopesRemainFileLevelChanges()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-namespace-scope").FullName);
        try
        {
            WriteReviewRepo(root);
            string blockPath = Path.Combine(root, "Lib", "BlockScope.cs");
            File.WriteAllText(blockPath,
                "namespace BlockScope42\n{\n    // block-marker\n    public class BlockType42 { }\n}\n");
            string fileScopedPath = Path.Combine(root, "Lib", "FileScope.cs");
            File.WriteAllText(fileScopedPath,
                "namespace FileScope42;\n// file-marker\npublic class FileType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m namespace-scope-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(blockPath, File.ReadAllText(blockPath)
                .Replace("block-marker", "block-edited", StringComparison.Ordinal));
            File.WriteAllText(fileScopedPath, File.ReadAllText(fileScopedPath)
                .Replace("file-marker", "file-edited", StringComparison.Ordinal));
            m.RequestRefresh(new[] { "Lib/BlockScope.cs", "Lib/FileScope.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath("Lib/BlockScope.cs")?.Contains("block-edited",
                           StringComparison.Ordinal) == true &&
                       q.ContentByPath("Lib/FileScope.cs")?.Contains("file-edited",
                           StringComparison.Ordinal) == true;
            }, 20_000), "index did not reflect namespace-scope comment edits");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("unmappedChanges", out _), "review_pack with unmappedChanges");
            var items = pack.GetProperty("unmappedChanges").GetProperty("items")
                .EnumerateArray().ToList();
            foreach (string path in new[] { "Lib/BlockScope.cs", "Lib/FileScope.cs" })
            {
                JsonElement item = Assert.Single(items,
                    candidate => candidate.GetProperty("path").GetString() == path);
                Assert.Equal("file_level", item.GetProperty("reason").GetString());
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SameLineNamespaceCommentsRemainFileLevelChanges()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-inline-namespace").FullName);
        try
        {
            WriteReviewRepo(root);
            string blockPath = Path.Combine(root, "Lib", "InlineBlockScope.cs");
            File.WriteAllText(blockPath,
                "namespace InlineBlockScope42 { /* block-inline-marker */ }\n");
            string fileScopedPath = Path.Combine(root, "Lib", "InlineFileScope.cs");
            File.WriteAllText(fileScopedPath,
                "namespace InlineFileScope42; // file-inline-marker\n");
            Git(root, "add -A");
            Git(root, "commit -q -m inline-namespace-comment-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(blockPath, File.ReadAllText(blockPath)
                .Replace("block-inline-marker", "block-inline-edited",
                    StringComparison.Ordinal));
            File.WriteAllText(fileScopedPath, File.ReadAllText(fileScopedPath)
                .Replace("file-inline-marker", "file-inline-edited",
                    StringComparison.Ordinal));
            m.RequestRefresh(new[] { "Lib/InlineBlockScope.cs", "Lib/InlineFileScope.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath("Lib/InlineBlockScope.cs")?.Contains(
                           "block-inline-edited", StringComparison.Ordinal) == true &&
                       q.ContentByPath("Lib/InlineFileScope.cs")?.Contains(
                           "file-inline-edited", StringComparison.Ordinal) == true;
            }, 20_000), "index did not reflect same-line namespace comment edits");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("unmappedChanges", out _), "review_pack with unmappedChanges");
            var items = pack.GetProperty("unmappedChanges").GetProperty("items")
                .EnumerateArray().ToList();
            foreach (string path in new[]
                     {
                         "Lib/InlineBlockScope.cs", "Lib/InlineFileScope.cs",
                     })
            {
                JsonElement item = Assert.Single(items,
                    candidate => candidate.GetProperty("path").GetString() == path);
                Assert.Equal("file_level", item.GetProperty("reason").GetString());
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnmappedReplacementHunkExposesBothCoordinateSidesWithoutDoubleCounting()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unmapped-sides").FullName);
        try
        {
            WriteReviewRepo(root);
            string path = Path.Combine(root, "Lib", "OneDirective.cs");
            File.WriteAllText(path, "global using System.Text;\n");
            Git(root, "add -A");
            Git(root, "commit -q -m unmapped-sides-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path, "global using System.IO;\n");
            m.RequestRefresh(new[] { "Lib/OneDirective.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath("Lib/OneDirective.cs")?.Contains("System.IO",
                           StringComparison.Ordinal) == true;
            }, 20_000), "index did not reflect the directive replacement");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("unmappedChanges", out _), "review_pack with unmappedChanges");
            var unmapped = pack.GetProperty("unmappedChanges");
            Assert.Equal(1, unmapped.GetProperty("total").GetInt32());
            JsonElement item = Assert.Single(unmapped.GetProperty("items").EnumerateArray());
            Assert.Equal("Lib/OneDirective.cs", item.GetProperty("path").GetString());
            Assert.Equal("both", item.GetProperty("side").GetString());
            Assert.Contains(item.GetProperty("additionalReasons").EnumerateArray(), reason =>
                reason.GetString() == "file_level_old");
            JsonElement oldSide = item.GetProperty("old");
            Assert.Equal(1, oldSide.GetProperty("start").GetInt32());
            Assert.Equal(1, oldSide.GetProperty("count").GetInt32());
            JsonElement newSide = item.GetProperty("new");
            Assert.Equal(1, newSide.GetProperty("start").GetInt32());
            Assert.Equal(1, newSide.GetProperty("count").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MixedFileLevelAndSymbolHunkReportsBothCoverageKinds()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-mixed-hunk").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/MixedCoverage.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "global using System.Text;\n" +
                "public class MixedCoverage42 { public int Value => 1; }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m mixed-hunk-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                "global using System.IO;\n" +
                "public class MixedCoverage42 { public int Value => 2; }\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(relativePath)?.Contains("System.IO",
                           StringComparison.Ordinal) == true &&
                       q.Outline(relativePath).Any(symbol =>
                           symbol.Name == "MixedCoverage42");
            }, 20_000), "index did not reflect the mixed file-level and symbol hunk");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("symbols", out _), "review_pack with symbols");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() ==
                "MixedCoverage42");
            JsonElement unmapped = Assert.Single(pack.GetProperty("unmappedChanges")
                .GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("path").GetString() == relativePath);
            Assert.Equal("file_level", unmapped.GetProperty("reason").GetString());
            Assert.Equal("both", unmapped.GetProperty("side").GetString());
            Assert.Contains(unmapped.GetProperty("additionalReasons").EnumerateArray(), reason =>
                reason.GetString() == "file_level_old");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackToolDescriptionAdvertisesIndexedCommitDefault()
    {
        var method = Assert.IsAssignableFrom<System.Reflection.MethodInfo>(
            typeof(NavigationTools).GetMethod(nameof(NavigationTools.ReviewPack)));
        var description = Assert.IsType<System.ComponentModel.DescriptionAttribute>(
            Assert.Single(method.GetCustomAttributes(
                typeof(System.ComponentModel.DescriptionAttribute), inherit: false)));
        Assert.Contains("index's recorded commit", description.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("against HEAD", description.Description,
            StringComparison.OrdinalIgnoreCase);

        var baseRef = Assert.Single(method.GetParameters(), parameter =>
            parameter.Name == "baseRef");
        var parameterDescription = Assert.IsType<System.ComponentModel.DescriptionAttribute>(
            Assert.Single(baseRef.GetCustomAttributes(
                typeof(System.ComponentModel.DescriptionAttribute), inherit: false)));
        Assert.Contains("index's recorded commit", parameterDescription.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReviewPackExposesNamespaceAndFileLevelHunks()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unmapped").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            string widget = Path.Combine(root, "Lib", "Widget.cs");
            File.WriteAllText(widget, File.ReadAllText(widget)
                .Replace("namespace Lib", "namespace RenamedLib", StringComparison.Ordinal));
            string directives = Path.Combine(root, "Lib", "Directives.cs");
            File.WriteAllText(directives, "global using System.Text;\n");
            m.RequestRefresh(new[] { "Lib/Widget.cs", "Lib/Directives.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline("Lib/Widget.cs").Any(s => s.Ns == "RenamedLib");
            }, 20000));

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(),
                j => j.TryGetProperty("unmappedChanges", out _), "review_pack with unmappedChanges");
            var unmapped = pack.GetProperty("unmappedChanges");
            Assert.True(unmapped.GetProperty("total").GetInt32() >= 2);
            var items = unmapped.GetProperty("items").EnumerateArray().ToList();
            Assert.Contains(items, item =>
                item.GetProperty("path").GetString() == "Lib/Widget.cs" &&
                item.GetProperty("reason").GetString() == "namespace");
            Assert.Contains(items, item =>
                item.GetProperty("path").GetString() == "Lib/Directives.cs" &&
                item.GetProperty("reason").GetString() == "file_level");
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.unmapped_hunks");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackMakesChangedFileAndByteCapsObservable()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-budget-caps").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string csPaths = string.Join(',', Enumerable.Range(0, 201)
                .Select(i => $"Generated/F{i:D3}.cs"));

            var capped = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(paths: csPaths, maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            Assert.Equal(201, capped.GetProperty("changedFiles").GetProperty("cs").GetInt32());
            var csCoverage = capped.GetProperty("changedCsFilesCoverage");
            Assert.Equal(201, csCoverage.GetProperty("total").GetInt32());
            Assert.Equal(200, csCoverage.GetProperty("returned").GetInt32());
            Assert.True(csCoverage.GetProperty("truncated").GetBoolean());
            Assert.Contains(capped.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.changed_files_cap");

            string projectPaths = string.Join(',', Enumerable.Range(0, 80)
                .Select(i => $"Projects/{i:D3}-{new string('x', 120)}.csproj"));
            string boundedJson = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(paths: projectPaths, maxBytes: 2048),
                j => j.TryGetProperty("changedProjectFilesCoverage", out _), "review_pack with changedProjectFilesCoverage").GetRawText();
            var bounded = Parse(boundedJson);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(boundedJson) <= 2048);
            var projectCoverage = bounded.GetProperty("changedProjectFilesCoverage");
            Assert.Equal(80, projectCoverage.GetProperty("total").GetInt32());
            Assert.True(projectCoverage.GetProperty("returned").GetInt32() < 80);
            Assert.True(projectCoverage.GetProperty("truncated").GetBoolean());
            Assert.Contains(bounded.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.byte_budget");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackCanTrimOneOversizedSymbolToZero()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-one-symbol-budget").FullName);
        try
        {
            string dir = Path.Combine(root, "Huge");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Huge.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            string hugeName = "Huge" + new string('X', 3000);
            File.WriteAllText(Path.Combine(dir, "Huge.cs"), $"public class {hugeName} {{ }}\n");
            Git(root, "init -q -b main");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "add -A");
            Git(root, "commit -q -m initial");
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            string json = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(paths: "Huge/Huge.cs", maxBytes: 2048),
                j => j.TryGetProperty("symbolsCoverage", out _), "review_pack with symbolsCoverage").GetRawText();
            var pack = Parse(json);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 2048);
            var coverage = pack.GetProperty("symbolsCoverage");
            Assert.Equal(1, coverage.GetProperty("total").GetInt32());
            Assert.Equal(0, coverage.GetProperty("returned").GetInt32());
            Assert.True(coverage.GetProperty("truncated").GetBoolean());
            Assert.Empty(pack.GetProperty("symbols").EnumerateArray());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackReportsDeletedFileAndFormerTypeCaps()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-deleted-caps").FullName);
        try
        {
            WriteReviewRepo(root);
            string formerPath = Path.Combine(root, "AFormer.cs");
            File.WriteAllText(formerPath,
                string.Join('\n', Enumerable.Range(0, 6)
                    .Select(i => $"public class FormerCap{i} {{ }}")));
            var deletedPaths = new List<string> { "AFormer.cs" };
            for (int i = 0; i < 20; i++)
            {
                string name = $"Deleted{i:D2}.txt";
                File.WriteAllText(Path.Combine(root, name), $"deleted {i}\n");
                deletedPaths.Add(name);
            }
            Git(root, "add -A");
            Git(root, "commit -q -m deletion-cap-fixture");
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            foreach (string path in deletedPaths) File.Delete(Path.Combine(root, path));
            m.RequestRefresh(deletedPaths);
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline("AFormer.cs").Count == 0;
            }, 20_000));

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFilesCoverage", out _), "review_pack with deletedFilesCoverage");
            var deletedCoverage = pack.GetProperty("deletedFilesCoverage");
            Assert.Equal(21, deletedCoverage.GetProperty("total").GetInt32());
            Assert.Equal(20, deletedCoverage.GetProperty("returned").GetInt32());
            Assert.True(deletedCoverage.GetProperty("truncated").GetBoolean());
            var former = pack.GetProperty("deletedFiles").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == "AFormer.cs");
            Assert.Equal(6, former.GetProperty("formerTypesTotal").GetInt32());
            Assert.Equal(5, former.GetProperty("formerTypes").GetArrayLength());
            Assert.True(former.GetProperty("formerTypesTruncated").GetBoolean());
            var noteIds = pack.GetProperty("notes").EnumerateArray()
                .Select(note => note.GetProperty("id").GetString()).ToList();
            Assert.Contains("review.deleted_files_cap", noteIds);
            Assert.Contains("review.former_types_cap", noteIds);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnavailableDeletedBaseBlobOmitsUnknownFormerTypeTotals()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-deleted-unavailable").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/Unavailable.cs";
            string path = Path.Combine(root, "Lib", "Unavailable.cs");
            byte[] oversizedBinary = new byte[(8 * 1024 * 1024) + 1024];
            Array.Fill(oversizedBinary, (byte)'x');
            oversizedBinary[0] = 0;
            File.WriteAllBytes(path, oversizedBinary);
            Git(root, "add -A");
            Git(root, "commit -q -m oversized-deleted-blob-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(path);
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(relativePath) is null;
            }, 20_000), "index did not reflect the oversized file deletion");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("deletedFiles", out _), "review_pack with deletedFiles");
            var deleted = pack.GetProperty("deletedFiles").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == relativePath);
            Assert.Equal("unavailable", deleted.GetProperty("recoveryStatus").GetString());
            Assert.False(deleted.TryGetProperty("formerTypes", out _));
            Assert.False(deleted.TryGetProperty("formerTypesTotal", out _));
            Assert.False(deleted.TryGetProperty("formerTypesTruncated", out _));
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.base_blob_unavailable");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FinalBaseBlobTimeoutMarksTheRecoveryBudgetAsHit()
    {
        Assert.False(NavigationTools.BaseBlobTimeBudgetHitAfterAttempt(null, 4_996));
        Assert.True(NavigationTools.BaseBlobTimeBudgetHitAfterAttempt(null, 4_997));
        Assert.False(NavigationTools.BaseBlobTimeBudgetHitAfterAttempt("recovered", 5_000));
    }

    [Fact]
    public void ReviewPackBoundsCumulativeBaseBlobRecoveryAndResponseEnvelope()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-base-recovery-budget").FullName);
        try
        {
            WriteReviewRepo(root);
            const int fileChars = 500_000;
            var deletedPaths = new List<string>();
            for (int i = 0; i < 9; i++)
            {
                string relativePath = $"Lib/Recovery{i:D2}.cs";
                string prefix = $"public class Recovery{i:D2} {{ }}\n/*";
                string content = prefix + new string('x', fileChars - prefix.Length - 2) + "*/";
                Assert.Equal(fileChars, content.Length);
                File.WriteAllText(Path.Combine(root, relativePath), content);
                deletedPaths.Add(relativePath);
            }
            Git(root, "add -A");
            Git(root, "commit -q -m base-recovery-budget-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            foreach (string path in deletedPaths) File.Delete(Path.Combine(root, path));
            m.RequestRefresh(deletedPaths);
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return deletedPaths.All(path => q.ContentByPath(path) is null);
            }, 20_000), "index did not reflect the recovery-budget deletions");

            const int maxBytes = 4096;
            string json = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: maxBytes),
                j => j.TryGetProperty("baseBlobRecoveryCoverage", out _), "review_pack with baseBlobRecoveryCoverage").GetRawText();
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= maxBytes, json);
            var pack = Parse(json);
            Assert.False(pack.TryGetProperty("error", out JsonElement error),
                error.ValueKind == JsonValueKind.Undefined ? json : error.GetString());
            JsonElement coverage = pack.GetProperty("baseBlobRecoveryCoverage");
            Assert.Equal(9, coverage.GetProperty("attempted").GetInt32());
            Assert.Equal(8, coverage.GetProperty("recovered").GetInt32());
            Assert.Equal(1, coverage.GetProperty("unavailable").GetInt32());
            Assert.Equal(4_000_000, coverage.GetProperty("retainedChars").GetInt32());
            Assert.Equal(4 * 1024 * 1024, coverage.GetProperty("charLimit").GetInt32());
            Assert.Equal(512 * 1024,
                coverage.GetProperty("perFileByteLimit").GetInt32());
            Assert.True(coverage.GetProperty("budgetHit").GetBoolean());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.base_blob_budget");
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.base_blob_unavailable");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExactSymbolLessCSharpMoveIsNotAlsoReportedAsDeletion()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-symbol-less-move").FullName);
        try
        {
            WriteReviewRepo(root);
            const string oldPath = "Lib/OldDirectives.cs";
            const string newPath = "Lib/NewDirectives.cs";
            File.WriteAllText(Path.Combine(root, oldPath), "global using System.Text;\n");
            Git(root, "add -A");
            Git(root, "commit -q -m symbol-less-move-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.ContentByPath(newPath)?.Contains("System.Text",
                           StringComparison.Ordinal) == true;
            }, 20_000), "index did not reflect the symbol-less C# move");

            var pack = SemanticRetry.ParseWithRetry( // n7ly sweep: retries transient degrades
                () => tools.ReviewPack(maxBytes: 24576),
                j => j.TryGetProperty("changedFiles", out _), "review_pack with changedFiles");
            var moves = pack.GetProperty("movedFiles");
            JsonElement move = Assert.Single(moves.GetProperty("items").EnumerateArray());
            Assert.Equal(oldPath, move.GetProperty("from").GetString());
            Assert.Equal(newPath, move.GetProperty("to").GetString());
            Assert.Equal("exact_blob", move.GetProperty("match").GetString());
            Assert.Equal(0, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.False(pack.TryGetProperty("deletedFiles", out _));
            Assert.False(pack.TryGetProperty("deletedFilesCoverage", out _));
        }
        finally { Cleanup(root); }
    }
}
