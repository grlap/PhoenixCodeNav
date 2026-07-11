using System.Text.Json;
using CodeNav.Core;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Regression coverage for review batch 1: PhoenixCodeNav-qie/bw3/d10/k1s.
/// </summary>
public class WorkspacePathsTests
{
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-paths").FullName);

    [Theory]
    [InlineData("src/App/Foo.cs")]
    [InlineData("Foo.cs")]
    [InlineData("a/b/../c/Foo.cs")]   // '..' that stays inside
    [InlineData("./src/Foo.cs")]
    public void AcceptsContainedPaths(string rel)
    {
        Assert.True(WorkspacePaths.TryResolveInside(_root, rel, out string full));
        Assert.StartsWith(_root, full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../Other/secret.env")]   // sibling-directory escape (the case-sensitivity fix)
    [InlineData("../App-sibling/x.cs")]
    public void RejectsSiblingDirectoryEscape(string rel)
    {
        // Root has a distinct sibling; a '..' that re-descends into it must be rejected
        // regardless of filesystem case rules (GetRelativePath yields a "../" result).
        string appRoot = Path.Combine(_root, "App");
        Directory.CreateDirectory(appRoot);
        Assert.False(WorkspacePaths.TryResolveInside(appRoot, rel, out _), $"expected rejection of '{rel}'");
    }

    [Theory]
    [InlineData("../../../../Windows/win.ini")]
    [InlineData("../secrets.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/etc/shadow")]        // absolute-looking; must not escape
    [InlineData("src/../../escape.cs")]
    [InlineData("")]
    public void RejectsEscapingOrRootedPaths(string rel)
    {
        // '/etc/shadow' degrades to a contained relative path after slash-trim, which is
        // acceptable (it resolves under root, cannot read /etc). Everything with real '..'
        // escape or a drive root must be rejected.
        bool ok = WorkspacePaths.TryResolveInside(_root, rel, out string full);
        if (ok)
        {
            Assert.StartsWith(_root, full, StringComparison.OrdinalIgnoreCase);
        }
        // The genuinely dangerous ones must be rejected outright.
        if (rel.Contains("..") || rel.Contains(':') || rel.Length == 0)
        {
            Assert.False(ok, $"expected rejection of '{rel}'");
        }
    }
}

[CollectionDefinition("Batch1 SQLite pool isolation", DisableParallelization = true)]
public sealed class Batch1SqlitePoolIsolationCollection { }

// IndexFixture and many independent integration-test fixtures still use the provider's
// process-global ClearAllPools during teardown. This class deliberately pounds live readers;
// isolate its collection so another fixture cannot invalidate a connection between Open/Read.
[Collection("Batch1 SQLite pool isolation")]
public class Batch1ToolTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexFixture _fx;
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;
    private readonly NavigationTools _tools;

    public Batch1ToolTests(IndexFixture fx)
    {
        _fx = fx;
        _manager = new IndexManager(_fx.Root, _fx.DbPath);
        _manager.Start();
        for (int i = 0; i < 100 && !_manager.IsQueryable; i++) Thread.Sleep(50);
        _semantic = new SemanticService(_manager);
        _tools = new NavigationTools(_manager, _semantic);
    }

    public void Dispose()
    {
        _semantic.Dispose();
        _manager.Dispose();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- qie: source_context path traversal ----

    [Theory]
    [InlineData("../../../../Windows/System32/drivers/etc/hosts")]
    [InlineData("C:/Windows/win.ini")]
    public void SourceContextRejectsWorkspaceEscape(string escape)
    {
        var json = Parse(_tools.SourceContext(escape, "1-5"));
        Assert.Equal("path_outside_workspace", json.GetProperty("error").GetString());
    }

    [Fact]
    public void SourceContextStillReadsContainedFiles()
    {
        using var q = _manager.OpenQueries();
        var guard = q.FindFiles("Guard.cs", 1).Single();
        var json = Parse(_tools.SourceContext(guard.Path, "1-6", contextLines: 0));
        Assert.Equal("live", json.GetProperty("freshness").GetString());
    }

    // ---- bw3: refresh_index cannot ingest external files ----

    [Fact]
    public void RefreshIndexDoesNotIngestExternalFiles()
    {
        // Drop a secret outside the workspace and try to pull it in via refresh_index.
        string outsideDir = Path.GetFullPath(Path.Combine(_fx.Root, "..", "codenav-outside-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(outsideDir);
        string secret = Path.Combine(outsideDir, "secret.config");
        File.WriteAllText(secret, "<add key=\"PhoenixSecretMarker\" value=\"TopSecret\" />");
        try
        {
            using var store = new IndexStore(_fx.DbPath, createNew: false);
            var result = DeltaRefresher.Refresh(store, _fx.Root, new[]
            {
                "../" + Path.GetFileName(outsideDir) + "/secret.config",
                Path.Combine(outsideDir, "secret.config"), // absolute form
            });
            Assert.Equal(0, result.AddedFiles);

            using var q = _manager.OpenQueries();
            Assert.Empty(q.SearchText("PhoenixSecretMarker", 5, new IndexQueries.TextFilter(Lang: "config")));
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    // ---- d10: Health() is safe alongside concurrent refreshes ----

    [Fact]
    public void HealthIsStableUnderConcurrentRefresh()
    {
        var stop = false;
        var errors = new List<Exception>();
        var pounder = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    var h = _manager.Health();
                    _ = h.IndexVersion; // touch cached fields
                    _ = _tools.RepoOverview();
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });
        pounder.Start();
        for (int i = 0; i < 40; i++)
        {
            _manager.RequestRefresh(); // full detect sweeps hammer the pump/write connection
            Thread.Sleep(5);
        }
        Volatile.Write(ref stop, true);
        pounder.Join(TimeSpan.FromSeconds(10));
        Assert.Empty(errors);
    }

    // ---- k1s: outline is bounded even for a huge single-root file ----

    [Fact]
    public void OutlineIsBoundedForHugeSingleRootFile()
    {
        // Build a synthetic 4000-member single-class file in an isolated index. The shared
        // fixture's IndexManager owns a live watcher; directly delta-refreshing that same DB here
        // races the watcher and can insert files.path twice under full-suite timing.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("namespace Acme.Huge {");
        sb.AppendLine("  public class GiganticService {");
        for (int i = 0; i < 4000; i++)
        {
            sb.AppendLine($"    public int ComputeVeryLongDescriptiveMethodName{i}(int alpha, string beta, decimal gamma) {{ return {i}; }}");
        }
        sb.AppendLine("  }");
        sb.AppendLine("}");

        string root = Directory.CreateTempSubdirectory("codenav-outline-huge").FullName;
        const string rel = "GiganticService.cs";
        try
        {
            File.WriteAllText(Path.Combine(root, "Huge.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, rel), sb.ToString());
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
            Assert.True(manager.IsQueryable);
            var tools = new NavigationTools(manager, semantic);

            string raw = tools.Outline(rel, depth: 2);
            Assert.True(raw.Length <= Json.HardBudgetBytes,
                $"outline of a 4000-member file was {raw.Length} bytes (cap {Json.HardBudgetBytes})");
            var json = Parse(raw);
            Assert.True(json.GetProperty("truncated").GetBoolean(), "oversized outline must set truncated:true");
            // The class node must survive even when its 4000 members are dropped.
            Assert.Contains("GiganticService", raw);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* Windows handles */ }
        }
    }

    [Fact]
    public void OutlineStillCompactForNormalFiles()
    {
        using var q = _manager.OpenQueries();
        var guard = q.FindFiles("Guard.cs", 1).Single();
        var json = Parse(_tools.Outline(guard.Path, depth: 2));
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.Contains("NotNull", _tools.Outline(guard.Path, depth: 2));
    }
}
