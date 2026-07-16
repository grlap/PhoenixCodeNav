using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Batch 8: accepting the idx:NNN symbolId handle (emitted on every search_symbol / symbol_at /
/// definition result) as an INPUT to definition / references / source_context, so an agent can act
/// on an exact declaration without re-resolving a name or an overload. Uses the shared 40-project
/// fixture (Guard is a stable single class).
/// </summary>
[Collection(SharedIndexCollection.Name)]
public class Batch8SymbolIdTests
{
    private readonly IndexFixture _fx;

    public Batch8SymbolIdTests(SharedIndexFixture fx) => _fx = fx;

    // One shared writer per class fixture: a manager per test (never disposed by xUnit) would
    // leave later tests as read-only followers, unable to perform fixture refreshes.
    private NavigationTools Tools() => _fx.SharedTools;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>A live idx: handle for the single Guard class, plus its declaration path/line.</summary>
    private static (string SymbolId, string Path, int StartLine) GuardHandle(NavigationTools tools)
    {
        var hit = Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact")).GetProperty("symbols")[0];
        string id = hit.GetProperty("symbolId").GetString()!;
        Assert.StartsWith("idx:", id);
        return (id, hit.GetProperty("path").GetString()!, hit.GetProperty("startLine").GetInt32());
    }

    [Fact]
    public void SymbolIdResolvesDefinition()
    {
        var tools = Tools();
        var (id, _, _) = GuardHandle(tools);

        string raw = tools.Definition(symbolId: id, mode: "indexed");
        Assert.DoesNotContain("\"error\"", raw);
        Assert.Contains("Guard", raw);
    }

    [Fact]
    public void SymbolIdResolvesSourceContextToTheSymbolSpan()
    {
        var tools = Tools();
        var (id, path, startLine) = GuardHandle(tools);

        var ctx = Parse(tools.SourceContext(symbolId: id, contextLines: 0));
        Assert.False(ctx.TryGetProperty("error", out _));
        var spans = ctx.GetProperty("spans");
        Assert.True(spans.GetArrayLength() >= 1);
        string source = spans[0].GetProperty("source").GetString()!;
        // The span is the symbol's own declaration — its first numbered line is the symbol start.
        Assert.Contains($"{startLine,5}|", source);
        Assert.Contains("Guard", source);
    }

    [Fact]
    public void SymbolIdResolvesReferences()
    {
        var tools = Tools();
        var (id, _, _) = GuardHandle(tools);

        var refs = Parse(tools.References(symbolId: id, mode: "indexed"));
        Assert.False(refs.TryGetProperty("error", out _));
        Assert.True(refs.GetProperty("totalCandidates").GetInt32() >= 1);
    }

    [Fact]
    public void SymbolIdTakesPrecedenceOverName()
    {
        var tools = Tools();
        var (id, _, _) = GuardHandle(tools);

        // A bogus name alongside a valid idx: the handle wins, so it still resolves Guard.
        string raw = tools.Definition(symbolId: id, name: "ZzDefinitelyNotARealSymbol", mode: "indexed");
        Assert.DoesNotContain("\"error\"", raw);
        Assert.Contains("Guard", raw);
    }

    [Fact]
    public void UnsupportedOrStaleSymbolIdHandlesAreRejectedClearly()
    {
        var tools = Tools();

        // A documentationCommentId is not yet accepted as input.
        Assert.Equal("bad_request", Parse(tools.Definition(symbolId: "T:Acme.Platform.Common.Guard")).GetProperty("error").GetString());
        // Garbage handle.
        Assert.Equal("bad_request", Parse(tools.SourceContext(symbolId: "not-a-handle")).GetProperty("error").GetString());
        // Well-formed but stale/out-of-range idx (ids are index-local).
        Assert.Equal("symbol_not_found", Parse(tools.References(symbolId: "idx:999999999")).GetProperty("error").GetString());
    }

    [Fact]
    public void SourceContextStillRequiresATargetWithoutSymbolId()
    {
        var tools = Tools();
        Assert.Equal("bad_request", Parse(tools.SourceContext()).GetProperty("error").GetString());
    }

    // ---- review fixes ----

    // Finding 1: a name hint must match a WHOLE identifier so semantic resolution of an idx: handle
    // on a multi-declarator line (e.g. "Height" alongside "HeightRatio") lands on the right sibling.
    [Fact]
    public void IndexOfWholeIdentifierDoesNotMatchInsideALongerIdentifier()
    {
        const string line = "public int HeightRatio, Height;";
        Assert.Equal(line.LastIndexOf("Height", StringComparison.Ordinal),
            SemanticService.IndexOfWholeIdentifier(line, "Height"));      // the standalone declarator
        Assert.Equal(line.IndexOf("HeightRatio", StringComparison.Ordinal),
            SemanticService.IndexOfWholeIdentifier(line, "HeightRatio"));
        Assert.Equal(-1, SemanticService.IndexOfWholeIdentifier(line, "eight"));   // only ever a substring
        Assert.Equal(-1, SemanticService.IndexOfWholeIdentifier("", "Height"));
    }

    // Finding 2: caller kinds/container filters exist to narrow a bare name; a symbolId handle has
    // already disambiguated, so a mismatched filter must not suppress the resolved declaration.
    [Fact]
    public void SymbolIdIgnoresMismatchedKindsFilter()
    {
        var tools = Tools();
        var (id, _, _) = GuardHandle(tools); // Guard is a class
        var decls = Parse(tools.Definition(symbolId: id, mode: "indexed", kinds: "method"))
            .GetProperty("declarations");
        Assert.True(decls.GetArrayLength() >= 1);
        Assert.Contains(decls.EnumerateArray(),
            d => d.GetProperty("name").GetString() == "Guard" && d.GetProperty("kind").GetString() == "class");
    }

    // fkv: the handle carries an identity fingerprint so a rowid the index reused for a different
    // symbol is detected (stale_handle) instead of resolving silently to the wrong symbol.
    [Fact]
    public void IdxHandleCarriesFingerprintAndDetectsTampering()
    {
        var tools = Tools();
        var (id, _, _) = GuardHandle(tools);
        int tilde = id.IndexOf('~');
        Assert.True(tilde > 0, "emitted idx handle should carry a ~fingerprint");

        // A fingerprint that no longer matches the row (as if the rowid were reused) is refused.
        Assert.Equal("stale_handle",
            Parse(tools.Definition(symbolId: id[..tilde] + "~deadbeef")).GetProperty("error").GetString());

        // A bare idx:N (no fingerprint — e.g. hand-typed) still resolves best-effort.
        string raw = tools.Definition(symbolId: id[..tilde], mode: "indexed");
        Assert.DoesNotContain("\"error\"", raw);
        Assert.Contains("Guard", raw);
    }
}
