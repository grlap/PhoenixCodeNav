using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 39 (v0.9.3) — the search_text honesty pair from the v0.9.2 field review:
/// 1ly — didYouMean gains SPELLING near-misses (single-token Damerau edit-distance-1 against
///       indexed symbol names, first-char-anchored, PROBED before suggesting) with
///       variantKind 'tokenForm'|'spelling'; the manifest wording now states exactly what is
///       and is not covered (the field observed the promised "didYouMean" never firing on
///       identifier typos — only token-FORM variants existed);
/// dzi — the elsewhere redirect and didYouMean carry structured samples
///       {path, line, containingSymbol} beside the compat samplePaths (the redirect used to
///       drop exactly the owner context main hits carry).
/// </summary>
public class Batch39Tests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SpellingNearMissSuggestsTheIndexedIdentifier()
    {
        string root = Directory.CreateTempSubdirectory("codenav-39-spell").FullName;
        try
        {
            WriteTextLabWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Deletion typo: 'WdgetFactory' -> WidgetFactory (the field's exact miss class).
            var deletion = Parse(tools.SearchText("WdgetFactory"));
            var dym = deletion.GetProperty("didYouMean");
            Assert.Equal("WidgetFactory", dym.GetProperty("query").GetString());
            Assert.Equal("spelling", dym.GetProperty("variantKind").GetString());
            Assert.True(dym.GetProperty("preciseCount").GetInt32() >= 1);
            // dzi: structured samples with owner context, plus the compat samplePaths.
            var sample = dym.GetProperty("samples").EnumerateArray().First();
            Assert.False(string.IsNullOrEmpty(sample.GetProperty("path").GetString()));
            Assert.True(sample.GetProperty("line").GetInt32() >= 1);
            Assert.True(dym.GetProperty("samplePaths").GetArrayLength() >= 1);
            Assert.Contains("near-miss identifier", deletion.GetProperty("note").GetString());

            // Adjacent transposition: 'WigdetFactory' -> WidgetFactory (Damerau, not plain
            // Levenshtein — a transposition is ONE typo, not two).
            var transposition = Parse(tools.SearchText("WigdetFactory"));
            Assert.Equal("WidgetFactory",
                transposition.GetProperty("didYouMean").GetProperty("query").GetString());

            // A suggestion is NEVER a substitution: the result itself stays a zero.
            Assert.Equal(0, deletion.GetProperty("preciseCount").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void TokenFormVariantOutranksSpelling()
    {
        string root = Directory.CreateTempSubdirectory("codenav-39-rank").FullName;
        try
        {
            WriteTextLabWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // 'Mode4' has BOTH a token-form variant with hits ("Mode 4" in a comment) and a
            // spelling neighbor (class Mode5). Form variants preserve the caller's spelling —
            // they are probed first and must win.
            var dym = Parse(tools.SearchText("Mode4")).GetProperty("didYouMean");
            Assert.Equal("tokenForm", dym.GetProperty("variantKind").GetString());
            Assert.Equal("Mode 4", dym.GetProperty("query").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NoSuggestionWithoutARealNeighborAndGuardsHold()
    {
        string root = Directory.CreateTempSubdirectory("codenav-39-guards").FullName;
        try
        {
            WriteTextLabWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // No ED-1 neighbor anywhere: honest absence, no didYouMean.
            var none = Parse(tools.SearchText("Zqxjklmv"));
            Assert.False(none.TryGetProperty("didYouMean", out _));
            Assert.Contains("No file contains", none.GetProperty("note").GetString());

            // Sub-4-char guard: 'Abq' is ED-1 from the indexed 'Abz', but 3-char neighborhoods
            // are noise by policy — no suggestion (documented limitation, deliberately pinned).
            var shortTok = Parse(tools.SearchText("Abq"));
            Assert.False(shortTok.TryGetProperty("didYouMean", out _));

            // First-character typo: accepted miss (the scan is first-char-anchored by design;
            // the manifest says so) — 'Xidget'/'XidgetFactory' must not suggest.
            var firstChar = Parse(tools.SearchText("XidgetFactory"));
            Assert.False(firstChar.TryGetProperty("didYouMean", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ElsewhereRedirectCarriesStructuredSamplesWithOwnerContext()
    {
        string root = Directory.CreateTempSubdirectory("codenav-39-elsewhere").FullName;
        try
        {
            WriteTextLabWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Scoped to a glob with no matches: the redirect must say where the text DOES
            // live — now with line + containingSymbol, not just a bare path string.
            var res = Parse(tools.SearchText("GammaTokenXyz", pathGlob: "nowhere/**"));
            Assert.Equal(0, res.GetProperty("preciseCount").GetInt32());
            var elsewhere = res.GetProperty("elsewhere");
            Assert.True(elsewhere.GetProperty("preciseCount").GetInt32() >= 1);
            Assert.True(elsewhere.GetProperty("samplePaths").GetArrayLength() >= 1); // compat kept
            var sample = elsewhere.GetProperty("samples").EnumerateArray().First();
            Assert.StartsWith("TextLab", sample.GetProperty("path").GetString());
            Assert.True(sample.GetProperty("line").GetInt32() > 1);
            Assert.Contains("Gamma", sample.GetProperty("containingSymbol").GetString());
        }
        finally { Cleanup(root); }
    }

    // Core-level contract for the metric itself: Damerau semantics, case-insensitivity,
    // ordering by declaration count, and the guards — independent of the MCP plumbing.
    [Fact]
    public void NearMissSymbolNamesHonorDamerauSemanticsAndOrdering()
    {
        string root = Directory.CreateTempSubdirectory("codenav-39-core").FullName;
        try
        {
            WriteTextLabWorkspace(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);

            Assert.Contains("WidgetFactory", q.NearMissSymbolNames("WdgetFactory"));   // deletion
            Assert.Contains("WidgetFactory", q.NearMissSymbolNames("WidgetsFactory")); // insertion
            Assert.Contains("WidgetFactory", q.NearMissSymbolNames("WigdetFactory"));  // transposition
            Assert.Contains("WidgetFactory", q.NearMissSymbolNames("WidgetFactorx"));  // substitution
            Assert.Contains("WidgetFactory", q.NearMissSymbolNames("wdgetfactory"));   // case-insensitive
            // The identical name (any case) is never a suggestion — FTS is case-insensitive,
            // so it could not have been the reason for the zero.
            Assert.DoesNotContain("WidgetFactory", q.NearMissSymbolNames("widgetfactory"));
            Assert.Empty(q.NearMissSymbolNames("Abq"));           // sub-4-char guard
            Assert.Empty(q.NearMissSymbolNames("XidgetFactory")); // first-char anchor (accepted miss)
            Assert.Empty(q.NearMissSymbolNames("WdgtFactory"));   // ED 2 — out of reach by design
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>TextLab: WidgetFactory (the spelling target, declared + used), a "Mode 4"
    /// comment + class Mode5 (form-vs-spelling ranking), Abz (short-token guard bait), and
    /// GammaTokenXyz inside a method (elsewhere owner context).</summary>
    private static void WriteTextLabWorkspace(string root)
    {
        string dir = Path.Combine(root, "TextLab");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TextLab.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "WidgetFactory.cs"),
            """
            namespace TextLab
            {
                public class WidgetFactory
                {
                    public void Build() { }
                }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Usage.cs"),
            """
            namespace TextLab
            {
                public class Usage
                {
                    public WidgetFactory? F;
                    public void Run()
                    {
                        // Mode 4 enabled for legacy partners
                    }
                }
                public class Mode5 { }
                public class Abz { }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Gamma.cs"),
            """
            namespace TextLab
            {
                public class GammaHost
                {
                    public string Serve()
                    {
                        return "GammaTokenXyz";
                    }
                }
            }
            """);
    }

    private static IndexManager BuildAndStart(string root)
    {
        string dbPath = IndexBuilder.DefaultDbPath(root);
        IndexBuilder.Build(root, dbPath);
        var m = new IndexManager(root, dbPath);
        m.Start();
        Assert.True(WaitUntil(() => m.IsQueryable, 20000));
        return m;
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
