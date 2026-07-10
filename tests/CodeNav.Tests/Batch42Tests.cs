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
        }
        finally { Cleanup(root); }
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
            using var m = StartManager(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // An untracked new class + a modified csproj, both uncommitted.
            File.WriteAllText(Path.Combine(root, "Lib", "Fresh.cs"),
                "namespace Lib { public class Fresh42 { } }");
            string csproj = Path.Combine(root, "Lib", "Lib.csproj");
            File.WriteAllText(csproj, File.ReadAllText(csproj).Replace("</Project>",
                "  <!-- reviewed change -->\n</Project>"));
            RefreshAndWait(m, "Lib/Fresh.cs", "Fresh42");

            var pack = Parse(tools.ReviewPack());
            Assert.Contains("Lib/Lib.csproj",
                pack.GetProperty("changedProjectFiles").EnumerateArray().Select(x => x.GetString()));
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
