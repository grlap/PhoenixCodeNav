using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

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
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

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
            RefreshAndWait(m, "Lib/Widget.cs", "Widget");

            var pack = Parse(tools.ReviewPack());
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

            var result = Parse(tools.ReviewPack());

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

            var dirtOnly = Parse(tools.ReviewPack());
            Assert.DoesNotContain(dirtOnly.GetProperty("symbols").EnumerateArray(),
                s => s.GetProperty("symbol").GetProperty("name").GetString() == "Branchy42");

            var branch = Parse(tools.ReviewPack(baseRef: "main"));
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
            string boundedError = tools.ReviewPack(baseRef: attackerRef, maxBytes: 2048);
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

            string defaultError = tools.ReviewPack(maxBytes: 2048);
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

            var pack = Parse(tools.ReviewPack());
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

            var pack = Parse(tools.ReviewPack());
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
            var full = Parse(tools.ReviewPack());
            Assert.Equal(submodulePath, Assert.Single(full.GetProperty("coverage")
                .GetProperty("submoduleWorktrees").GetProperty("samplePaths")
                .EnumerateArray()).GetString());
            Assert.Equal(submodulePath, Assert.Single(full.GetProperty("changedSubmoduleLinks")
                .GetProperty("samplePaths").EnumerateArray()).GetString());

            string json = tools.ReviewPack(maxBytes: 2048);
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
            string fullJson = tools.ReviewPack();
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

            string boundedJson = tools.ReviewPack(maxBytes: 2048);
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

            var pack = Parse(tools.ReviewPack());
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
            RefreshAndWait(m, "Lib/Big.cs", "Big42");

            var pack = Parse(tools.ReviewPack(maxSymbols: 100));
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

            var pack = Parse(tools.ReviewPack(maxSymbols: 100, maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack());
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

            var capped = Parse(tools.ReviewPack(paths: csPaths, maxBytes: 24576));
            Assert.Equal(201, capped.GetProperty("changedFiles").GetProperty("cs").GetInt32());
            var csCoverage = capped.GetProperty("changedCsFilesCoverage");
            Assert.Equal(201, csCoverage.GetProperty("total").GetInt32());
            Assert.Equal(200, csCoverage.GetProperty("returned").GetInt32());
            Assert.True(csCoverage.GetProperty("truncated").GetBoolean());
            Assert.Contains(capped.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.changed_files_cap");

            string projectPaths = string.Join(',', Enumerable.Range(0, 80)
                .Select(i => $"Projects/{i:D3}-{new string('x', 120)}.csproj"));
            string boundedJson = tools.ReviewPack(paths: projectPaths, maxBytes: 2048);
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

            string json = tools.ReviewPack(paths: "Huge/Huge.cs", maxBytes: 2048);
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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
            string json = tools.ReviewPack(maxBytes: maxBytes);
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

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
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

    [Fact]
    public void ExactCSharpMoveAcrossUnrelatedProjectsRetainsFormerDeletionCoverage()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-cross-project-move").FullName);
        try
        {
            WriteReviewRepo(root);
            string unrelated = Path.Combine(root, "Unrelated");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "Unrelated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string oldPath = "Lib/CrossProjectMove.cs";
            const string newPath = "Unrelated/CrossProjectMove.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace CrossProjectMoveNs { public class CrossProjectMove42 { public void Keep() { } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m cross-project-move-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "CrossProjectMove42");
            }, 20_000), "index did not reflect the cross-project exact move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement move = Assert.Single(pack.GetProperty("movedFiles")
                .GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("from").GetString() == oldPath);
            Assert.Equal(newPath, move.GetProperty("to").GetString());
            Assert.Equal("exact_blob", move.GetProperty("match").GetString());

            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "CrossProjectMove42");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.True(!formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) ||
                        dangling.ValueKind == JsonValueKind.Null,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                path => path.GetString() == newPath);

            JsonElement coverage = pack.GetProperty("deletedFilesCoverage");
            Assert.Equal(1, coverage.GetProperty("total").GetInt32());
            Assert.Equal(1, coverage.GetProperty("returned").GetInt32());
            Assert.False(coverage.TryGetProperty("truncated", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExactMoveLosingOneOfMultipleProjectDomainsRetainsDeletionEvidence()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-partial-project-move").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string projectA = Path.Combine(root, "ProjectA");
            string projectB = Path.Combine(root, "ProjectB");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(projectA);
            Directory.CreateDirectory(projectB);
            const string oldPath = "Shared/SharedDomain.cs";
            const string newPath = "ProjectA/SharedDomain.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace SharedDomainNs { public class SharedDomain42 { public void Keep() { } } }\n");
            File.WriteAllText(Path.Combine(projectA, "ProjectA.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"../Shared/SharedDomain.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(projectB, "ProjectB.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"../Shared/SharedDomain.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(projectB, "UseSharedDomain.cs"),
                "namespace ProjectB { public class UseSharedDomain { public SharedDomainNs.SharedDomain42? Value; } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m shared-project-domain-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            using (var baselineQueries = m.OpenQueries())
            {
                HashSet<string> baselineOwners = baselineQueries.ProjectsContaining(oldPath)
                    .Select(project => project.Name)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("ProjectA", baselineOwners);
                Assert.Contains("ProjectB", baselineOwners);
            }

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "SharedDomain42");
            }, 20_000), "index did not reflect the partial-project-domain exact move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement move = Assert.Single(pack.GetProperty("movedFiles")
                .GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("from").GetString() == oldPath);
            Assert.Equal(newPath, move.GetProperty("to").GetString());
            Assert.Equal("exact_blob", move.GetProperty("match").GetString());

            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "SharedDomain42");
            Assert.True(formerType.GetProperty("referenceCandidates").GetInt32() > 0,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("samplePaths").EnumerateArray(), path =>
                path.GetString() == "ProjectB/UseSharedDomain.cs");
            if (formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) &&
                dangling.ValueKind == JsonValueKind.Number)
            {
                Assert.NotEqual(0, dangling.GetInt32());
            }

            JsonElement coverage = pack.GetProperty("deletedFilesCoverage");
            Assert.Equal(1, coverage.GetProperty("total").GetInt32());
            Assert.Equal(1, coverage.GetProperty("returned").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RelocatedSurvivingTypeDoesNotProduceDeletedDanglingWarning()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-modified-move").FullName);
        try
        {
            WriteReviewRepo(root);
            const string oldPath = "Lib/OldRelocated.cs";
            const string newPath = "Lib/NewRelocated.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                """
                namespace Lib
                {
                    public class Relocated42
                    {
                        public int Value => 1;
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Consumer", "UseRelocated.cs"),
                "namespace Consumer { public class UseRelocated { public Lib.Relocated42? Value; } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m modified-move-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            File.WriteAllText(Path.Combine(root, newPath),
                File.ReadAllText(Path.Combine(root, newPath))
                    .Replace("Value => 1", "Value => 2", StringComparison.Ordinal));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "Relocated42");
            }, 20_000), "index did not reflect the modified C# relocation");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "Relocated42");
            Assert.True(formerType.TryGetProperty("danglingCandidates", out JsonElement dangling),
                formerType.GetRawText());
            Assert.Equal(0, dangling.GetInt32());
            Assert.Equal("project_candidate_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackMarksReferenceCandidateCountsAsLowerBoundsAtTheScanCap()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-ref-cap").FullName);
        try
        {
            string dir = Path.Combine(root, "App");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(dir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Target.cs"),
                "public class ReferenceCapTarget42 { }\n");
            for (int i = 0; i < 205; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"Use{i:D3}.cs"),
                    $"public class Use{i:D3} {{ ReferenceCapTarget42? Value; }}\n");
            }
            Git(root, "init -q -b main");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "add -A");
            Git(root, "commit -q -m initial");
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            var pack = Parse(tools.ReviewPack(paths: "App/Target.cs", maxBytes: 24576));
            var digest = Assert.Single(pack.GetProperty("symbols").EnumerateArray());
            Assert.True(digest.GetProperty("referenceCandidatesLowerBound").GetBoolean());
            Assert.True(digest.TryGetProperty("referenceCandidatesCoverage", out var coverage),
                digest.GetRawText());
            Assert.Equal(200, coverage.GetProperty("scanned").GetInt32());
            Assert.Equal(201, coverage.GetProperty("atLeast").GetInt32());
            Assert.Equal(200, coverage.GetProperty("limit").GetInt32());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.reference_candidates_cap");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ModifiedSameProjectRelocationReportsRemovedMemberWithoutCallingTypeDangling()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-relocated-member").FullName);
        try
        {
            WriteReviewRepo(root);
            const string oldPath = "Lib/OldRelocatedMember.cs";
            const string newPath = "Lib/NewRelocatedMember.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace Lib { public class RelocatedMember42 { public void Gone() { } public int Value => 1; } }\n");
            File.WriteAllText(Path.Combine(root, "Consumer", "UseRelocatedMember.cs"),
                "namespace Consumer { public class UseRelocatedMember { public void Run(Lib.RelocatedMember42 value) { value.Gone(); } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m relocated-member-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, oldPath));
            File.WriteAllText(Path.Combine(root, newPath),
                "namespace Lib { public class RelocatedMember42 { public int Value => 2; } }\n");
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "RelocatedMember42") &&
                       q.Outline(newPath).All(symbol => symbol.Name != "Gone");
            }, 20_000), "index did not reflect the member-removing relocation");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement survivingType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "RelocatedMember42");
            Assert.True(survivingType.TryGetProperty("danglingCandidates",
                out JsonElement typeDangling), survivingType.GetRawText());
            Assert.Equal(0, typeDangling.GetInt32());
            Assert.Equal("project_candidate_survivor",
                survivingType.GetProperty("danglingStatus").GetString());

            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == oldPath);
            JsonElement gone = formerFile.GetProperty("formerSymbols").EnumerateArray()
                .Single(symbol => symbol.GetProperty("name").GetString() == "Gone");
            Assert.True(gone.GetProperty("danglingCandidates").GetInt32() > 0,
                gone.GetRawText());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.former_symbol_dangling");
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.deleted_dangling");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void IdenticalFqnInUnrelatedProjectRemainsAdvisoryInsteadOfExactZero()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unrelated-survivor").FullName);
        try
        {
            WriteReviewRepo(root);
            string unrelated = Path.Combine(root, "Unrelated");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "Unrelated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(unrelated, "Shadow.cs"),
                "namespace Lib { public class OldThing { public void Legacy() { } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m unrelated-survivor-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            const string oldPath = "Lib/Old.cs";
            File.Delete(Path.Combine(root, oldPath));
            m.RequestRefresh(new[] { oldPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline("Unrelated/Shadow.cs").Any(symbol => symbol.Name == "OldThing");
            }, 20_000), "index did not reflect the unrelated surviving declaration");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "OldThing");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.True(formerType.GetProperty("referenceCandidates").GetInt32() > 0,
                formerType.GetRawText());
            Assert.True(!formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) ||
                        dangling.ValueKind == JsonValueKind.Null,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                path => path.GetString() == "Unrelated/Shadow.cs");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnrelatedSameNameDeclarationWithoutCallsIsNotADanglingReference()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unrelated-no-call").FullName);
        try
        {
            WriteReviewRepo(root);
            string unrelated = Path.Combine(root, "Unrelated");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "Unrelated.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string oldPath = "Lib/NoCallOld.cs";
            const string shadowPath = "Unrelated/NoCallShadow.cs";
            const string source =
                "namespace NoCallNs { public class NoCallShadow42 { } }\n";
            File.WriteAllText(Path.Combine(root, oldPath), source);
            File.WriteAllText(Path.Combine(root, shadowPath), source);
            Git(root, "add -A");
            Git(root, "commit -q -m unrelated-no-call-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, oldPath));
            m.RequestRefresh(new[] { oldPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(shadowPath).Any(symbol => symbol.Name == "NoCallShadow42");
            }, 20_000), "index did not reflect the unrelated no-call survivor");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "NoCallShadow42");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.Equal(0, formerType.GetProperty("referenceCandidates").GetInt32());
            Assert.Empty(formerType.GetProperty("samplePaths").EnumerateArray());
            Assert.True(!formerType.TryGetProperty("danglingCandidates", out JsonElement dangling) ||
                        dangling.ValueKind == JsonValueKind.Null,
                formerType.GetRawText());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GeneratedAndBaseListChangedSameProjectSurvivorsAreProjectDomainEvidence()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-project-domain-survivors").FullName);
        try
        {
            WriteReviewRepo(root);
            const string generatedOld = "Lib/GeneratedOld.cs";
            const string generatedCurrent = "Lib/GeneratedDomain.g.cs";
            const string baseOld = "Lib/BaseOld.cs";
            const string baseCurrent = "Lib/BaseCurrent.cs";
            File.WriteAllText(Path.Combine(root, generatedOld),
                "namespace Lib { public class GeneratedDomain42 { public int Value => 1; } }\n");
            File.WriteAllText(Path.Combine(root, baseOld),
                "namespace Lib { public class BaseChangedDomain42 : System.IDisposable { public void Dispose() { } } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m project-domain-survivor-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, generatedOld));
            File.Delete(Path.Combine(root, baseOld));
            File.WriteAllText(Path.Combine(root, generatedCurrent),
                "namespace Lib { public class GeneratedDomain42 { public int Value => 2; } }\n");
            File.WriteAllText(Path.Combine(root, baseCurrent),
                "namespace Lib { public class BaseChangedDomain42 : object { } }\n");
            m.RequestRefresh(new[] { generatedOld, generatedCurrent, baseOld, baseCurrent });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(generatedOld) is null &&
                       q.ContentByPath(baseOld) is null &&
                       q.Outline(generatedCurrent).Any(symbol =>
                           symbol.Name == "GeneratedDomain42" && symbol.FileIsGenerated) &&
                       q.Outline(baseCurrent).Any(symbol => symbol.Name == "BaseChangedDomain42");
            }, 20_000), "index did not reflect the same-project survivors");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            var deletedFiles = pack.GetProperty("deletedFiles").EnumerateArray().ToList();
            foreach ((string oldPath, string typeName, string currentPath) in new[]
                     {
                         (generatedOld, "GeneratedDomain42", generatedCurrent),
                         (baseOld, "BaseChangedDomain42", baseCurrent),
                     })
            {
                JsonElement deleted = deletedFiles.Single(file =>
                    file.GetProperty("path").GetString() == oldPath);
                JsonElement formerType = deleted.GetProperty("formerTypes").EnumerateArray()
                    .Single(symbol => symbol.GetProperty("name").GetString() == typeName);
                Assert.True(formerType.TryGetProperty("danglingCandidates",
                    out JsonElement dangling), formerType.GetRawText());
                Assert.Equal(0, dangling.GetInt32());
                Assert.Equal("project_candidate_survivor",
                    formerType.GetProperty("danglingStatus").GetString());
                Assert.Contains(
                    formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                    path => path.GetString() == currentPath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void PureOldSideDeletionReportsFileLevelGapAndNamespaceNameCoverage()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-old-side-deletions").FullName);
        try
        {
            WriteReviewRepo(root);
            const string mixedPath = "Lib/MixedOldSide.cs";
            const string namespacePath = "Lib/DeletedNamespaceName.cs";
            File.WriteAllText(Path.Combine(root, mixedPath),
                "global using System.Text;\n" +
                "public class RemovedOldSide42 { }\n" +
                "public class KeptOldSide42 { }\n");
            File.WriteAllText(Path.Combine(root, namespacePath),
                "namespace DeletedNamespace42;\n" +
                "public class KeptNamespaceBody42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m old-side-deletion-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(Path.Combine(root, mixedPath),
                "public class KeptOldSide42 { }\n");
            File.WriteAllText(Path.Combine(root, namespacePath),
                "public class KeptNamespaceBody42 { }\n");
            m.RequestRefresh(new[] { mixedPath, namespacePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline(mixedPath).Any(symbol => symbol.Name == "KeptOldSide42") &&
                       q.Outline(mixedPath).All(symbol => symbol.Name != "RemovedOldSide42") &&
                       q.Outline(namespacePath).Any(symbol =>
                           symbol.Name == "KeptNamespaceBody42" && symbol.Ns is null);
            }, 20_000), "index did not reflect the pure old-side deletions");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            var unmapped = pack.GetProperty("unmappedChanges").GetProperty("items")
                .EnumerateArray().ToList();
            JsonElement mixed = Assert.Single(unmapped, item =>
                item.GetProperty("path").GetString() == mixedPath);
            Assert.Equal("old", mixed.GetProperty("side").GetString());
            Assert.Equal("file_level_deleted", mixed.GetProperty("reason").GetString());
            Assert.Equal(1, mixed.GetProperty("old").GetProperty("start").GetInt32());
            Assert.Equal(2, mixed.GetProperty("old").GetProperty("count").GetInt32());
            Assert.False(mixed.TryGetProperty("new", out _));

            JsonElement namespaceOnly = Assert.Single(unmapped, item =>
                item.GetProperty("path").GetString() == namespacePath);
            Assert.Equal("old", namespaceOnly.GetProperty("side").GetString());
            Assert.Equal("namespace", namespaceOnly.GetProperty("reason").GetString());
            Assert.Equal(1,
                namespaceOnly.GetProperty("old").GetProperty("count").GetInt32());

            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == mixedPath);
            Assert.Contains(formerFile.GetProperty("formerSymbols").EnumerateArray(), symbol =>
                symbol.GetProperty("name").GetString() == "RemovedOldSide42");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MultilineNamespaceFinalNameLineWithMixedContentIsFileLevel()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-multiline-namespace").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/MultilineNamespace.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace Multiline42\n" +
                "    .Inner { /* final-name-marker */ }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m multiline-namespace-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path, File.ReadAllText(path).Replace(
                "final-name-marker", "final-name-edited", StringComparison.Ordinal));
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(relativePath)?.Contains("final-name-edited",
                           StringComparison.Ordinal) == true;
            }, 20_000), "index did not reflect the multiline namespace comment edit");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement item = Assert.Single(pack.GetProperty("unmappedChanges")
                .GetProperty("items").EnumerateArray(), candidate =>
                candidate.GetProperty("path").GetString() == relativePath);
            Assert.Equal("file_level", item.GetProperty("reason").GetString());
            Assert.Equal("both", item.GetProperty("side").GetString());
            Assert.Contains(item.GetProperty("additionalReasons").EnumerateArray(), reason =>
                reason.GetString() == "file_level_old");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RemovingMemberFromGenericSiblingDoesNotMatchNonGenericContainerMember()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-generic-container-identity").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/GenericContainerIdentity.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace GenericContainerIdentity42;\n" +
                "\n" +
                "public class C\n" +
                "{\n" +
                "    public void M() { }\n" +
                "}\n" +
                "\n" +
                "public class C<T>\n" +
                "{\n" +
                "    public void M() { }\n" +
                "    public void Keep() { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m generic-container-identity-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                "namespace GenericContainerIdentity42;\n" +
                "\n" +
                "public class C\n" +
                "{\n" +
                "    public void M() { }\n" +
                "}\n" +
                "\n" +
                "public class C<T>\n" +
                "{\n" +
                "    public void Keep() { }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> outline = q.Outline(relativePath);
                return outline.Count(symbol => symbol.Name == "M") == 1 &&
                       outline.Any(symbol => symbol.Name == "Keep");
            }, 20_000), "index did not reflect the generic-container member deletion");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == relativePath);
            JsonElement removed = Assert.Single(
                formerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "M");
            Assert.Equal("C", removed.GetProperty("container").GetString());
            Assert.Equal("void M()", removed.GetProperty("signature").GetString());
            Assert.Equal(10, removed.GetProperty("startLine").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RemovingOneExplicitInterfaceImplementationKeepsSignaturesDistinct()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-explicit-interface-identity").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/ExplicitInterfaceIdentity.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace ExplicitInterfaceIdentity42;\n" +
                "\n" +
                "public interface IFoo { void M(); }\n" +
                "public interface IBar { void M(); }\n" +
                "\n" +
                "public class Implementation : IFoo, IBar\n" +
                "{\n" +
                "    void IFoo.M() { }\n" +
                "    void IBar.M() { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m explicit-interface-identity-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                "namespace ExplicitInterfaceIdentity42;\n" +
                "\n" +
                "public interface IFoo { void M(); }\n" +
                "public interface IBar { void M(); }\n" +
                "\n" +
                "public class Implementation : IFoo, IBar\n" +
                "{\n" +
                "    void IBar.M() { }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> outline = q.Outline(relativePath);
                return outline.Any(symbol => symbol.Signature == "void IBar.M()") &&
                       outline.All(symbol => symbol.Signature != "void IFoo.M()");
            }, 20_000), "index did not reflect the explicit-interface member deletion");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement formerFile = pack.GetProperty("formerSymbols").EnumerateArray()
                .Single(file => file.GetProperty("path").GetString() == relativePath);
            JsonElement removed = Assert.Single(
                formerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "M");
            Assert.Equal("void IFoo.M()", removed.GetProperty("signature").GetString());
            Assert.DoesNotContain(formerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("signature").GetString() == "void IBar.M()");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NamespaceDeclarationTokenIsNotADeletedTypeDanglingReference()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-namespace-declaration-reference").FullName);
        try
        {
            WriteReviewRepo(root);
            const string oldPath = "Lib/NamespaceTokenVictim.cs";
            const string namespacePath = "Lib/NamespaceTokenOnly.cs";
            File.WriteAllText(Path.Combine(root, oldPath),
                "namespace Lib { public class NamespaceTokenVictim42 { } }\n");
            File.WriteAllText(Path.Combine(root, namespacePath),
                "namespace NamespaceTokenVictim42 { public class Marker42 { } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m namespace-declaration-reference-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.Delete(Path.Combine(root, oldPath));
            m.RequestRefresh(new[] { oldPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(namespacePath).Any(symbol => symbol.Name == "Marker42");
            }, 20_000), "index did not reflect the namespace-token victim deletion");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "NamespaceTokenVictim42");
            Assert.Equal(0, formerType.GetProperty("referenceCandidates").GetInt32());
            Assert.Equal(0, formerType.GetProperty("danglingCandidates").GetInt32());
            Assert.Empty(formerType.GetProperty("samplePaths").EnumerateArray());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExactMoveWithinDefaultOwnerRetainsEvidenceForLostLinkedProjectDomains()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-default-and-linked-owners").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string projectA = Path.Combine(root, "A");
            string projectB = Path.Combine(root, "B");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(projectA);
            Directory.CreateDirectory(projectB);
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/MovedType.cs";
            const string typeName = "LinkedOwnerDomain42";
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, oldPath),
                $"namespace LinkedOwnerDomainNs {{ public class {typeName} {{ }} }}\n");
            foreach ((string directory, string projectName) in new[]
                     {
                         (projectA, "A"),
                         (projectB, "B"),
                     })
            {
                File.WriteAllText(Path.Combine(directory, $"{projectName}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
                    "<ItemGroup><Compile Include=\"../Shared/Type.cs\" Link=\"Linked/Type.cs\" /></ItemGroup></Project>");
                File.WriteAllText(Path.Combine(directory, $"Use{projectName}.cs"),
                    $"namespace {projectName} {{ public class Use{projectName} {{ public LinkedOwnerDomainNs.{typeName}? Value; }} }}\n");
            }
            Git(root, "add -A");
            Git(root, "commit -q -m default-and-linked-owner-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            using (var baselineQueries = m.OpenQueries())
            {
                HashSet<string> owners = baselineQueries.ProjectsContaining(oldPath)
                    .Select(project => project.Name)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("Shared", owners);
                Assert.Contains("A", owners);
                Assert.Contains("B", owners);
            }

            File.Move(Path.Combine(root, oldPath), Path.Combine(root, newPath));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == typeName);
            }, 20_000), "index did not reflect the multi-owner exact move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement move = Assert.Single(pack.GetProperty("movedFiles")
                .GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("from").GetString() == oldPath);
            Assert.Equal(newPath, move.GetProperty("to").GetString());
            Assert.Equal("exact_blob", move.GetProperty("match").GetString());

            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles").EnumerateArray(),
                file => file.GetProperty("path").GetString() == oldPath);
            JsonElement formerType = Assert.Single(
                deleted.GetProperty("formerTypes").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == typeName);
            Assert.True(formerType.GetProperty("referenceCandidates").GetInt32() > 0,
                formerType.GetRawText());
            Assert.Contains(formerType.GetProperty("samplePaths").EnumerateArray(), path =>
                path.GetString() is "A/UseA.cs" or "B/UseB.cs");
            Assert.Contains(formerType.GetProperty("survivingDeclarationPaths").EnumerateArray(),
                path => path.GetString() == newPath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void TupleElementNamesAreOmittedFromDeclarationKeysButTypesAndNestingRemain()
    {
        static string Key(string parameter)
        {
            ParsedCsFile parsed = SyntaxIndexer.Parse("TupleDeclarationKey.cs",
                $"public class TupleIdentity42 {{ public void M({parameter}) {{ }} }}");
            SymbolRow method = Assert.Single(parsed.Symbols,
                symbol => symbol.Kind == "method" && symbol.Name == "M");
            return Assert.IsType<string>(method.DeclarationKey);
        }

        string flat = Key("(int left, string right) value");
        Assert.Equal(flat, Key("(int x, string y) renamed"));
        Assert.Equal(
            Key("System.Collections.Generic.List<(int left, string right)> value"),
            Key("System.Collections.Generic.List<(int x, string y)> renamed"));

        string nested = Key("(int code, (string text, bool valid) metadata) value");
        Assert.Equal(nested,
            Key("(int number, (string label, bool ok) details) renamed"));
        Assert.NotEqual(flat, Key("(long left, string right) value"));
        Assert.NotEqual(flat, nested);
        Assert.NotEqual(flat, Key("ref (int left, string right) value"));
    }

    [Fact]
    public void TupleElementRenamePreservesReviewPackDeclarationIdentity()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-tuple-declaration-key").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/TupleDeclarationKey.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class TupleIdentity42\n" +
                "{\n" +
                "    public void Transform((int code, (string text, bool valid) metadata) value)\n" +
                "    {\n" +
                "        _ = value.code;\n" +
                "    }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m tuple-declaration-key-fixture");

            using var manager = StartManager(root);
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class TupleIdentity42\n" +
                "{\n" +
                "    public void Transform((int number, (string label, bool ok) details) renamed)\n" +
                "    {\n" +
                "        _ = renamed.number;\n" +
                "    }\n" +
                "}\n");
            manager.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var queries = manager.OpenQueries();
                return queries.Outline(relativePath).Any(symbol =>
                    symbol.Name == "Transform" &&
                    symbol.Signature.Contains("number", StringComparison.Ordinal) &&
                    symbol.Signature.Contains("label", StringComparison.Ordinal));
            }, 20_000), "index did not reflect the tuple element-name changes");

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "Transform");
            if (pack.TryGetProperty("formerSymbols", out JsonElement formerFiles))
            {
                Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == relativePath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MethodParameterRenameAndReturnTypeChangePreserveDeclarationIdentity()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-method-declaration-key").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/MethodDeclarationKey.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class MethodIdentity42\n" +
                "{\n" +
                "    public int Transform(int value)\n" +
                "    {\n" +
                "        return value;\n" +
                "    }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m method-declaration-key-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(path,
                "namespace DeclarationKey42;\n" +
                "public class MethodIdentity42\n" +
                "{\n" +
                "    public long Transform(int renamed)\n" +
                "    {\n" +
                "        return renamed;\n" +
                "    }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline(relativePath).Any(symbol =>
                    symbol.Name == "Transform" &&
                    symbol.Signature == "long Transform(int renamed)");
            }, 20_000), "index did not reflect the method display-signature change");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "Transform");
            if (pack.TryGetProperty("formerSymbols", out JsonElement formerFiles))
            {
                Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == relativePath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BaseListChangeIsStableButOverloadParameterTypeReplacementIsFormer()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-type-and-overload-declaration-key").FullName);
        try
        {
            WriteReviewRepo(root);
            const string baseListPath = "Lib/BaseListDeclarationKey.cs";
            const string overloadPath = "Lib/OverloadDeclarationKey.cs";
            string baseListFullPath = Path.Combine(root, baseListPath);
            string overloadFullPath = Path.Combine(root, overloadPath);
            File.WriteAllText(baseListFullPath,
                "namespace DeclarationKey42;\n" +
                "public class BaseListIdentity42 : System.IDisposable\n" +
                "{\n" +
                "    public void Dispose() { }\n" +
                "}\n");
            File.WriteAllText(overloadFullPath,
                "namespace DeclarationKey42;\n" +
                "public class OverloadIdentity42\n" +
                "{\n" +
                "    public void M(int value) { }\n" +
                "    public void M(string value) { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m type-and-overload-declaration-key-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(baseListFullPath,
                "namespace DeclarationKey42;\n" +
                "public class BaseListIdentity42 : object\n" +
                "{\n" +
                "    public void Dispose() { }\n" +
                "}\n");
            File.WriteAllText(overloadFullPath,
                "namespace DeclarationKey42;\n" +
                "public class OverloadIdentity42\n" +
                "{\n" +
                "    public void M(long value) { }\n" +
                "    public void M(string value) { }\n" +
                "}\n");
            m.RequestRefresh(new[] { baseListPath, overloadPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> overloads = q.Outline(overloadPath)
                    .Where(symbol => symbol.Name == "M").ToList();
                return q.Outline(baseListPath).Any(symbol =>
                           symbol.Name == "BaseListIdentity42" &&
                           symbol.Signature.Contains(": object", StringComparison.Ordinal)) &&
                       overloads.Any(symbol => symbol.Signature == "void M(long value)") &&
                       overloads.Any(symbol => symbol.Signature == "void M(string value)") &&
                       overloads.All(symbol => symbol.Signature != "void M(int value)");
            }, 20_000), "index did not reflect the declaration-key counterexamples");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() ==
                "BaseListIdentity42");
            JsonElement formerFiles = pack.GetProperty("formerSymbols");
            Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == baseListPath);
            JsonElement overloadFormerFile = Assert.Single(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == overloadPath);
            JsonElement replacedOverload = Assert.Single(
                overloadFormerFile.GetProperty("formerSymbols").EnumerateArray(),
                symbol => symbol.GetProperty("name").GetString() == "M");
            Assert.Equal("void M(int value)",
                replacedOverload.GetProperty("signature").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReferenceDeclarationExclusionPerFileBudgetReportsExactCoverage()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-budget").FullName);
        try
        {
            string dir = Path.Combine(root, "App");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(dir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const int perFileLimit = 512 * 1024;
            const string prefix = "public class DeclarationBudgetProbe42 { }\n/*";
            string content = prefix +
                             new string('x', perFileLimit + 1 - prefix.Length - 2) +
                             "*/";
            Assert.Equal(perFileLimit + 1, content.Length);
            File.WriteAllText(Path.Combine(dir, "Oversized.cs"), content);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);
            IndexQueries.ReferenceCandidateResult result = q.ReferenceCandidates(
                "DeclarationBudgetProbe42", maxCandidateFiles: 10, samplesPerProject: 1,
                excludeDeclarations: true);

            Assert.False(result.CandidateFilesTruncated);
            Assert.True(result.DeclarationExclusionBudgetHit);
            Assert.True(result.CandidateFilesScanned < result.CandidateFilesAtLeast);
            Assert.Equal(0, result.CandidateFilesScanned);
            Assert.Equal(1, result.CandidateFilesAtLeast);
            Assert.Equal(10, result.CandidateFileLimit);
            Assert.Equal(0, result.DeclarationFilesParsed);
            Assert.Equal(128, result.DeclarationFileParseLimit);
            Assert.Equal(0, result.DeclarationCharsParsed);
            Assert.Equal(4 * 1024 * 1024, result.DeclarationCharLimit);
            Assert.Equal(perFileLimit, result.DeclarationPerFileCharLimit);
            Assert.Equal(0, result.TotalHits);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDeclarationBudgetDoesNotMasqueradeAsTheCandidateFileCap()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-note-cause").FullName);
        try
        {
            string dir = Path.Combine(root, "App");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(dir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string path = "App/Former.cs";
            File.WriteAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)),
                "public class DeclarationNoteCause42 { }\n");

            const int perFileLimit = 512 * 1024;
            const string prefix = "public class DeclarationNoteCause42 { }\n/*";
            string oversized = prefix +
                               new string('x', perFileLimit + 1 - prefix.Length - 2) +
                               "*/";
            Assert.Equal(perFileLimit + 1, oversized.Length);
            File.WriteAllText(Path.Combine(dir, "OversizedCandidate.cs"), oversized);
            Git(root, "init -q -b main");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "add -A");
            Git(root, "commit -q -m declaration-note-cause");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.WriteAllText(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)),
                "public class RenamedDeclarationNoteCause42 { }\n");
            RefreshAndWait(m, path, "RenamedDeclarationNoteCause42");

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            List<JsonElement> notes = pack.GetProperty("notes").EnumerateArray().ToList();
            Assert.Contains(notes, note => note.GetProperty("id").GetString() ==
                                           "review.reference_declaration_budget");
            Assert.DoesNotContain(notes, note => note.GetProperty("id").GetString() ==
                                                 "review.reference_candidates_cap");
            JsonElement formerFile = Assert.Single(pack.GetProperty("formerSymbols")
                .EnumerateArray(), file => file.GetProperty("path").GetString() == path);
            JsonElement former = Assert.Single(formerFile.GetProperty("formerSymbols")
                .EnumerateArray(), symbol => symbol.GetProperty("name").GetString() ==
                                             "DeclarationNoteCause42");
            Assert.True(former.GetProperty("referenceCandidatesLowerBound").GetBoolean());
            Assert.True(former.GetProperty("referenceCandidatesCoverage")
                .GetProperty("declarationExclusionBudgetHit").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewSurvivorFilterUsesCapturedGitPathsWithLiteralBackslashesOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-survivor-backslash").FullName);
        try
        {
            string directory = Path.Combine(root, "Survivors");
            Directory.CreateDirectory(directory);
            const string gitPath = "Survivors/Literal\\Twin.cs";
            File.WriteAllText(Path.Combine(directory, "Literal\\Twin.cs"),
                "public class LiteralBackslashSurvivor42 { }\n");

            var literal = new SymbolHit(1, "class", "LiteralBackslashSurvivor42", null,
                null, "class LiteralBackslashSurvivor42", "public", 1, 1, false, null,
                gitPath, false, null);
            var excluded = literal with { Id = 2, FilePath = "Deleted.cs" };
            var missing = literal with { Id = 3, FilePath = "Survivors/Missing.cs" };
            List<SymbolHit> survivors = NavigationTools.FilterExistingReviewDeclarations(
                "Deleted.cs", ["Deleted.cs"],
                [literal, excluded, missing], Exists);

            Assert.Equal(literal, Assert.Single(survivors));

            bool Exists(string path) =>
                CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root, path,
                    out string fullPath) &&
                !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(root, fullPath) &&
                File.Exists(fullPath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewSurvivorFilterRejectsMissingPathOutsideDeletionManifest()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-stale-survivor").FullName);
        try
        {
            string directory = Path.Combine(root, "Survivors");
            Directory.CreateDirectory(directory);
            const string livePath = "Survivors/Live.cs";
            File.WriteAllText(Path.Combine(directory, "Live.cs"),
                "public class LiveSurvivor42 { }\n");

            var live = new SymbolHit(1, "class", "LiveSurvivor42", null,
                null, "class LiveSurvivor42", "public", 1, 1, false, null,
                livePath, false, null);
            var stale = live with { Id = 2, FilePath = "Survivors/Stale.cs" };
            List<SymbolHit> survivors = NavigationTools.FilterExistingReviewDeclarations(
                "Deleted.cs", ["Deleted.cs"], [live, stale], Exists);

            Assert.Equal(live, Assert.Single(survivors));

            bool Exists(string path) =>
                CodeNav.Core.WorkspacePaths.TryResolveGitPathInside(root, path,
                    out string fullPath) &&
                !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(root, fullPath) &&
                File.Exists(fullPath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReferenceDeclarationOffsetCacheInvalidatesWhenIndexedContentChanges()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-offset-cache").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/DeclarationOffsetCache.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "namespace DeclarationOffsetCache42;\n" +
                "public class CacheProbe42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m declaration-offset-cache-fixture");

            using var m = StartManager(root);
            using IndexQueries q = m.OpenQueries();

            IndexQueries.ReferenceCandidateResult primed = q.ReferenceCandidates(
                "CacheProbe42", 20, 3, excludeDeclarations: true);
            Assert.Equal(0, primed.TotalHits);

            // Shift the declaration identifier to a different absolute offset and add one real
            // use. Reusing q is decisive: stale declaration offsets would count both the moved
            // declaration and the constructor call.
            File.WriteAllText(path,
                "namespace DeclarationOffsetCache42;\n" +
                "\n" +
                "public class PaddingBeforeCacheProbe42\n" +
                "{\n" +
                "    public int Value => 42;\n" +
                "}\n" +
                "\n" +
                "public class CacheProbe42 { }\n" +
                "\n" +
                "public class CacheProbeConsumer42\n" +
                "{\n" +
                "    public object Make() => new CacheProbe42();\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
                q.ContentByPath(relativePath)?.Contains("new CacheProbe42()",
                    StringComparison.Ordinal) == true, 20_000),
                "the reused query connection did not observe the refreshed indexed content");

            IndexQueries.ReferenceCandidateResult refreshed = q.ReferenceCandidates(
                "CacheProbe42", 20, 3, excludeDeclarations: true);
            Assert.Equal(1, refreshed.TotalHits);
            Assert.Equal(1, refreshed.ProdHits);
            Assert.Equal(0, refreshed.TestHits);
            List<TextHit> samples = refreshed.Groups.SelectMany(group => group.Samples).ToList();
            Assert.Contains(samples, hit =>
                hit.FilePath == relativePath &&
                hit.LineText.Contains("new CacheProbe42()", StringComparison.Ordinal));
            Assert.DoesNotContain(samples, hit =>
                hit.LineText.Contains("class CacheProbe42", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DeclarationKeyCanonicalizesGenericTypeAndExplicitInterfaceTrivia()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-declaration-key-trivia").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/DeclarationKeyTrivia.cs";
            string path = Path.Combine(root, relativePath);
            File.WriteAllText(path,
                "using System.Collections.Generic;\n" +
                "namespace DeclarationKeyTrivia42;\n" +
                "public interface IFoo<T>\n" +
                "{\n" +
                "    void M(Dictionary<string, int> value);\n" +
                "}\n" +
                "public class Implementation : IFoo<int>\n" +
                "{\n" +
                "    void IFoo < int > . M(Dictionary < string, int > value) { }\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m declaration-key-trivia-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // The spelling stays syntax-equivalent and only trivia around generic punctuation
            // changes. Alias-versus-qualified equivalence is deliberately not asserted: that
            // would require semantic binding and is outside the persisted syntax-key contract.
            File.WriteAllText(path,
                "using System.Collections.Generic;\n" +
                "namespace DeclarationKeyTrivia42;\n" +
                "public interface IFoo<T>\n" +
                "{\n" +
                "    void M(Dictionary<string, int> value);\n" +
                "}\n" +
                "public class Implementation : IFoo<int>\n" +
                "{\n" +
                "    void IFoo<int>.M(Dictionary<string,int> value) { }\n" +
                "}\n");
            m.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.Outline(relativePath).Any(symbol =>
                    symbol.Name == "M" &&
                    symbol.Signature.Contains("IFoo<int>.M", StringComparison.Ordinal));
            }, 20_000), "index did not reflect the canonical generic trivia");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("signature").GetString()?
                    .Contains("IFoo<int>.M", StringComparison.Ordinal) == true);
            if (pack.TryGetProperty("formerSymbols", out JsonElement formerFiles))
            {
                Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == relativePath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GenericParameterRenamesAreStableWhileArityAndConcreteTypesRemainDistinct()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-generic-parameter-identity").FullName);
        try
        {
            WriteReviewRepo(root);
            const string renamePath = "Lib/GenericParameterRename.cs";
            const string distinctPath = "Lib/GenericIdentityChanges.cs";
            string renameFullPath = Path.Combine(root, renamePath);
            string distinctFullPath = Path.Combine(root, distinctPath);
            File.WriteAllText(renameFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public static class N\n" +
                "{\n" +
                "    public sealed class T { }\n" +
                "    public sealed class U { }\n" +
                "}\n" +
                "public class C<T>\n" +
                "{\n" +
                "    public T FromContainer(T x) => x;\n" +
                "    public void Qualified(N.T concrete, T generic) { }\n" +
                "}\n" +
                "public class MethodContainer\n" +
                "{\n" +
                "    public T M<T>(T x) => x;\n" +
                "}\n");
            File.WriteAllText(distinctFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public class GenuineChanges\n" +
                "{\n" +
                "    public void ByType(int x) { }\n" +
                "    public void ByQualifiedType(N.T x) { }\n" +
                "    public T ByArity<T>(T x) => x;\n" +
                "}\n");
            Git(root, "add -A");
            Git(root, "commit -q -m generic-parameter-identity-fixture");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            File.WriteAllText(renameFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public static class N\n" +
                "{\n" +
                "    public sealed class T { }\n" +
                "    public sealed class U { }\n" +
                "}\n" +
                "public class C<U>\n" +
                "{\n" +
                "    public U FromContainer(U x) => x;\n" +
                "    public void Qualified(N.T concrete, U generic) { }\n" +
                "}\n" +
                "public class MethodContainer\n" +
                "{\n" +
                "    public U M<U>(U x) => x;\n" +
                "}\n");
            File.WriteAllText(distinctFullPath,
                "namespace GenericParameterIdentity42;\n" +
                "public class GenuineChanges\n" +
                "{\n" +
                "    public void ByType(long x) { }\n" +
                "    public void ByQualifiedType(N.U x) { }\n" +
                "    public T ByArity<T, U>(T x) => x;\n" +
                "}\n");
            m.RequestRefresh(new[] { renamePath, distinctPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                List<SymbolHit> renamed = q.Outline(renamePath);
                List<SymbolHit> distinct = q.Outline(distinctPath);
                return renamed.Any(symbol => symbol.Signature == "class C<U>") &&
                       renamed.Any(symbol =>
                           symbol.Signature == "U FromContainer(U x)") &&
                       renamed.Any(symbol =>
                           symbol.Signature == "void Qualified(N.T concrete, U generic)") &&
                       renamed.Any(symbol => symbol.Signature == "U M<U>(U x)") &&
                       distinct.Any(symbol => symbol.Signature == "void ByType(long x)") &&
                       distinct.Any(symbol =>
                           symbol.Signature == "void ByQualifiedType(N.U x)") &&
                       distinct.Any(symbol => symbol.Signature == "T ByArity<T, U>(T x)");
            }, 20_000), "index did not reflect the generic identity changes");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "FromContainer");
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "M");

            JsonElement formerFiles = pack.GetProperty("formerSymbols");
            Assert.DoesNotContain(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == renamePath);
            JsonElement distinctFormerFile = Assert.Single(formerFiles.EnumerateArray(), file =>
                file.GetProperty("path").GetString() == distinctPath);
            List<JsonElement> former = distinctFormerFile.GetProperty("formerSymbols")
                .EnumerateArray().ToList();
            Assert.Contains(former, symbol =>
                symbol.GetProperty("signature").GetString() == "void ByType(int x)");
            Assert.Contains(former, symbol =>
                symbol.GetProperty("signature").GetString() ==
                "void ByQualifiedType(N.T x)");
            Assert.Contains(former, symbol =>
                symbol.GetProperty("signature").GetString() == "T ByArity<T>(T x)");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnindexedWholeFileUsesAnUnknownRangeInsteadOfInventingIntMaxLines()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unindexed-range").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.WriteAllText(Path.Combine(root, "Fresh.cs"),
                "namespace Fresh42; public class FreshType42 { }\n");

            var pack = Parse(tools.ReviewPack(paths: "Fresh.cs", maxBytes: 24576));
            JsonElement item = Assert.Single(pack.GetProperty("unmappedChanges")
                .GetProperty("items").EnumerateArray());
            Assert.Equal("whole_file_unindexed", item.GetProperty("reason").GetString());
            Assert.Equal(1, item.GetProperty("start").GetInt32());
            Assert.False(item.TryGetProperty("end", out _));
            Assert.DoesNotContain("2147483646", pack.GetRawText(), StringComparison.Ordinal);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NamespaceClassificationStopsBeforeLoadingAnOversizedIndexedFile()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-namespace-budget").FullName);
        try
        {
            WriteReviewRepo(root);
            const string path = "Lib/HugeNamespaceBudget.cs";
            string fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            string padding = new string('x', (512 * 1024) + 64);
            File.WriteAllText(fullPath,
                "global using System;\nnamespace NamespaceBudget42;\n" +
                "public class HugeNamespaceBudget42 { }\n/*" + padding + "*/\n");
            Git(root, "add -A");
            Git(root, "commit -q -m namespace-budget-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.WriteAllText(fullPath,
                "global using System.Text;\nnamespace NamespaceBudget42;\n" +
                "public class HugeNamespaceBudget42 { }\n/*" + padding + "*/\n");
            RefreshAndWait(m, path, "HugeNamespaceBudget42");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement coverage = pack.GetProperty("namespaceAnalysisCoverage");
            Assert.Equal(1, coverage.GetProperty("requested").GetInt32());
            Assert.Equal(0, coverage.GetProperty("parsed").GetInt32());
            Assert.Equal(512 * 1024,
                coverage.GetProperty("perFileCharLimit").GetInt32());
            Assert.True(coverage.GetProperty("budgetHit").GetBoolean());
            Assert.Contains(pack.GetProperty("unmappedChanges").GetProperty("items")
                .EnumerateArray(), item => item.GetProperty("reason").GetString() == "file_level");
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.namespace_analysis_budget");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectOwnershipFallbackIncludesEveryUnremovedDefaultAncestor()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-ancestors").FullName);
        try
        {
            WriteReviewRepo(root);
            string outer = Path.Combine(root, "Outer");
            string inner = Path.Combine(outer, "Inner");
            Directory.CreateDirectory(inner);
            File.WriteAllText(Path.Combine(outer, "Outer.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><Compile Remove=\"Inner/Moved.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(inner, "Inner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string oldPath = "Outer/Inner/Type.cs";
            const string newPath = "Outer/Inner/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace OwnerAncestor42; public class SharedByAncestors42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-ancestor-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "SharedByAncestors42");
            }, 20_000), "index did not reflect the default-ancestor move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("parsed").GetInt32() >= 2);
            Assert.False(coverage.TryGetProperty("budgetHit", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectOwnershipFallbackKeepsExplicitReincludeAfterMatchingRemove()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-reinclude").FullName);
        try
        {
            WriteReviewRepo(root);
            string project = Path.Combine(root, "Owner");
            string shared = Path.Combine(root, "Shared");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(project, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Remove=\"../Shared/*.cs\" />" +
                "<Compile Include=\"../Shared/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace OwnerReinclude42; public class ExplicitReinclude42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-reinclude-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.ProjectsContaining(newPath).Any(projectRow =>
                           projectRow.Name == "Owner");
            }, 20_000), "index did not retain the explicit reinclude owner");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(0, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.False(pack.TryGetProperty("deletedFiles", out _));
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), item => item.GetProperty("from").GetString() == oldPath &&
                                          item.GetProperty("to").GetString() == newPath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectOwnershipFallbackAppliesCompileOperationsInDocumentOrder()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-operation-order").FullName);
        try
        {
            WriteReviewRepo(root);
            string owner = Path.Combine(root, "OrderedOwner");
            string shared = Path.Combine(root, "Shared");
            Directory.CreateDirectory(owner);
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(owner, "OrderedOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Shared/*.cs\" />" +
                "<Compile Remove=\"../Shared/Moved.cs\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace OrderedOwner42; public class OrderedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-operation-order-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "OrderedOwnerType42");
            }, 20_000), "index did not reflect the ordered-membership move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("Directory.Build.rsp")]
    [InlineData("MSBuild.rsp")]
    public void ResponseFileDeletionInvalidatesMoveOwnershipProof(string responseFile)
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-rsp-invalidation").FullName);
        try
        {
            WriteReviewRepo(root);
            File.WriteAllText(Path.Combine(root, responseFile),
                "-property:EnableDefaultCompileItems=true\n");
            Git(root, "add -A");
            Git(root, "commit -q -m response-file-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            const string oldPath = "Lib/Widget.cs";
            const string newPath = "Lib/MovedWidget.cs";
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            File.Delete(Path.Combine(root, responseFile));
            m.RequestRefresh(new[] { oldPath, newPath, responseFile });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "Widget");
            }, 20_000), "index did not reflect the response-file move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("src/App.csproj", true)]
    [InlineData("src/App.csproj.user", true)]
    [InlineData("shared/Shared.shproj", true)]
    [InlineData("build/Build.proj", true)]
    [InlineData("shared/Imports.projitems", true)]
    [InlineData("Phoenix.sln", true)]
    [InlineData("Phoenix.slnx", true)]
    [InlineData("Phoenix.slnf", true)]
    [InlineData("config/Directory.Build.props", true)]
    [InlineData("config/Directory.Build.targets", true)]
    [InlineData("config/Directory.Build.rsp", true)]
    [InlineData("config/MSBuild.rsp", true)]
    [InlineData("config/directory.build.RSP", true)]
    [InlineData("config/notes.rsp", false)]
    [InlineData("src/App.cs", false)]
    public void ProjectShapePathsRecognizeEveryBuildAndSolutionShape(string path,
        bool expected)
    {
        Assert.Equal(expected, NavigationTools.IsProjectShapePath(path));
    }

    [Fact]
    public void ReviewPackUsesProjectShapeClassifierForChangedProjectFiles()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-project-shapes").FullName);
        try
        {
            WriteReviewRepo(root);
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string[] paths =
            [
                "build/App.csproj.user",
                "build/Shared.shproj",
                "build/Build.proj",
                "build/Imports.projitems",
                "build/Directory.Build.rsp",
                "build/MSBuild.rsp",
                "Phoenix.slnx",
                "Phoenix.slnf",
                "Phoenix.sln",
                "build/Directory.Build.props",
                "build/Directory.Build.targets",
            ];

            JsonElement pack = Parse(tools.ReviewPack(paths: string.Join(',', paths),
                maxBytes: 24576));
            string[] actual = pack.GetProperty("changedProjectFiles").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), actual);
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_files_changed");
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("BaseOutputPath")]
    [InlineData("BaseIntermediateOutputPath")]
    [InlineData("DefaultLanguageSourceExtension")]
    [InlineData("CustomBeforeMicrosoftCommonProps")]
    [InlineData("CustomAfterMicrosoftCommonTargets")]
    [InlineData("CustomBeforeMicrosoftCSharpTargets")]
    [InlineData("ImportByWildcardBeforeMicrosoftCommonProps")]
    [InlineData("ImportUserLocationsByWildcardAfterMicrosoftCommonTargets")]
    [InlineData("MSBuildExtensionsPath")]
    public void RawProjectShapeRejectsUnevaluatedSdkMembershipControls(string propertyName)
    {
        string xml = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><{propertyName}>custom</{propertyName}></PropertyGroup></Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.False(shape.CompileOwnershipComplete);
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk", true)]
    [InlineData("Microsoft.NET.Sdk/9.0.100", true)]
    [InlineData("Microsoft.NET.Sdk.Contoso", false)]
    [InlineData("Microsoft.NET.Sdk.Web", false)]
    public void RawProjectShapeAcceptsOnlyTheKnownStandardSdk(string sdk, bool expectedComplete)
    {
        string xml = $"<Project Sdk=\"{sdk}\" />";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal(expectedComplete, shape.CompileOwnershipComplete);
    }

    [Fact]
    public void RawProjectShapeTreatsPackageBuildAssetsAsUnevaluated()
    {
        const string xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                           "<PackageReference Include=\"Build.Customizer\" Version=\"1.0.0\" />" +
                           "</ItemGroup></Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.False(shape.CompileOwnershipComplete);
    }

    [Theory]
    [InlineData("true", true, true)]
    [InlineData("false", false, true)]
    [InlineData("0", false, false)]
    [InlineData("no", false, false)]
    [InlineData("", false, false)]
    public void RawProjectShapeRequiresABooleanDefaultCompileValue(string value,
        bool expectedDefaultItems, bool expectedComplete)
    {
        string xml = $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                     $"<EnableDefaultCompileItems>{value}</EnableDefaultCompileItems>" +
                     "</PropertyGroup></Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal(expectedDefaultItems, shape.DefaultCompileItems);
        Assert.Equal(expectedComplete, shape.CompileOwnershipComplete);
    }

    [Theory]
    [InlineData("<ItemGroup><Compile Include=\"../../Moved.cs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Compile Include=\"Old.cs\" Exclude=\"../../Moved.cs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Compile Remove=\"C:/outside/Moved.cs\" /></ItemGroup>")]
    [InlineData("<ProjectExtensions><Compile Include=\"Moved.cs\" /></ProjectExtensions>")]
    public void RawProjectShapeRejectsEscapingOrNonItemCompileSpecs(string projectXml)
    {
        string xml = "<Project Sdk=\"Microsoft.NET.Sdk\">" + projectXml + "</Project>";
        var shape = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape("P/P.csproj",
            System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.False(shape.CompileOwnershipComplete);
    }

    [Fact]
    public void MsBuildGlobCanUseCaseSensitiveProofSemantics()
    {
        Assert.True(CodeNav.Core.Discovery.MsBuildGlob.IsMatch(
            "Shared/Moved.cs", "shared/*.cs"));
        Assert.False(CodeNav.Core.Discovery.MsBuildGlob.IsMatch(
            "Shared/Moved.cs", "shared/*.cs", ignoreCase: false));
    }

    [Fact]
    public void ProjectOwnershipCanForceCaseSensitiveProofSemantics()
    {
        var project = new ProjectRow(1, "Owner/Owner.csproj", "Owner", "sdk", "net9.0",
            false, "parsed");
        var parsed = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
            project.Path, System.Text.Encoding.UTF8.GetBytes(
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
                "<ItemGroup><Compile Include=\"../owner/Moved.cs\" /></ItemGroup></Project>"));
        var shapes = new Dictionary<long, CodeNav.Core.Discovery.ParsedProject>
        {
            [project.Id] = parsed,
        };
        Assert.Empty(NavigationTools.LikelyOwningProjectIds("Owner/Moved.cs", [project], shapes,
            ignoreCaseOverride: false));
        Assert.Contains(project.Id, NavigationTools.LikelyOwningProjectIds("Owner/Moved.cs",
            [project], shapes, ignoreCaseOverride: true));
    }

    [Theory]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<EnableDefaultCompileItems>0</EnableDefaultCompileItems></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<DefaultLanguageSourceExtension>.fs</DefaultLanguageSourceExtension></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" /></ItemGroup>")]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" /></ItemGroup>" +
        "<ProjectExtensions><Compile Include=\"Moved.cs\" /></ProjectExtensions>")]
    [InlineData("<PropertyGroup><TargetFramework>net9.0</TargetFramework>" +
        "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Old.cs\" />" +
        "<Compile Include=\"../../Owner/Moved.cs\" /></ItemGroup>")]
    public void UnevaluatedProjectMembershipCannotPreserveAnExactMove(string ownerBody)
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-unevaluated-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string owner = Path.Combine(root, "Owner");
            string secondary = Path.Combine(root, "Secondary");
            Directory.CreateDirectory(owner);
            Directory.CreateDirectory(secondary);
            File.WriteAllText(Path.Combine(owner, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">" + ownerBody + "</Project>");
            File.WriteAllText(Path.Combine(secondary, "Secondary.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Owner/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Owner/Old.cs";
            const string newPath = "Owner/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace UnevaluatedOwner42; public class UnevaluatedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m unevaluated-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "UnevaluatedOwnerType42");
            }, 20_000), "index did not reflect the unevaluated-owner move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnixProjectOwnershipProofUsesCaseSensitiveCompileSpecs()
    {
        if (OperatingSystem.IsWindows() || !GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-case-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            Directory.CreateDirectory(Path.Combine(root, "Owner"));
            Directory.CreateDirectory(Path.Combine(root, "Secondary"));
            File.WriteAllText(Path.Combine(root, "Owner", "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"Old.cs\" />" +
                "<Compile Include=\"../owner/Moved.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Secondary", "Secondary.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Owner/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Owner/Old.cs";
            const string newPath = "Owner/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace CaseOwner42; public class CaseOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m case-owner-baseline");
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null && q.ContentByPath(newPath) is not null;
            }, 20_000));
            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(".hidden/Moved.cs", "", true)]
    [InlineData(".hidden/Moved.cs",
        "<ItemGroup><Compile Include=\".hidden/Moved.cs\" /></ItemGroup>", false)]
    [InlineData("Moved.cs",
        "<ItemGroup><Compile Include=\"Moved.cs\" Exclude=\"Moved.cs\" /></ItemGroup>", false)]
    public void DefaultSdkMembershipHonorsExclusionsAndOrderedExplicitIncludes(
        string movedRelativePath, string projectItems, bool expectedDeletion)
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-sdk-default-membership").FullName);
        try
        {
            WriteReviewRepo(root);
            string projectDirectory = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup>" + projectItems +
                "</Project>");
            const string oldPath = "P/Old.cs";
            string newPath = "P/" + movedRelativePath;
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace SdkDefaults42; public class SdkDefaultType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m sdk-default-membership-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string destination = Path.Combine(root,
                newPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                destination);
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "SdkDefaultType42");
            }, 20_000), "index did not reflect the SDK-default membership move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(expectedDeletion ? 1 : 0,
                pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            if (expectedDeletion)
            {
                Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == oldPath);
            }
            else if (pack.TryGetProperty("deletedFiles", out JsonElement deletedFiles))
            {
                Assert.DoesNotContain(deletedFiles.EnumerateArray(), file =>
                    file.GetProperty("path").GetString() == oldPath);
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImportedProjectOwnershipCannotBecomeACompleteMovePreservationProof()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-imported-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string importedOwner = Path.Combine(root, "ImportedOwner");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(importedOwner);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(shared, "Shared.projitems"),
                "<Project><ItemGroup><Compile Include=\"Type.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(importedOwner, "ImportedOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><Import Project=\"../Shared/Shared.projitems\" /></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace ImportedOwner42; public class ImportedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m imported-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "ImportedOwnerType42");
            }, 20_000), "index did not reflect the imported-owner move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_shape_incomplete");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExpressionBasedProjectOwnershipCannotBecomeACompleteMovePreservationProof()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-expression-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string expressionOwner = Path.Combine(root, "ExpressionOwner");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(expressionOwner);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(expressionOwner, "ExpressionOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "<SharedRoot>../Shared</SharedRoot></PropertyGroup><ItemGroup>" +
                "<Compile Include=\"$(SharedRoot)/*.cs\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace ExpressionOwner42; public class ExpressionOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m expression-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "ExpressionOwnerType42");
            }, 20_000), "index did not reflect the expression-owner move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ConditionedProjectOwnershipCannotBecomeACompleteMovePreservationProof()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-conditioned-owner").FullName);
        try
        {
            WriteReviewRepo(root);
            string shared = Path.Combine(root, "Shared");
            string conditionalOwner = Path.Combine(root, "ConditionalOwner");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(conditionalOwner);
            File.WriteAllText(Path.Combine(shared, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(conditionalOwner, "ConditionalOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"../Shared/*.cs\" " +
                "Condition=\"Exists('../Shared/Type.cs')\" /></ItemGroup></Project>");
            const string oldPath = "Shared/Type.cs";
            const string newPath = "Shared/Moved.cs";
            File.WriteAllText(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                "namespace ConditionedOwner42; public class ConditionedOwnerType42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m conditioned-owner-baseline");

            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            File.Move(Path.Combine(root, oldPath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, newPath.Replace('/', Path.DirectorySeparatorChar)));
            m.RequestRefresh(new[] { oldPath, newPath });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.ContentByPath(oldPath) is null &&
                       q.Outline(newPath).Any(symbol => symbol.Name == "ConditionedOwnerType42");
            }, 20_000), "index did not reflect the conditioned-owner move");

            var pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == oldPath);
            Assert.True(pack.GetProperty("projectOwnershipFallbackCoverage")
                .GetProperty("evaluationIncomplete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectShapeFallbackRejectsAnOversizedProjectBeforeXmlParsing()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-project-shape-budget").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Huge.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!--" +
                new string('x', (256 * 1024) + 1) + "--></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "Huge.csproj", "Huge", "sdk", "net9.0", false, "parsed"),
            };
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects);
            Assert.True(snapshot.BudgetHit);
            Assert.False(snapshot.EvaluationIncomplete);
            Assert.Equal(1, snapshot.Attempted);
            Assert.Empty(snapshot.Parsed);
            Assert.Equal(0, snapshot.BytesRead);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectShapeFallbackSkipsXmlWorkWhenTheProjectUniverseIsAlreadyCapped()
    {
        var projects = new List<ProjectRow>
        {
            new(1, "Missing.csproj", "Missing", "sdk", "net9.0", false, "parsed"),
        };
        NavigationTools.ReviewProjectShapeSnapshot snapshot =
            NavigationTools.LoadProjectShapesBounded(Path.GetTempPath(), projects,
                projectCountLimited: true);
        Assert.True(snapshot.BudgetHit);
        Assert.False(snapshot.EvaluationIncomplete);
        Assert.Equal(2, snapshot.RequestedAtLeast);
        Assert.Equal(0, snapshot.Attempted);
        Assert.Empty(snapshot.Parsed);
    }

    [Fact]
    public void ProjectShapeFallbackTreatsImplicitDirectoryBuildFilesAsIncomplete()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-directory-build-shape").FullName);
        try
        {
            string projectDirectory = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
                "<Project><ItemGroup><Compile Include=\"Shared/*.cs\" /></ItemGroup></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "P/P.csproj", "P", "sdk", "net9.0", false, "parsed"),
            };
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects);
            Assert.True(snapshot.EvaluationIncomplete);
            Assert.False(snapshot.BudgetHit);
            Assert.Equal(1, snapshot.Attempted);
            Assert.Empty(snapshot.Parsed);

            var customSdk = CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
                "P/Custom.csproj", System.Text.Encoding.UTF8.GetBytes(
                    "<Project Sdk=\"Contoso.Custom.Sdk\" />"));
            Assert.False(customSdk.CompileOwnershipComplete);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackPinsRowsAndMetadataToOneRefreshEpoch()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-review-epoch").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/ReviewEpoch.cs";
            string fullPath = Path.Combine(root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath,
                "namespace ReviewEpoch42; public class BeforeRefreshEpoch42 { " +
                "public void BeforeRefreshMember42() { } }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m review-epoch-baseline");

            using var manager = StartManager(root);
            Assert.True(WaitUntil(() => manager.State == "ready", 20_000));
            IndexHealth before = manager.Health();
            var tools = new NavigationTools(manager, new SemanticService(manager));
            int refreshes = 0;
            manager.ReviewSnapshotAfterQueryForTest = sql =>
            {
                if (!sql.Contains("SELECT id, path, size, line_count, is_generated FROM files",
                        StringComparison.Ordinal) || Interlocked.Exchange(ref refreshes, 1) != 0)
                    return;

                File.WriteAllText(fullPath,
                    "namespace ReviewEpoch42; public class AfterRefreshEpoch42 { " +
                    "public void AfterRefreshMember42() { } }\n");
                long processedBefore = manager.Health().PendingProcessed;
                manager.RequestRefresh(new[] { relativePath });
                Assert.True(WaitUntil(() =>
                {
                    using var q = manager.OpenQueries();
                    return manager.State == "ready" &&
                           manager.Health().PendingProcessed > processedBefore &&
                           q.SearchSymbols("AfterRefreshEpoch42", "exact", null, 2).Count == 1;
                }, 20_000), "deterministic in-review refresh did not complete");
            };

            JsonElement pack;
            try
            {
                pack = Parse(tools.ReviewPack(paths: relativePath, maxBytes: 24576));
            }
            finally
            {
                manager.ReviewSnapshotAfterQueryForTest = null;
            }

            Assert.Equal(1, Volatile.Read(ref refreshes));
            List<string?> names = pack.GetProperty("symbols").EnumerateArray()
                .Select(item => item.GetProperty("symbol").GetProperty("name").GetString())
                .ToList();
            Assert.Contains("BeforeRefreshEpoch42", names);
            Assert.DoesNotContain("AfterRefreshEpoch42", names);
            Assert.Equal(before.LastRefreshUtc,
                pack.GetProperty("meta").GetProperty("lastRefreshUtc").GetString());
            Assert.NotEqual(before.LastRefreshUtc, manager.Health().LastRefreshUtc);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FullRebuildWaitsForPinnedReviewSnapshotToDrain()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-review-rebuild-gate").FullName);
        try
        {
            WriteReviewRepo(root);
            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            using var waiting = new ManualResetEventSlim(false);
            using var boundary = new ManualResetEventSlim(false);
            using var completed = new ManualResetEventSlim(false);
            int activeAtBoundary = -1;
            manager.FullRebuildWaitingForReviewSnapshotsForTest = () => waiting.Set();
            manager.FullRebuildDestructiveBoundaryForTest = active =>
            {
                activeAtBoundary = active;
                boundary.Set();
            };
            manager.FullRebuildCompletedForTest = () => completed.Set();
            int rebuildRequests = 0;
            manager.ReviewSnapshotAfterQueryForTest = sql =>
            {
                if (!sql.Contains("SELECT id, path, size, line_count, is_generated FROM files",
                        StringComparison.Ordinal) ||
                    Interlocked.Exchange(ref rebuildRequests, 1) != 0)
                    return;
                manager.RequestFullRebuild();
                Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)),
                    "full rebuild did not reach the active-review gate");
                Assert.False(completed.IsSet,
                    "full rebuild crossed its destructive boundary while a review snapshot was active");
                Assert.False(boundary.IsSet,
                    "full rebuild reached destructive work while a review snapshot was active");
            };

            JsonElement pack;
            try
            {
                pack = Parse(tools.ReviewPack(paths: "Lib/Widget.cs", maxBytes: 24576));
            }
            finally
            {
                manager.ReviewSnapshotAfterQueryForTest = null;
            }
            Assert.Contains(pack.GetProperty("symbols").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("name").GetString() == "Widget");
            Assert.True(boundary.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref activeAtBoundary));
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "full rebuild did not resume after the review snapshot drained");
            // The rebuild epilogue deliberately queues a detect-all convergence sweep (edits made
            // during BuildOwned's pre-watcher interval); "refreshing" is a designed transient
            // right after the completed seam, so wait for the landing state instead of racing it.
            Assert.True(WaitUntil(() => manager.State == "ready", 20000),
                $"manager did not settle at 'ready' after the rebuild (state '{manager.State}')");
            manager.FullRebuildWaitingForReviewSnapshotsForTest = null;
            manager.FullRebuildDestructiveBoundaryForTest = null;
            manager.FullRebuildCompletedForTest = null;
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SeparateTypeHeaderAndMemberHunksRetainBothSidesReviewUnits()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-per-hunk-types").FullName);
        try
        {
            WriteReviewRepo(root);
            const string relativePath = "Lib/PerHunkType.cs";
            string fullPath = Path.Combine(root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath,
                "namespace PerHunkMapping42;\n" +
                "public class OldPerHunkType42 : OldPerHunkBase42\n" +
                "{\n" +
                "    public void StableOne42() { }\n" +
                "\n\n\n\n" +
                "    public void OldPerHunkMember42() { int value = 1; }\n" +
                "}\n" +
                "public class OldPerHunkBase42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m per-hunk-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            File.WriteAllText(fullPath,
                "namespace PerHunkMapping42;\n" +
                "public class NewPerHunkType42 : NewPerHunkBase42\n" +
                "{\n" +
                "    public void StableOne42() { }\n" +
                "\n\n\n\n" +
                "    public void NewPerHunkMember42() { int value = 2; }\n" +
                "}\n" +
                "public class OldPerHunkBase42 { }\n" +
                "public class NewPerHunkBase42 { }\n");
            RefreshAndWait(manager, relativePath, "NewPerHunkMember42");

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            List<string?> currentNames = pack.GetProperty("symbols").EnumerateArray()
                .Select(item => item.GetProperty("symbol").GetProperty("name").GetString())
                .ToList();
            Assert.Contains("NewPerHunkType42", currentNames);
            Assert.Contains("NewPerHunkMember42", currentNames);

            JsonElement formerFile = Assert.Single(pack.GetProperty("formerSymbols")
                .EnumerateArray(), file => file.GetProperty("path").GetString() == relativePath);
            List<string?> formerNames = formerFile.GetProperty("formerSymbols").EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString()).ToList();
            Assert.Contains("OldPerHunkType42", formerNames);
            Assert.Contains("OldPerHunkMember42", formerNames);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectOwnershipGlobBudgetIsStickyAndFailsClosed()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-glob-budget").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "Owner.csproj", "Owner", "sdk", "net9.0", false, "parsed"),
            };
            var tinyBudget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 1,
                TimeSpan.FromSeconds(10));
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects,
                    matchBudgetOverride: tinyBudget);
            Assert.False(snapshot.BudgetHit);

            HashSet<long> owners = NavigationTools.LikelyOwningProjectIds("Nested/File.cs",
                snapshot.Projects, snapshot.Parsed, matchBudget: snapshot.MatchBudget,
                onMatchAttempt: snapshot.MarkGlobMatchAttempted);
            Assert.Empty(owners);
            Assert.True(snapshot.MatchBudget.IsExhausted);
            Assert.True(snapshot.BudgetHit);
            Assert.False(snapshot.Complete);
            Assert.Equal(1, snapshot.MatchBudget.OperationLimit);
            Assert.True(snapshot.MatchBudget.Operations <= snapshot.MatchBudget.OperationLimit);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExpiredOwnershipBudgetCannotProveDefaultSdkOwnership()
    {
        CodeNav.Core.Discovery.ParsedProject parsed =
            CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
            "Default.csproj", System.Text.Encoding.UTF8.GetBytes(
                "<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        Assert.True(parsed.DefaultCompileItems);
        Assert.Empty(parsed.CompileOperations ?? []);
        var projects = new List<ProjectRow>
        {
            new(1, "Default.csproj", "Default", "sdk", "net9.0", false, "parsed"),
        };
        var budget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 1_000,
            TimeSpan.Zero);

        HashSet<long> owners = NavigationTools.LikelyOwningProjectIds("Default.cs",
            projects, new Dictionary<long, CodeNav.Core.Discovery.ParsedProject> { [1] = parsed },
            matchBudget: budget);

        Assert.Empty(owners);
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void DefaultSdkOwnershipCannotEscapeAfterBudgetExpiresDuringFinalEvaluation()
    {
        CodeNav.Core.Discovery.ParsedProject parsed =
            CodeNav.Core.Discovery.ProjectFileParser.ParseCompileShape(
                "Default.csproj", System.Text.Encoding.UTF8.GetBytes(
                    "<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        var projects = new List<ProjectRow>
        {
            new(1, "Default.csproj", "Default", "sdk", "net9.0", false, "parsed"),
        };
        var budget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 32,
            Timeout.InfiniteTimeSpan);
        bool evaluationCompleted = false;

        HashSet<long> owners = NavigationTools.LikelyOwningProjectIds("Default.cs",
            projects, new Dictionary<long, CodeNav.Core.Discovery.ParsedProject> { [1] = parsed },
            matchBudget: budget,
            afterDefaultSdkEvaluationForTest: () =>
            {
                evaluationCompleted = true;
                Assert.Equal(CodeNav.Core.Discovery.GlobMatchOutcome.BudgetExhausted,
                    CodeNav.Core.Discovery.MsBuildGlob.Match(new string('x', 128), "*",
                        ignoreCase: false, budget));
            });

        Assert.True(evaluationCompleted);
        Assert.True(budget.IsExhausted);
        Assert.Empty(owners);
    }

    [Fact]
    public void ProjectOwnershipCacheIsInvalidatedAfterLaterGlobExhaustion()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-cache-budget").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            var projects = new List<ProjectRow>
            {
                new(1, "Owner.csproj", "Owner", "sdk", "net9.0", false, "parsed"),
            };
            var budget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 128,
                TimeSpan.FromSeconds(10));
            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects,
                    matchBudgetOverride: budget);
            var resolver = new NavigationTools.ReviewProjectOwnershipResolver(() => snapshot);

            Assert.Contains(1, resolver.OwnerIds("A.cs"));
            Assert.Empty(resolver.OwnerIds(new string('a', 512) + ".cs"));
            Assert.True(snapshot.BudgetHit);
            Assert.Empty(resolver.OwnerIds("A.cs"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackDisclosesProjectOwnershipGlobExhaustion()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-glob-coverage").FullName);
        try
        {
            WriteReviewRepo(root);
            string ownerDirectory = Path.Combine(root, "GlobOwner");
            Directory.CreateDirectory(ownerDirectory);
            File.WriteAllText(Path.Combine(ownerDirectory, "GlobOwner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            const string relativePath = "GlobOwner/Deleted.cs";
            File.WriteAllText(Path.Combine(ownerDirectory, "Deleted.cs"),
                "namespace GlobOwner42; public class DeletedByGlobBudget42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m owner-glob-coverage-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(32, 1,
                    TimeSpan.FromSeconds(10));
            File.Delete(Path.Combine(ownerDirectory, "Deleted.cs"));
            manager.RequestRefresh(new[] { relativePath });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(relativePath) is null;
            }, 20_000), "index did not reflect glob-budget deletion");

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("budgetHit").GetBoolean());
            Assert.True(coverage.GetProperty("globBudgetHit").GetBoolean());
            Assert.False(coverage.TryGetProperty("shapeBudgetHit", out _));
            Assert.False(coverage.GetProperty("complete").GetBoolean());
            Assert.Equal(1, coverage.GetProperty("globOperationLimit").GetInt64());
            Assert.True(coverage.GetProperty("globOperations").GetInt64() <= 1);
            Assert.Equal(32, coverage.GetProperty("globSegmentLimit").GetInt32());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_glob_budget");
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_shape_budget");
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == relativePath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackSeparatesShapeOnlyOwnershipBudgetCause()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-shape-cause").FullName);
        try
        {
            WriteReviewRepo(root);
            string hugeDirectory = Path.Combine(root, "HugeShape");
            Directory.CreateDirectory(hugeDirectory);
            File.WriteAllText(Path.Combine(hugeDirectory, "HugeShape.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><!--" +
                new string('x', (256 * 1024) + 1) + "--></Project>");
            Git(root, "add -A");
            Git(root, "commit -q -m shape-cause-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            File.Delete(Path.Combine(root, "Lib", "Old.cs"));
            manager.RequestRefresh(new[] { "Lib/Old.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath("Lib/Old.cs") is null;
            }, 20_000));

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("shapeBudgetHit").GetBoolean());
            Assert.False(coverage.TryGetProperty("globBudgetHit", out _));
            Assert.True(coverage.GetProperty("budgetHit").GetBoolean());
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_shape_budget");
            Assert.DoesNotContain(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.project_glob_budget");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProjectShapeLoadingDoesNotConsumeOrReportGlobBudgetBeforeMatching()
    {
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-owner-cause-isolation").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Default.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var projects = new List<ProjectRow>
            {
                new(1, "Default.csproj", "Default", "sdk", "net9.0", false, "parsed"),
            };
            var zeroTimeMatchBudget = new CodeNav.Core.Discovery.GlobMatchBudget(32, 1_000,
                TimeSpan.Zero);

            NavigationTools.ReviewProjectShapeSnapshot snapshot =
                NavigationTools.LoadProjectShapesBounded(root, projects,
                    matchBudgetOverride: zeroTimeMatchBudget);

            Assert.False(snapshot.ShapeBudgetHit);
            Assert.False(snapshot.GlobMatchAttempted);
            Assert.False(snapshot.GlobBudgetHit);
            Assert.False(snapshot.MatchBudget.IsExhausted);
            Assert.True(snapshot.Complete);

            var resolver = new NavigationTools.ReviewProjectOwnershipResolver(() => snapshot);
            Assert.Empty(resolver.OwnerIds("Default.cs"));

            Assert.False(snapshot.ShapeBudgetHit);
            Assert.True(snapshot.GlobMatchAttempted);
            Assert.True(snapshot.GlobBudgetHit);
            Assert.True(snapshot.MatchBudget.IsExhausted);
            Assert.False(snapshot.Complete);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExactMoveWithUnrelatedDeletionAndCompleteOwnerProofStaysMoveOnly()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-move-unrelated-delete").FullName);
        try
        {
            WriteReviewRepo(root);
            const string moveSource = "Lib/Widget.cs";
            const string moveTarget = "Lib/MovedWidget.cs";
            const string unrelatedDeletion = "Lib/Old.cs";

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(256, 1_000_000,
                    Timeout.InfiniteTimeSpan);
            File.Move(Path.Combine(root,
                    moveSource.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, moveTarget.Replace('/', Path.DirectorySeparatorChar)));
            File.Delete(Path.Combine(root,
                unrelatedDeletion.Replace('/', Path.DirectorySeparatorChar)));
            manager.RequestRefresh(new[] { moveSource, moveTarget, unrelatedDeletion });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(moveSource) is null &&
                       q.Outline(moveTarget).Any(symbol => symbol.Name == "Widget") &&
                       q.ContentByPath(unrelatedDeletion) is null;
            }, 20_000), "index did not reflect the exact move plus unrelated deletion");

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == moveSource &&
                move.GetProperty("to").GetString() == moveTarget);
            Assert.Equal(1, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            JsonElement deleted = Assert.Single(pack.GetProperty("deletedFiles")
                .EnumerateArray());
            Assert.Equal(unrelatedDeletion, deleted.GetProperty("path").GetString());
            Assert.DoesNotContain(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == moveSource);
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("complete").GetBoolean());
            Assert.False(coverage.TryGetProperty("budgetHit", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OrdinaryDeletionPreflightExhaustionReplaysEarlierExactMove()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-move-preflight-exhaustion").FullName);
        try
        {
            File.WriteAllText(Path.Combine(root, "Owner.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            const string moveSource = "Move.cs";
            const string moveTarget = "Moved.cs";
            const string ordinaryDeletion = "Delete.cs";
            File.WriteAllText(Path.Combine(root, moveSource),
                "namespace PreflightBudget42; public class MovedByPreflight42 { }\n");
            File.WriteAllText(Path.Combine(root, ordinaryDeletion),
                "namespace PreflightBudget42; public class DeletedByPreflight42 { }\n");
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            Git(root, "init -q -b main");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "add -A");
            Git(root, "commit -q -m preflight-budget-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            // One default-SDK owner lookup costs 16 operations. The exact move consumes two
            // complete lookups (32); the ordinary deletion's preflight gets four operations into
            // its entry checkpoint, then exhausts before it can evaluate the project.
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(32, 36,
                    Timeout.InfiniteTimeSpan);
            File.Move(Path.Combine(root, moveSource), Path.Combine(root, moveTarget));
            File.Delete(Path.Combine(root, ordinaryDeletion));
            manager.RequestRefresh(new[] { moveSource, moveTarget, ordinaryDeletion });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(moveSource) is null &&
                       q.Outline(moveTarget).Any(symbol =>
                           symbol.Name == "MovedByPreflight42") &&
                       q.ContentByPath(ordinaryDeletion) is null;
            }, 20_000), "index did not reflect the move plus ordinary deletion");

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == moveSource &&
                move.GetProperty("to").GetString() == moveTarget);
            Assert.Equal(2, pack.GetProperty("changedFiles").GetProperty("deleted").GetInt32());
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == ordinaryDeletion);
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == moveSource);
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.Equal(36, coverage.GetProperty("globOperationLimit").GetInt64());
            Assert.Equal(36, coverage.GetProperty("globOperations").GetInt64());
            Assert.True(coverage.GetProperty("globBudgetHit").GetBoolean());
            Assert.False(coverage.GetProperty("complete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LaterMoveGlobExhaustionReplaysEarlierProvisionalMoveAsDeletion()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(
            Directory.CreateTempSubdirectory("codenav-42-move-budget-replay").FullName);
        try
        {
            WriteReviewRepo(root);
            string movesDirectory = Path.Combine(root, "Moves");
            Directory.CreateDirectory(movesDirectory);
            File.WriteAllText(Path.Combine(movesDirectory, "Moves.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework>" +
                "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                "</PropertyGroup><ItemGroup><Compile Include=\"**/*.cs\" />" +
                "</ItemGroup></Project>");
            const string aSource = "Moves/A.cs";
            const string aTarget = "Moves/A2.cs";
            string longStem = "Z" + new string('z', 96);
            string zSource = $"Moves/{longStem}.cs";
            string zTarget = $"Moves/{longStem}2.cs";
            File.WriteAllText(Path.Combine(movesDirectory, "A.cs"),
                "namespace MoveBudget42; public class EarlyMove42 { " +
                "public void Preserved42() { } }\n");
            File.WriteAllText(Path.Combine(movesDirectory, longStem + ".cs"),
                "namespace MoveBudget42; public class LateMove42 { }\n");
            Git(root, "add -A");
            Git(root, "commit -q -m move-budget-baseline");

            using var manager = StartManager(root);
            var tools = new NavigationTools(manager, new SemanticService(manager));
            tools.ReviewProjectGlobBudgetFactoryForTest = () =>
                new CodeNav.Core.Discovery.GlobMatchBudget(64, 256,
                    TimeSpan.FromSeconds(10));
            File.Move(Path.Combine(movesDirectory, "A.cs"),
                Path.Combine(movesDirectory, "A2.cs"));
            File.Move(Path.Combine(movesDirectory, longStem + ".cs"),
                Path.Combine(movesDirectory, longStem + "2.cs"));
            manager.RequestRefresh(new[] { aSource, aTarget, zSource, zTarget });
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return manager.State == "ready" && q.ContentByPath(aSource) is null &&
                       q.Outline(aTarget).Any(symbol => symbol.Name == "EarlyMove42") &&
                       q.ContentByPath(zSource) is null &&
                       q.Outline(zTarget).Any(symbol => symbol.Name == "LateMove42");
            }, 20_000), "index did not reflect both exact moves");

            JsonElement pack = Parse(tools.ReviewPack(maxBytes: 24576));
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == aSource &&
                move.GetProperty("to").GetString() == aTarget);
            Assert.Contains(pack.GetProperty("movedFiles").GetProperty("items")
                .EnumerateArray(), move =>
                move.GetProperty("from").GetString() == zSource &&
                move.GetProperty("to").GetString() == zTarget);
            JsonElement replayed = Assert.Single(pack.GetProperty("deletedFiles")
                .EnumerateArray(), file => file.GetProperty("path").GetString() == aSource);
            Assert.Contains(pack.GetProperty("deletedFiles").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == zSource);
            JsonElement formerType = Assert.Single(replayed.GetProperty("formerTypes")
                .EnumerateArray(), type => type.GetProperty("name").GetString() == "EarlyMove42");
            Assert.Equal("ambiguous_survivor",
                formerType.GetProperty("danglingStatus").GetString());
            Assert.False(formerType.TryGetProperty("danglingCandidates", out _));
            JsonElement coverage = pack.GetProperty("projectOwnershipFallbackCoverage");
            Assert.True(coverage.GetProperty("globBudgetHit").GetBoolean());
            Assert.False(coverage.GetProperty("complete").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture + helpers

    /// <summary>Lib (Widget.DoWork with a replaceable body marker + Old.cs OldThing) and
    /// Consumer (ProjectReference -> Lib; names Widget/DoWork/OldThing so reference candidates
    /// and the dependent split have material). Committed on branch 'main'.</summary>
    private static void WriteReviewRepo(string root)
    {
        string lib = Path.Combine(root, "Lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(lib, "Widget.cs"),
            """
            namespace Lib
            {
                public class Widget
                {
                    public void DoWork()
                    {
                        // body-marker
                    }
                }
            }
            """);
        File.WriteAllText(Path.Combine(lib, "Old.cs"),
            "namespace Lib { public class OldThing { public void Legacy() { } } }");
        string consumer = Path.Combine(root, "Consumer");
        Directory.CreateDirectory(consumer);
        File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(consumer, "Use.cs"),
            """
            namespace Consumer
            {
                public class Use
                {
                    public void Run()
                    {
                        var w = new Lib.Widget();
                        w.DoWork();
                        var o = new Lib.OldThing();
                        o.Legacy();
                    }
                }
            }
            """);
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
        Git(root, "init -q -b main");
        Git(root, "config user.email test@example.com");
        Git(root, "config user.name CodeNavTest");
        Git(root, "config commit.gpgsign false");
        Git(root, "add -A");
        Git(root, "commit -q -m init");
    }

    private static IndexManager StartManager(string root)
    {
        string dbPath = IndexBuilder.DefaultDbPath(root);
        IndexBuilder.Build(root, dbPath);
        var m = new IndexManager(root, dbPath);
        m.Start();
        Assert.True(WaitUntil(() => m.IsQueryable && m.Health().IndexedCommit is not null, 30000),
            "manager did not become queryable with a git baseline");
        return m;
    }

    /// <summary>Push one file through the pump and wait until its symbol state is visible —
    /// review_pack maps hunk ranges against INDEX spans, so the index must reflect the edit.</summary>
    private static void RefreshAndWait(IndexManager m, string relPath, string symbolName)
    {
        m.RequestRefresh(new[] { relPath });
        Assert.True(WaitUntil(() =>
        {
            using var q = m.OpenQueries();
            return q.SearchSymbols(symbolName, "exact", null, 2).Count > 0;
        }, 20000), $"index did not reflect {relPath}");
    }

    private static void Git(string dir, string args) =>
        GitInfo.RunProcess("git", dir,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);

    private static string GitOutput(string dir, string args)
    {
        string? output = GitInfo.RunProcess("git", dir,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args,
            waitMs: 20000);
        Assert.NotNull(output);
        return output!;
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

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
