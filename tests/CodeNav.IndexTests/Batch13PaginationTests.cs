using System.Text;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Pagination correctness (bugs e2q + cli). e2q: the byte-budget shrink drops a page's tail (keeping a
/// prefix), so nextCursor must resume at offset + the count actually RETURNED, not a fixed offset+limit,
/// or the dropped hits are skipped forever. cli: search_symbol auto-mode must continue the match mode
/// resolved on page 1 on later pages — re-running the exact->prefix->substring ladder from a non-zero
/// offset returns an empty page because the fallback is gated to offset==0.
/// </summary>
[Collection(SharedIndexCollection.Name)]
public class Batch13PaginationTests
{
    private readonly NavigationTools _tools;

    public Batch13PaginationTests(SharedIndexFixture fx)
    {
        _tools = fx.SharedTools;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // e2q, end-to-end through the real tool: search_text with large context forces a page to exceed the
    // 64KB hard cap, so WithListBudget shrinks it (truncated) mid-walk. The walk must still visit EVERY
    // hit exactly once. With the pre-fix cursor (offset+limit), the shrunk-off tail is skipped and this
    // fails. (A prior version asserted an inline copy of the formula and could not catch a reversion.)
    [Fact]
    public void SearchTextPaginationCoversEveryHitUnderByteShrink()
    {
        string root = Directory.CreateTempSubdirectory("codenav-e2q").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 7);
            const int hitCount = 60;
            var sb = new StringBuilder();
            sb.AppendLine("namespace E2Q { class Big {");
            for (int i = 0; i < hitCount; i++)
                sb.AppendLine($"    // MATCHME line {i} " + new string('x', 400)); // long lines -> big context
            sb.AppendLine("} }");
            File.WriteAllText(Path.Combine(root, "Big.cs"), sb.ToString());

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            var manager = new IndexManager(root, dbPath);
            try
            {
                manager.Start();
                for (int i = 0; i < 600 && !manager.IsQueryable; i++) Thread.Sleep(50); // 30s: the 5s wait was the suite-wide startup-starvation flake class
                Assert.True(manager.IsQueryable);
                var tools = new NavigationTools(manager, new SemanticService(manager));

                int total = Parse(tools.SearchText("MATCHME", limit: 20)).GetProperty("preciseCount").GetInt32();
                Assert.True(total >= hitCount, $"expected >= {hitCount} precise hits, got {total}");

                var seen = new HashSet<int>();
                string? cursor = null;
                bool sawShrink = false;
                for (int guard = 0; guard < total + 20; guard++)
                {
                    var page = Parse(tools.SearchText("MATCHME", limit: 20, context: 15, cursor: cursor));
                    foreach (var h in page.GetProperty("hits").EnumerateArray())
                        seen.Add(h.GetProperty("line").GetInt32());
                    if (page.TryGetProperty("truncated", out var tr) && tr.GetBoolean()) sawShrink = true;
                    if (!page.TryGetProperty("nextCursor", out var nc) || nc.ValueKind == JsonValueKind.Null) break;
                    cursor = nc.GetString();
                }

                Assert.True(sawShrink, "test did not force a byte-budget shrink — it would not guard e2q");
                Assert.Equal(total, seen.Count); // every hit visited exactly once despite mid-page shrink
            }
            finally { manager.Dispose(); }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    // cli: auto that resolves to prefix on page 1 must continue in prefix on page 2 — not restart at
    // exact (which, gated to offset==0, returns an empty page and loses the prefix results).
    [Fact]
    public void SearchSymbolAutoPaginationContinuesResolvedMode()
    {
        var p1 = Parse(_tools.SearchSymbol("I", match: "auto", limit: 2));
        // Fixture invariant: many 'I'-prefixed interfaces, no symbol named exactly "I" -> auto resolves prefix.
        Assert.Equal("prefix", p1.GetProperty("matchMode").GetString());
        string? cursor = p1.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor); // more interface matches exist beyond page 1

        var p2 = Parse(_tools.SearchSymbol("I", match: "auto", limit: 2, cursor: cursor));
        Assert.Equal("prefix", p2.GetProperty("matchMode").GetString()); // continued, not restarted at exact
        Assert.True(p2.GetProperty("symbols").GetArrayLength() > 0, "page 2 empty — auto ladder restarted (bug cli)");
    }

    // End-to-end continuity for the NON-truncated path: walking every page returns each result exactly
    // once (no empty restart from cli, no dup). The byte-shrink (e2q) path is covered by the test above.
    [Fact]
    public void SearchSymbolPaginationVisitsEveryResultOnce()
    {
        var all = Parse(_tools.SearchSymbol("I", match: "prefix", limit: 100));
        if (all.TryGetProperty("nextCursor", out _)) return; // baseline itself paged/truncated — no clean total
        int total = all.GetProperty("symbols").GetArrayLength();
        if (total < 4) return; // need enough to span several pages

        int seen = 0;
        string? cursor = null;
        for (int guard = 0; guard <= total + 5; guard++)
        {
            var page = Parse(_tools.SearchSymbol("I", match: "prefix", limit: 3, cursor: cursor));
            seen += page.GetProperty("symbols").GetArrayLength();
            if (!page.TryGetProperty("nextCursor", out var nc) || nc.ValueKind == JsonValueKind.Null) break;
            cursor = nc.GetString();
        }
        Assert.Equal(total, seen); // every result seen exactly once — no skip, no duplicate
    }
}
