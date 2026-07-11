using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Small synthetic workspace with checked-in third-party source under 3rdparty/ (a directory the
/// scanner indexes, unlike bin/obj/packages) plus a first-party user of the same type. Gives the
/// Batch 7 vendor/first-party filters — excludePath, firstPartyOnly, the per-hit noise flag, and
/// repo_overview.suggestedExcludes — something real to discriminate. Its own fixture so the
/// mutation-free tests stay isolated from the shared IndexFixture.
/// </summary>
public sealed class VendorFixture : IDisposable
{
    public const string VendorFile = "3rdparty/AcmeVendor/VendorLib.cs";
    public const string NestedVendorFile = "src/Deep/external/NestedVendor.cs";
    public const string FirstPartyFile = "src/App/PhoenixShared.cs";

    public string Root { get; }
    public string DbPath { get; }

    public VendorFixture()
    {
        Root = Directory.CreateTempSubdirectory("codenav-vendor").FullName;
        WorkspaceGenerator.Generate(Root, targetProjects: 8, seed: 7);

        // Vendored third-party source: a uniquely-named type that also *uses* a first-party type.
        Write(VendorFile,
            "namespace Acme.Vendor\n" +
            "{\n" +
            "    public class PhoenixVendorType\n" +
            "    {\n" +
            "        public void Touch() { var _ = new PhoenixSharedType(); }\n" +
            "    }\n" +
            "}\n");
        // Vendored source nested deep under a non-root 'external/' dir — guards any-depth exclusion.
        Write(NestedVendorFile,
            "namespace Acme.Deep\n" +
            "{\n" +
            "    public class PhoenixNestedVendorType { }\n" +
            "}\n");
        // First-party: declares the shared type and a first-party user of it.
        Write(FirstPartyFile,
            "namespace Acme.App\n" +
            "{\n" +
            "    public class PhoenixSharedType { }\n" +
            "    public class PhoenixSharedUser { public void M() { var _ = new PhoenixSharedType(); } }\n" +
            "}\n");

        DbPath = IndexBuilder.DefaultDbPath(Root);
        IndexBuilder.Build(Root, DbPath);
    }

    public IndexQueries Open() => new(DbPath);

    private readonly object _toolsGate = new();
    private IndexManager? _manager;
    private NavigationTools? _tools;

    /// <summary>
    /// One live IndexManager per fixture instance, created on first use and disposed with the
    /// fixture. The index ownership lease is exclusive per database — a manager per TEST would
    /// leak the lease (xUnit never disposes test-created managers) and starve every subsequent
    /// open of the same db with "another phoenix process owns this index".
    /// </summary>
    public NavigationTools SharedTools
    {
        get
        {
            lock (_toolsGate)
            {
                if (_tools is not null) return _tools;
                var manager = new IndexManager(Root, DbPath);
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable, "index did not become queryable");
                _manager = manager;
                _tools = new NavigationTools(manager, new CodeNav.Core.Semantic.SemanticService(manager));
                return _tools;
            }
        }
    }

    private void Write(string rel, string content)
    {
        string full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch { /* leave temp on Windows lock */ }
    }
}

public class Batch7FilterTests : IClassFixture<VendorFixture>
{
    private readonly VendorFixture _fx;

    public Batch7FilterTests(VendorFixture fx) => _fx = fx;

    // One shared manager per class fixture: the ownership lease is exclusive per database, so a
    // manager per test (never disposed by xUnit) would starve every open after the first.
    private NavigationTools Tools() => _fx.SharedTools;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static IEnumerable<JsonElement> Samples(JsonElement refsResponse) =>
        refsResponse.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("samples").EnumerateArray());

    // ---------------------------------------------------------------- query-layer detection

    [Fact]
    public void IsVendorPathFlagsDirSegmentsOnly()
    {
        Assert.True(IndexQueries.IsVendorPath("3rdparty/AcmeVendor/VendorLib.cs"));
        Assert.True(IndexQueries.IsVendorPath("src/vendor/lib/X.cs"));
        Assert.True(IndexQueries.IsVendorPath("a/External/b/C.cs"));           // case-insensitive
        Assert.False(IndexQueries.IsVendorPath("src/App/PhoenixShared.cs"));   // first-party
        Assert.False(IndexQueries.IsVendorPath("vendor.cs"));                  // file name, not a dir segment
        Assert.False(IndexQueries.IsVendorPath("a/b/generated"));             // trailing segment is the file
        // 'packages' is excluded by the scanner (never indexed) so it is deliberately NOT a marker.
        Assert.False(IndexQueries.IsVendorPath("packages/Foo/Bar.cs"));
    }

    [Fact]
    public void SuggestedExcludesDetectsIndexedVendorDirsAtAnyDepth()
    {
        using var q = _fx.Open();
        var excludes = q.SuggestedExcludes();
        Assert.Contains("3rdparty/**", excludes);            // root-level vendor dir
        Assert.Contains("src/Deep/external/**", excludes);   // nested vendor dir (not a sampled miss)
    }

    [Fact]
    public void VendorExcludeGlobsMatchWhatIsVendorPathFlags()
    {
        // firstPartyOnly's globs must cover exactly the paths the noise flag reports, at any depth.
        var globs = IndexQueries.VendorExcludeGlobs();
        Assert.Contains("3rdparty/**", globs);
        Assert.Contains("**/external/**", globs);
        Assert.Contains("**/third_party/**", globs); // '_' marker present (escaped downstream)
    }

    // ---------------------------------------------------------------- search_symbol (o09b)

    [Fact]
    public void SearchSymbolFlagsNoiseAndFirstPartyOnlyDropsVendor()
    {
        var tools = Tools();

        var all = Parse(tools.SearchSymbol("PhoenixVendorType", match: "exact")).GetProperty("symbols");
        Assert.Equal(1, all.GetArrayLength());
        Assert.Equal(VendorFixture.VendorFile, all[0].GetProperty("path").GetString());
        Assert.True(all[0].GetProperty("noise").GetBoolean());                 // vendored hit is flagged

        // firstPartyOnly and an equivalent excludePath both drop the vendored hit.
        Assert.Equal(0, Parse(tools.SearchSymbol("PhoenixVendorType", match: "exact", firstPartyOnly: true))
            .GetProperty("symbols").GetArrayLength());
        Assert.Equal(0, Parse(tools.SearchSymbol("PhoenixVendorType", match: "exact", excludePath: "3rdparty/**"))
            .GetProperty("symbols").GetArrayLength());

        // A first-party hit survives firstPartyOnly and carries NO noise flag (null → omitted).
        var shared = Parse(tools.SearchSymbol("PhoenixSharedType", match: "exact", firstPartyOnly: true))
            .GetProperty("symbols");
        Assert.True(shared.GetArrayLength() >= 1);
        Assert.False(shared[0].TryGetProperty("noise", out _));
    }

    [Fact]
    public void FirstPartyOnlyExcludesVendorNestedAtAnyDepth()
    {
        var tools = Tools();

        // A vendor dir nested well below the root ('src/Deep/external/') is flagged AND excluded —
        // firstPartyOnly is complete at any depth, not just for root-level vendor dirs.
        var all = Parse(tools.SearchSymbol("PhoenixNestedVendorType", match: "exact")).GetProperty("symbols");
        Assert.Equal(1, all.GetArrayLength());
        Assert.Equal(VendorFixture.NestedVendorFile, all[0].GetProperty("path").GetString());
        Assert.True(all[0].GetProperty("noise").GetBoolean());

        Assert.Equal(0, Parse(tools.SearchSymbol("PhoenixNestedVendorType", match: "exact", firstPartyOnly: true))
            .GetProperty("symbols").GetArrayLength());
    }

    // ---------------------------------------------------------------- search_text (o09b + o09c)

    [Fact]
    public void SearchTextFlagsNoiseAndFiltersVendor()
    {
        var tools = Tools();

        var hits = Parse(tools.SearchText("PhoenixVendorType")).GetProperty("hits");
        var vendorHit = hits.EnumerateArray()
            .Single(h => h.GetProperty("path").GetString() == VendorFixture.VendorFile);
        Assert.True(vendorHit.GetProperty("noise").GetBoolean());

        // firstPartyOnly and excludePath both remove the vendored line.
        Assert.DoesNotContain(Parse(tools.SearchText("PhoenixVendorType", firstPartyOnly: true))
            .GetProperty("hits").EnumerateArray(), h => h.GetProperty("path").GetString() == VendorFixture.VendorFile);
        Assert.DoesNotContain(Parse(tools.SearchText("PhoenixVendorType", excludePath: "3rdparty/**"))
            .GetProperty("hits").EnumerateArray(), h => h.GetProperty("path").GetString() == VendorFixture.VendorFile);
    }

    // ---------------------------------------------------------------- find_file (o09c)

    [Fact]
    public void FindFileExcludePathDropsVendorFile()
    {
        var tools = Tools();
        Assert.True(Parse(tools.FindFile("VendorLib.cs")).GetProperty("files").GetArrayLength() >= 1);
        Assert.Equal(0, Parse(tools.FindFile("VendorLib.cs", excludePath: "3rdparty/**"))
            .GetProperty("files").GetArrayLength());
    }

    [Fact]
    public void FindFileEmptyGlobReturnsNoMatchNotEverything()
    {
        // An empty include must mean "no match", never a WHERE 1=1 full-table listing.
        var tools = Tools();
        Assert.Equal(0, Parse(tools.FindFile("")).GetProperty("files").GetArrayLength());
        using var q = _fx.Open();
        Assert.Empty(q.FindFiles("", 10));
    }

    // ---------------------------------------------------------------- references (o09c)

    [Fact]
    public void ReferencesExcludePathDropsVendorCandidatesAndForcesIndexed()
    {
        var tools = Tools();

        var all = Parse(tools.References("PhoenixSharedType", mode: "indexed"));
        Assert.Contains(Samples(all), s => s.GetProperty("path").GetString()!.StartsWith("3rdparty/"));
        int totalAll = all.GetProperty("totalCandidates").GetInt32();

        var ex = Parse(tools.References("PhoenixSharedType", mode: "indexed", excludePath: "3rdparty/**"));
        Assert.DoesNotContain(Samples(ex), s => s.GetProperty("path").GetString()!.StartsWith("3rdparty/"));
        Assert.True(ex.GetProperty("totalCandidates").GetInt32() < totalAll);

        // In auto mode a path filter forces the indexed path (so the filter is honored precisely),
        // surfaced non-silently via partialReason + indexed confidence.
        var auto = Parse(tools.References("PhoenixSharedType", excludePath: "3rdparty/**"));
        Assert.Equal("indexed", auto.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.Equal("path_filter_ran_indexed_candidates", auto.GetProperty("partialReason").GetString());
    }

    // ---------------------------------------------------------------- repo_overview (o09b)

    [Fact]
    public void RepoOverviewListsSuggestedExcludes()
    {
        var tools = Tools();
        var excludes = Parse(tools.RepoOverview()).GetProperty("suggestedExcludes")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("3rdparty/**", excludes);
    }
}
