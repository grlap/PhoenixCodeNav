using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Coverage for feedback batch 6: PhoenixCodeNav-kab (definition includeBody),
/// -r2o (references nudge + confidence notes), -zb9 (search_text containingSymbol),
/// -gm8 (outline partialFiles, source_context budget hint).
/// </summary>
public class Batch6FeedbackTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexFixture _fx;
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;

    public Batch6FeedbackTests(IndexFixture fx)
    {
        _fx = fx;
        _manager = new IndexManager(_fx.Root, _fx.DbPath);
        _manager.Start();
        for (int i = 0; i < 600 && !_manager.IsQueryable; i++) Thread.Sleep(50); // 30s: the 5s wait was the suite-wide startup-starvation flake class
        _semantic = new SemanticService(_manager);
    }

    public void Dispose()
    {
        _semantic.Dispose();
        _manager.Dispose();
    }

    private NavigationTools Tools() => new(_manager, _semantic);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Writes a temp file into an existing project dir, refreshes, runs the body,
    /// then deletes + refreshes back — the established fixture-mutation pattern.</summary>
    private void WithTempFile(string fileName, string content, Action<string> body)
    {
        using var q0 = _manager.OpenQueries();
        var anyCs = q0.FindFiles("*.cs", 1).Single();
        string dir = Path.GetDirectoryName(anyCs.Path)!.Replace('\\', '/');
        string rel = $"{dir}/{fileName}";
        string full = Path.Combine(_fx.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(full, content);
        try
        {
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel },
                q => q.ContentByPath(rel) == content,
                $"the temporary fixture {rel} was not indexed");
            body(rel);
        }
        finally
        {
            File.Delete(full);
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel },
                q => q.ContentByPath(rel) is null,
                $"the deleted temporary fixture {rel} remained indexed");
        }
    }

    // ---------------------------------------------------------------- kab: includeBody

    [Fact]
    public void DefinitionIncludeBodyReturnsInlineSource()
    {
        var tools = Tools();

        // Default: no body (null-omitted).
        var plain = Parse(tools.Definition("Guard", kinds: "class"));
        Assert.False(plain.TryGetProperty("body", out var b0) && b0.ValueKind != JsonValueKind.Null);

        // includeBody: numbered source of the primary declaration, inline.
        var withBody = Parse(tools.Definition("Guard", kinds: "class", includeBody: true));
        var bodyEl = withBody.GetProperty("body");
        Assert.EndsWith("Guard.cs", bodyEl.GetProperty("path").GetString());
        string source = bodyEl.GetProperty("source").GetString()!;
        Assert.Contains("Guard", source);
        Assert.Contains("NotNull", source);      // the member — proves the span covers the body
        Assert.Contains("|", source);            // numbered lines, source_context format
        Assert.False(bodyEl.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void DefinitionIncludeBodyWorksOnBothPaths()
    {
        var tools = Tools();

        // Indexed path: spans come from the index, so content is index-backed.
        var indexed = Parse(tools.Definition("Guard", kinds: "class", mode: "indexed", includeBody: true));
        var iBody = indexed.GetProperty("body");
        Assert.Equal("index", iBody.GetProperty("freshness").GetString());
        Assert.Contains("NotNull", iBody.GetProperty("source").GetString());

        // Semantic path: spans come from live sources, so the body reads the live file.
        var semantic = Parse(tools.Definition("Guard", kinds: "class", mode: "semantic", includeBody: true, timeoutMs: 30000));
        if (semantic.TryGetProperty("error", out var err))
        {
            // Semantic genuinely unavailable in this fixture — assert the honest error shape,
            // not a silent skip that lets a live-freshness regression pass unnoticed.
            Assert.Equal("semantic_unavailable", err.GetString());
        }
        else
        {
            var sBody = semantic.GetProperty("body");
            Assert.Equal("live", sBody.GetProperty("freshness").GetString());
            Assert.Contains("NotNull", sBody.GetProperty("source").GetString());
        }
    }

    [Fact]
    public void DefinitionBodyStaysUnderHardByteBudget()
    {
        // Escaping-dense AND non-ASCII body: quotes/backslashes still escape under the relaxed
        // encoder, and CJK chars are 1 UTF-16 unit but 3 UTF-8 bytes — so a char-counting budget
        // would under-count wire bytes. The response must fit the cap in BYTES. This test fails
        // under the pre-fix char-based budget.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("namespace Zeb.Esc {");
        sb.AppendLine("  public class ZebEscapeType {");
        for (int i = 0; i < 400; i++)
        {
            sb.AppendLine($"    // \"引用\" \\\\ 反斜杠 これはコメント żółć <T> && 'q' + {i}");
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");

        WithTempFile("ZebEscapeType.cs", sb.ToString(), rel =>
        {
            var tools = Tools();
            string raw = tools.Definition("ZebEscapeType", includeBody: true, bodyMaxBytes: 16384);
            Assert.True(Json.Utf8Bytes(raw) <= Json.HardBudgetBytes,
                $"definition(includeBody) is {Json.Utf8Bytes(raw)} UTF-8 bytes, cap {Json.HardBudgetBytes}");
            Assert.Contains("ZebEscapeType", raw);
            // The body is actually present and was truncated — proves the shrink path ran, not
            // that the response was trivially small.
            var body = Parse(raw).GetProperty("body");
            Assert.False(body.TryGetProperty("omitted", out _));
            Assert.True(body.GetProperty("truncated").GetBoolean());
        });
    }

    [Fact]
    public void WithListBudgetIsIdempotentAcrossRepeatedCalls()
    {
        // Directly pins the copy fix: a build whose serialized size forces a shrink, called
        // twice on the SAME input list, must return an identical result and leave the input
        // untouched. Before the copy, the 2nd call operated on the already-gutted list.
        var items = Enumerable.Range(0, 200).Select(i => $"item-{i}-{new string('x', 200)}").ToList();
        object Build(List<string> its, bool trunc) => new { data = its, truncated = trunc };

        string first = Json.WithListBudget(items, Build);
        Assert.Equal(200, items.Count);                         // input list unmutated (it was copied)
        string second = Json.WithListBudget(items, Build);
        Assert.Equal(first, second);                            // idempotent
        Assert.True(Json.Utf8Bytes(first) <= Json.HardBudgetBytes);
        Assert.Contains("\"truncated\":true", first);           // it genuinely shrank
    }

    [Fact]
    public void WithListBudgetCanDropOneOversizedItemToZero()
    {
        var items = new List<string> { new('\\', Json.HardBudgetBytes) };
        object Build(List<string> its, bool trunc) => new { data = its, truncated = trunc };

        string json = Json.WithListBudget(items, Build);

        Assert.True(Json.Utf8Bytes(json) <= Json.HardBudgetBytes);
        JsonElement response = JsonDocument.Parse(json).RootElement;
        Assert.Empty(response.GetProperty("data").EnumerateArray());
        Assert.True(response.GetProperty("truncated").GetBoolean());
        Assert.Single(items); // the caller's list is still untouched
    }

    [Fact]
    public void IndexWorktreeResultBoundsDynamicPathAndDetail()
    {
        string path = "C:/" + new string('\\', Json.HardBudgetBytes) +
                      new string('é', Json.HardBudgetBytes);
        string detail = new string('d', Json.HardBudgetBytes * 2);
        var result = new WorktreeIndexResult("worktree_not_found", detail,
            0, 0, 0, 7, null, false);
        var meta = new Meta("ready", "14", "now", "now", 0,
            "indexed", "text", Build: "0.11.5+test", IndexSchema: "14");

        string json = NavigationTools.SerializeIndexWorktreeResult(path, result, meta);

        Assert.True(Json.Utf8Bytes(json) <= Json.HardBudgetBytes,
            $"worktree result used {Json.Utf8Bytes(json)} bytes");
        JsonElement response = JsonDocument.Parse(json).RootElement;
        Assert.Equal("worktree_not_found", response.GetProperty("error").GetString());
        Assert.True(response.GetProperty("pathTruncated").GetBoolean());
        Assert.Equal(Json.Utf8Bytes(path), response.GetProperty("pathBytes").GetInt32());
        Assert.True(response.GetProperty("detailTruncated").GetBoolean());
        Assert.Equal(Json.Utf8Bytes(detail), response.GetProperty("detailBytes").GetInt32());
        Assert.Equal("ready", response.GetProperty("meta").GetProperty("indexStatus").GetString());
    }

    [Fact]
    public void DefinitionBodyRespectsByteBudgetAndHints()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("namespace Zeb.Big {");
        sb.AppendLine("  public class ZebBigType {");
        for (int i = 0; i < 120; i++)
        {
            sb.AppendLine($"    public int ZebBigMethodNumber{i}(int alpha) {{ return alpha + {i}; }}");
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");

        WithTempFile("ZebBigType.cs", sb.ToString(), _ =>
        {
            var tools = Tools();
            var json = Parse(tools.Definition("ZebBigType", includeBody: true, bodyMaxBytes: 512));
            var body = json.GetProperty("body");
            Assert.True(body.GetProperty("truncated").GetBoolean(), "120-line body must truncate at 512 bytes");
            Assert.Contains("source_context", body.GetProperty("hint").GetString());
            // Budget respected with headroom for JSON escaping.
            Assert.True(body.GetProperty("source").GetString()!.Length <= 1024);
        });
    }

    // ---------------------------------------------------------------- r2o: steering

    [Fact]
    public void SearchSymbolCarriesNextStepHint()
    {
        var tools = Tools();
        var json = Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact"));
        Assert.Contains("references", json.GetProperty("hint").GetString());
        Assert.Contains("includeBody", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void ConfidenceNoteIsContextualNotVerbatim()
    {
        // 47t (field: the indexed explainer repeated on EVERY response was noise): plain indexed
        // responses carry NO confidenceNote — the tier meanings live in server_capabilities'
        // confidenceModel, read once. It also carries the inline build stamp (ddp).
        var tools = Tools();
        var meta = Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact")).GetProperty("meta");
        Assert.Equal("indexed", meta.GetProperty("confidence").GetString());
        Assert.False(meta.TryGetProperty("confidenceNote", out _));
        // Pin the stamp's COMPOSITION, not Stamp-vs-itself (review: the self-referential assert let a
        // static-init ordering bug ship "0.5.0+" with the commit dropped, at 200 tests green).
        Assert.Equal($"{BuildInfo.Version}+{BuildInfo.Commit}", meta.GetProperty("build").GetString());
        Assert.EndsWith(BuildInfo.Commit, meta.GetProperty("build").GetString());

        // Heuristic responses STILL explain themselves — that warning changes how much to trust
        // this specific result.
        var hMeta = Parse(tools.RelatedTests("Guard")).GetProperty("meta");
        Assert.Equal("heuristic", hMeta.GetProperty("confidence").GetString());
        Assert.False(string.IsNullOrEmpty(hMeta.GetProperty("confidenceNote").GetString()));
    }

    // 9z4 (field: couldn't tell if 'refreshing' meant "results wrong" or "background catch-up"):
    // refreshing/stale statuses carry a one-line meaning; plain ready does not.
    [Fact]
    public void StatusNoteExplainsRefreshingAndStale()
    {
        var ready = new IndexHealth("ready", "v", null, null, 0, null, 0, "w", "d");
        Assert.False(string.IsNullOrEmpty(Meta.From(ready, "indexed", "text").IndexStatus));
        Assert.Null(Meta.From(ready, "indexed", "text").StatusNote);

        var stale = Meta.From(ready with { PendingChanges = 3 }, "indexed", "text");
        Assert.Equal("stale", stale.IndexStatus);
        Assert.Contains("pending (3)", stale.StatusNote);

        var refreshing = Meta.From(ready with { State = "refreshing" }, "indexed", "text");
        Assert.Equal("refreshing", refreshing.IndexStatus);
        Assert.Contains("non-blocking", refreshing.StatusNote);
    }

    // ---------------------------------------------------------------- zb9: containingSymbol

    [Fact]
    public void SearchTextHitsCarryContainingSymbol()
    {
        WithTempFile("ZebOwner.cs",
            "namespace Zeb.Own\n{\n    public class ZebOwnerType\n    {\n" +
            "        public void ZebOwnerMethod()\n        {\n" +
            "            var zebraOwnershipMarker = 1;\n            _ = zebraOwnershipMarker;\n" +
            "        }\n    }\n}\n",
            _ =>
            {
                var tools = Tools();
                var json = Parse(tools.SearchText("zebraOwnershipMarker"));
                var hits = json.GetProperty("hits").EnumerateArray().ToList();
                Assert.NotEmpty(hits);
                // The hit line lives inside ZebOwnerMethod — owner is Type.Method.
                Assert.Contains(hits, h =>
                    h.TryGetProperty("containingSymbol", out var cs) &&
                    cs.GetString() == "ZebOwnerType.ZebOwnerMethod");
            });
    }

    // ---------------------------------------------------------------- gm8: nits

    [Fact]
    public void OutlineListsPartialSiblingFiles()
    {
        using var q0 = _manager.OpenQueries();
        var anyCs = q0.FindFiles("*.cs", 1).Single();
        string dir = Path.GetDirectoryName(anyCs.Path)!.Replace('\\', '/');
        string rel1 = $"{dir}/ZebPartialA.cs";
        string rel2 = $"{dir}/ZebPartialB.cs";
        string rel3 = $"{dir}/ZebPartialDecoy.cs";
        string full1 = Path.Combine(_fx.Root, rel1.Replace('/', Path.DirectorySeparatorChar));
        string full2 = Path.Combine(_fx.Root, rel2.Replace('/', Path.DirectorySeparatorChar));
        string full3 = Path.Combine(_fx.Root, rel3.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(full1, "namespace Zeb.Part { public partial class ZebPartialType { public void A() { } } }");
        File.WriteAllText(full2, "namespace Zeb.Part { public partial class ZebPartialType { public void B() { } } }");
        // Decoys: same-name NON-partial type in another namespace, and a same-name PARTIAL
        // nested inside a container — neither is a declaration of the same type.
        File.WriteAllText(full3,
            "namespace Zeb.Other { public class ZebPartialType { } }\n" +
            "namespace Zeb.Part { public partial class ZebHost { public partial class ZebPartialType { } } }\n");
        try
        {
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel1, rel2, rel3 },
                q => q.ContentByPath(rel1) is not null &&
                     q.ContentByPath(rel2) is not null &&
                     q.ContentByPath(rel3) is not null,
                "the partial-type fixtures were not indexed");

            var tools = Tools();
            // Prove the decoy file actually indexed — otherwise the DoesNotContain below is vacuous.
            string decoyOutline = tools.Outline(rel3, depth: 2);
            Assert.Contains("ZebHost", decoyOutline);
            Assert.Contains("ZebPartialType", decoyOutline);

            string raw = tools.Outline(rel1, depth: 1);
            // The sibling declaration file is cross-linked on the partial type node...
            Assert.Contains("\"isPartial\":true", raw);
            Assert.Contains(rel2, raw);
            // ...but the decoy file (different identity: other ns / nested container) is not.
            Assert.DoesNotContain(rel3, raw);
            // And the other direction agrees.
            Assert.Contains(rel1, tools.Outline(rel2, depth: 1));
        }
        finally
        {
            File.Delete(full1);
            File.Delete(full2);
            File.Delete(full3);
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel1, rel2, rel3 },
                q => q.ContentByPath(rel1) is null &&
                     q.ContentByPath(rel2) is null &&
                     q.ContentByPath(rel3) is null,
                "the deleted partial-type fixtures remained indexed");
        }
    }

    [Fact]
    public void SteeringHintsAreScopedToFirstPageAndCsHits()
    {
        var tools = Tools();

        // Empty result: no steering hint.
        var empty = Parse(tools.SearchSymbol("NoSuchSymbolZz123", match: "exact"));
        Assert.False(empty.TryGetProperty("hint", out var h0) && h0.ValueKind != JsonValueKind.Null);

        // Cursored page: no steering hint (first page only).
        var page1 = Parse(tools.SearchSymbol("C", match: "substring", limit: 3));
        string? cursor = page1.GetProperty("nextCursor").GetString();
        if (cursor is not null)
        {
            var page2 = Parse(tools.SearchSymbol("C", match: "substring", limit: 3, cursor: cursor));
            Assert.False(page2.TryGetProperty("hint", out var h1) && h1.ValueKind != JsonValueKind.Null);
        }

        // Non-.cs hits (config) carry no containingSymbol.
        var cfg = Parse(tools.SearchText("repositoryPath", lang: "config"));
        foreach (var hit in cfg.GetProperty("hits").EnumerateArray())
        {
            Assert.False(hit.TryGetProperty("containingSymbol", out var cs) && cs.ValueKind != JsonValueKind.Null);
        }
    }

    [Fact]
    public void SourceContextTruncationCarriesBudgetHint()
    {
        var tools = Tools();
        using var q = _manager.OpenQueries();
        var guardFile = q.FindFiles("Guard.cs", 1).Single();

        var json = Parse(tools.SourceContext(guardFile.Path, "1-400", contextLines: 0, maxBytes: 256));
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.Contains("maxBytes", json.GetProperty("hint").GetString());
        // Whole response fits the hard byte budget even when the raw request would not.
        Assert.True(Json.Utf8Bytes(tools.SourceContext(guardFile.Path, "1-400", maxBytes: Json.HardBudgetBytes)) <= Json.HardBudgetBytes);

        // A span entirely past EOF yields no span — not an inverted {startLine>endLine} range.
        var beyond = Parse(tools.SourceContext(guardFile.Path, "100000-100010", contextLines: 0));
        Assert.Empty(beyond.GetProperty("spans").EnumerateArray());
    }
}
