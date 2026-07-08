using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// search_text regex mode (Batch B). Covers the conservative required-literal extraction that drives
/// FTS pre-narrowing (must NEVER over-narrow, or it drops real matches) and the end-to-end regex path:
/// real .NET-regex matching, literal-narrow vs full-scan, case sensitivity, invalid-pattern handling,
/// and context lines.
/// </summary>
public class Batch16RegexSearchTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // Only literals bounded by REAL subject-side separators on BOTH sides are whole tokens. Pattern
    // edges and ^/$ do NOT count (the match is unanchored), so 'public' in 'public\s+static' could sit
    // inside 'xmypublic' -> extracting it would drop that file from FTS (a false negative).
    [Theory]
    [InlineData(@"\bpublic\b", new[] { "public" })]                          // \b both sides -> sound
    [InlineData(@"\bCreate\b", new[] { "create" })]
    [InlineData(@"\bpublic\b\s+\bstatic\b", new[] { "public", "static" })]
    [InlineData(@"public\s+static", new string[0])]                          // edge-touching: NOT whole tokens
    [InlineData(@"InterfaceBase\.Create", new string[0])]                    // edge-touching at both ends
    [InlineData(@"foo bar", new string[0])]
    [InlineData(@"foo\B", new string[0])]                                    // \B asserts a WORD neighbor (not a boundary)
    [InlineData(@"Get\w+Client", new string[0])]                             // substring of one identifier token
    [InlineData(@"foo.*bar", new string[0])]
    [InlineData(@"(foo|bar)baz", new string[0])]
    [InlineData(@"public|private", new string[0])]                           // top-level alternation: nothing required
    [InlineData(@"[A-Z]\w+", new string[0])]
    [InlineData(@"\d{4}", new string[0])]
    [InlineData(@"abc?def", new string[0])]
    public void ExtractRequiredIsWholeTokenAndSound(string pattern, string[] expected)
        => Assert.Equal(expected.OrderBy(x => x), RegexLiterals.ExtractRequired(pattern).OrderBy(x => x));

    [Fact]
    public void RegexModeMatchesScopesAndGuards()
    {
        string root = Directory.CreateTempSubdirectory("codenav-rx").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 4);
            File.WriteAllText(Path.Combine(root, "RxTarget.cs"), string.Join("\n", new[]
            {
                "namespace Rx {",
                "  class T {",
                "    public static void Foo() { }",         // matches public\s+static
                "    public    static int Bar;",             // matches (multiple spaces)
                "    int publicstatic = 4321;",              // NO public\s+static (no gap); has 4 digits
                "    void M() { var c = GetFooClient(); }",  // matches Get\w+Client
                "  }",
                "}",
            }));
            // Soundness (reviewer Finding 2): 'public static' occurs only INSIDE 'zzpublic static' here,
            // so the file's FTS tokens are {zzpublic, static, ...} — no standalone 'public'. A regex
            // public\s+static must still FIND it (full scan); narrowing on a non-whole 'public' would drop it.
            File.WriteAllText(Path.Combine(root, "EdgeCase.cs"),
                "namespace E { class C { void M() { /* zzpublic static */ } } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            var manager = new IndexManager(root, dbPath);
            try
            {
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable);
                var tools = new NavigationTools(manager, new SemanticService(manager));

                // \b-bounded literal -> SOUND FTS narrowing; matches 'public' as a whole word on the two
                // 'public static' lines, not inside 'publicstatic'.
                var pw = Parse(tools.SearchText("\\bpublic\\b", regex: true, pathGlob: "RxTarget.cs"));
                Assert.Equal("regex", pw.GetProperty("mode").GetString());
                Assert.True(pw.GetProperty("narrowed").GetBoolean());
                Assert.Equal(2, pw.GetProperty("matchCount").GetInt32());

                // edge-touching literal pattern is NOT narrowable (would false-negative), but full scan finds it
                var ps = Parse(tools.SearchText("public\\s+static", regex: true, pathGlob: "RxTarget.cs"));
                Assert.False(ps.GetProperty("narrowed").GetBoolean());
                Assert.Equal(2, ps.GetProperty("matchCount").GetInt32());

                // SOUNDNESS GUARD (Finding 2): 'public static' inside 'zzpublic static' must be found —
                // narrowing on a non-whole-token literal would drop this file (0 hits) via FTS AND.
                var edge = Parse(tools.SearchText("public\\s+static", regex: true, pathGlob: "EdgeCase.cs"));
                Assert.False(edge.GetProperty("narrowed").GetBoolean());
                Assert.Equal(1, edge.GetProperty("matchCount").GetInt32());

                // literal-narrowed \w+ pattern
                Assert.True(Parse(tools.SearchText("Get\\w+Client", regex: true, pathGlob: "RxTarget.cs"))
                    .GetProperty("matchCount").GetInt32() >= 1);

                // no-literal pattern -> full scan (narrowed:false), still finds the 4-digit number
                var dig = Parse(tools.SearchText("\\d{4}", regex: true, pathGlob: "RxTarget.cs"));
                Assert.False(dig.GetProperty("narrowed").GetBoolean());
                Assert.True(dig.GetProperty("matchCount").GetInt32() >= 1);

                // case sensitivity: PUBLIC (no flag) misses; (?i) hits
                Assert.Equal(0, Parse(tools.SearchText("PUBLIC", regex: true, pathGlob: "RxTarget.cs"))
                    .GetProperty("matchCount").GetInt32());
                Assert.True(Parse(tools.SearchText("(?i)PUBLIC", regex: true, pathGlob: "RxTarget.cs"))
                    .GetProperty("matchCount").GetInt32() >= 2);

                // invalid regex -> bad_request, no crash
                Assert.Equal("bad_request", Parse(tools.SearchText("(unclosed", regex: true)).GetProperty("error").GetString());

                // context lines work in regex mode
                var ctx = Parse(tools.SearchText("GetFooClient", regex: true, pathGlob: "RxTarget.cs", context: 1));
                var hit = ctx.GetProperty("hits").EnumerateArray().First();
                Assert.True(hit.TryGetProperty("before", out _) || hit.TryGetProperty("after", out _));
            }
            finally { manager.Dispose(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }
}
