using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

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
                // 23h/tzj: coverage + narrowing transparency; xyy: silent note on a clean success.
                Assert.Contains("public", pw.GetProperty("narrowedOn").EnumerateArray().Select(e => e.GetString()));
                Assert.True(pw.GetProperty("filesTotal").GetInt32() >= 1);
                Assert.False(pw.TryGetProperty("budgetHit", out _)); // full coverage -> omitted
                Assert.False(pw.TryGetProperty("note", out _));      // nothing to steer -> omitted

                // edge-touching literal pattern is NOT narrowable (would false-negative), but full scan finds it
                var ps = Parse(tools.SearchText("public\\s+static", regex: true, pathGlob: "RxTarget.cs"));
                Assert.False(ps.GetProperty("narrowed").GetBoolean());
                Assert.Equal(2, ps.GetProperty("matchCount").GetInt32());
                Assert.False(ps.TryGetProperty("narrowedOn", out _)); // scan mode -> no literals to report
                Assert.Equal(1, ps.GetProperty("filesTotal").GetInt32()); // pathGlob scoped to exactly 1 file

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

                // case sensitivity: PUBLIC (no flag) misses; (?i) hits. Zero-hit carries the line-based/
                // case-sensitivity reminder note (ajv/xyy: contextual, only when it changes the next move).
                var caps = Parse(tools.SearchText("PUBLIC", regex: true, pathGlob: "RxTarget.cs"));
                Assert.Equal(0, caps.GetProperty("matchCount").GetInt32());
                Assert.Contains("LINE-BASED", caps.GetProperty("note").GetString());
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
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    // ks6: the ReDoS guards must be regression-tested, not just review-verified. (1) A catastrophic
    // pattern trips the per-match timeout (RegexMatchTimeoutException -> timedOut) instead of hanging.
    // (2) The overall wall-clock budget is checked PER LINE, so a ~zero budget aborts mid-scan.
    [Fact]
    public void RedosAndBudgetGuardsTimeOutInsteadOfHanging()
    {
        string root = Directory.CreateTempSubdirectory("codenav-redos").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 12);
            // Catastrophic-backtracking bait: a long 'a' run that (a+)+$ cannot match (trailing '!').
            File.WriteAllText(Path.Combine(root, "Redos.cs"),
                "namespace R { class C { /* " + new string('a', 80) + "! */ } }");
            // Budget bait: enough lines (~100k, one big file) that even a cheap non-matching scan takes
            // >= 1ms, so the PER-LINE budget check provably fires mid-file on a ~zero budget. (5k lines
            // scanned in under a millisecond — the review's 'between-files-only' hole needs a big file.)
            File.WriteAllText(Path.Combine(root, "Budget.cs"),
                "namespace B { class C {\n" + string.Concat(Enumerable.Repeat("// pad line\n", 100_000)) + "} }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var q = new IndexQueries(dbPath);
            // (a|aa)+$ — an alternation-headed loop with overlapping branches; .NET's auto-atomicity
            // cannot rewrite it (unlike (a+)+$, which the optimizer makes linear), so a long 'a' run
            // with a non-matching tail genuinely explodes and must be stopped by the per-match timeout.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var redos = q.SearchRegex("(a|aa)+$", null, 300, 0, 20, 0, 0, totalBudgetMs: 30000, perMatchMs: 60);
            sw.Stop();
            Assert.True(redos.TimedOut, "catastrophic pattern did not trip the per-match ReDoS guard");
            Assert.True(sw.ElapsedMilliseconds < 15000, $"ReDoS guard too slow: {sw.ElapsedMilliseconds}ms");

            var budget = q.SearchRegex("neverMatchesAnythingZz", null, 300, 0, 20, 0, 0, totalBudgetMs: 0, perMatchMs: 250);
            Assert.True(budget.TimedOut, "per-line budget check did not fire on a ~zero budget");
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }
}
