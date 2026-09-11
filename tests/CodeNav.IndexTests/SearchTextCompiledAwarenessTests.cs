using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Xunit.Abstractions;

namespace CodeNav.Tests;

public class SearchTextCompiledAwarenessTests(ITestOutputHelper output)
{
    private const string Marker = "OrphanSearchMarker";

    [Fact]
    public void TextCompileAwarenessHasItsOwnDiscoverableCapability()
    {
        WithWorkspace(root => Write(root, "Readme.md", "fixture\n"), (_, _, tools) =>
        {
            const string id = "text-search-compiled-awareness";
            var health = new IndexHealth("ready", "11", "indexed", "refreshed",
                0, null, 123, "C:/" + new string('r', 257), "index.db");
            int bytes = Json.Utf8Bytes(NavigationTools.ServerCapabilitiesUncompactedForTest(health));
            output.WriteLine($"Detailed capabilities: {bytes} bytes; margin {Json.HardBudgetBytes - bytes} bytes.");
            JsonElement compact = Parse(tools.ServerCapabilities());
            Assert.Single(compact.GetProperty("features").EnumerateArray(), f => f.GetProperty("id").GetString() == id);
            JsonElement detailed = Parse(tools.ServerCapabilities(detail: true));
            string summary = Assert.Single(detailed.GetProperty("features").EnumerateArray(),
                f => f.GetProperty("id").GetString() == id).GetProperty("summary").GetString()!;
            foreach (string token in new[] { "token", "regex", "samples", "orphaned", ".cs/.fs/.fsi", "indexed" })
                Assert.Contains(token, summary);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextHitsFlagOnlyUnownedCompileSources(bool regex)
    {
        WithWorkspace(root =>
        {
            Write(root, "Cs/Cs.csproj", CsProject("<Compile Remove=\"Removed.cs;Linked.cs\" />"));
            Write(root, "Consumer/Consumer.csproj", CsProject(
                "<Compile Include=\"../Cs/Linked.cs\" Link=\"Linked.cs\" />"));
            Write(root, "Fs/Fs.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>true</EnableDefaultCompileItems></PropertyGroup>
                  <ItemGroup><Compile Include="Live.fsi;Upper.FSI" /><Compile Remove="Removed.fs" /></ItemGroup>
                </Project>
                """);
            foreach (string path in new[] { "Cs/Live.cs", "Cs/Removed.cs", "Cs/Linked.cs", "Cs/Upper.CS", "Loose.cs", "LooseUpper.CS" })
                Write(root, path, $"// {Marker}\nclass Source {{ }}\n");
            foreach (string path in new[] { "Fs/Live.fs", "Fs/Removed.fs", "Loose.fs", "LooseMixed.fS" })
                Write(root, path, $"// {Marker}\nmodule Source\nlet value = 1\n");
            foreach (string path in new[] { "Fs/Live.fsi", "Fs/Upper.FSI", "Loose.fsi", "LooseUpper.FSI" })
                Write(root, path, $"// {Marker}\nmodule Source\nval value: int\n");
            foreach (string path in new[] { "Script.fsx", "ScriptMixed.FsX", "Readme.md", "Query.sql", "web.config" })
                Write(root, path, Marker + "\n");
        }, (root, manager, tools) =>
        {
            string[] orphans = ["Cs/Removed.cs", "Loose.cs", "Fs/Removed.fs", "Loose.fs", "Loose.fsi",
                "LooseUpper.CS", "LooseMixed.fS", "LooseUpper.FSI"];
            string[] owned = ["Cs/Live.cs", "Cs/Linked.cs", "Cs/Upper.CS", "Fs/Live.fs", "Fs/Live.fsi", "Fs/Upper.FSI"];
            string[] textOnly = ["Script.fsx", "ScriptMixed.FsX", "Readme.md", "Query.sql", "web.config"];
            JsonElement response = Parse(tools.SearchText(Marker, regex: regex, limit: 100));
            var hits = response.GetProperty("hits").EnumerateArray().ToDictionary(h => h.GetProperty("path").GetString()!);
            Assert.Equal(orphans.Concat(owned).Concat(textOnly).Order(), hits.Keys.Order());
            Assert.Equal(19, response.GetProperty(regex ? "matchCount" : "preciseCount").GetInt32());
            Assert.Equal("indexed", response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.All(orphans, path => Assert.True(hits[path].GetProperty("orphaned").GetBoolean(), path));
            Assert.All(owned.Concat(textOnly), path => Assert.False(hits[path].TryGetProperty("orphaned", out _), path));

            using (var queries = manager.OpenQueries())
            {
                Assert.Equal(orphans.Order(), queries.OrphanedPaths(orphans.Concat(owned).ToArray()).Order());
                Assert.Equal(textOnly.Order(), queries.OrphanedPaths(textOnly).Order());
                Assert.Equal(orphans.Order(), queries.OrphanedSourcePaths(
                    orphans.Concat(owned).Concat(textOnly).Concat(orphans).Append("Missing.CS").ToArray()).Order());
                Assert.Empty(queries.OrphanedSourcePaths([]));
                Assert.Equal(orphans.Length, queries.Overview().OrphanedFiles);
                Assert.Equal("Consumer", Assert.Single(queries.ProjectsContaining("Cs/Linked.cs")).Name);
            }

            JsonElement filtered = Parse(tools.SearchText(Marker, regex: regex, project: "Consumer"));
            JsonElement linked = Assert.Single(filtered.GetProperty("hits").EnumerateArray());
            Assert.Equal("Cs/Linked.cs", linked.GetProperty("path").GetString());
            Assert.False(linked.TryGetProperty("orphaned", out _));
        });
    }

    [Fact]
    public void PartialTextHitsRetainOrphanAnnotation()
    {
        WithWorkspace(root => Write(root, "Loose.cs", "// OrphanAlpha\n// OrphanBeta\n"),
            (_, _, tools) =>
            {
                JsonElement response = Parse(tools.SearchText("OrphanAlpha OrphanBeta", partials: "always"));
                JsonElement[] hits = response.GetProperty("hits").EnumerateArray().ToArray();
                Assert.Equal(2, hits.Length);
                Assert.All(hits, hit =>
                {
                    Assert.Equal("partial", hit.GetProperty("matchKind").GetString());
                    Assert.True(hit.GetProperty("orphaned").GetBoolean());
                });
            });
    }

    [Theory]
    [InlineData("elsewhere")]
    [InlineData("didYouMean")]
    public void StructuredSuggestionsDiscloseUnownedSources(string suggestion)
    {
        WithWorkspace(root =>
        {
            Write(root, "Cs/Cs.csproj", CsProject(""));
            Write(root, "Cs/Live.cs", "class OrphanVariant4 { }\n");
            Write(root, "Loose.cs", "class OrphanVariant4 { }\n");
            Write(root, "Readme.md", "OrphanVariant4\n");
        }, (_, _, tools) =>
        {
            JsonElement response = Parse(suggestion == "elsewhere"
                ? tools.SearchText("OrphanVariant4", pathGlob: "Missing/**")
                : tools.SearchText("OrphanVariant 4"));
            Assert.Empty(response.GetProperty("hits").EnumerateArray());
            var samples = response.GetProperty(suggestion).GetProperty("samples").EnumerateArray()
                .ToDictionary(hit => hit.GetProperty("path").GetString()!);
            Assert.Equal(3, samples.Count);
            Assert.True(samples["Loose.cs"].GetProperty("orphaned").GetBoolean());
            Assert.False(samples["Cs/Live.cs"].TryGetProperty("orphaned", out _));
            Assert.False(samples["Readme.md"].TryGetProperty("orphaned", out _));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectOnlyRefreshUpdatesTextHitOwnership(bool regex)
    {
        WithWorkspace(root =>
        {
            Write(root, "Cs/Cs.csproj", CsProject(""));
            Write(root, "Cs/Source.cs", $"// {Marker}\nclass Source {{ }}\n");
        }, (root, manager, tools) =>
        {
            JsonElement Hit() => Assert.Single(Parse(tools.SearchText(Marker, regex: regex))
                .GetProperty("hits").EnumerateArray());
            Assert.False(Hit().TryGetProperty("orphaned", out _));
            foreach (bool removed in new[] { true, false })
            {
                Write(root, "Cs/Cs.csproj", CsProject(removed ? "<Compile Remove=\"Source.cs\" />" : ""));
                IndexManagerTestSupport.RefreshAndWait(manager, ["Cs/Cs.csproj"],
                    q => q.ProjectsContaining("Cs/Source.cs").Count == (removed ? 0 : 1),
                    "project-only refresh must update compile ownership without changing source content");
                JsonElement hit = Hit();
                Assert.Equal("Cs/Source.cs", hit.GetProperty("path").GetString());
                if (removed) Assert.True(hit.GetProperty("orphaned").GetBoolean());
                else Assert.False(hit.TryGetProperty("orphaned", out _));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrphanAnnotationsSurviveByteBudgetPagination(bool regex)
    {
        const int count = 60;
        WithWorkspace(root => Write(root, "Loose.cs", string.Concat(Enumerable.Range(0, count)
            .Select(i => $"// {Marker} {i} " + new string('x', 400) + "\n"))),
            (_, _, tools) =>
            {
                var seen = new HashSet<int>();
                bool shrunk = false;
                string? cursor = null;
                for (int page = 0; page < count; page++)
                {
                    string json = tools.SearchText(Marker, regex: regex, limit: 20, context: 15, cursor: cursor);
                    Assert.True(Json.Utf8Bytes(json) <= Json.HardBudgetBytes);
                    JsonElement response = Parse(json);
                    Assert.Equal(count, response.GetProperty(regex ? "matchCount" : "preciseCount").GetInt32());
                    shrunk |= response.GetProperty("truncated").GetBoolean();
                    foreach (JsonElement hit in response.GetProperty("hits").EnumerateArray())
                    {
                        Assert.True(seen.Add(hit.GetProperty("line").GetInt32()), "duplicate hit across pages");
                        Assert.True(hit.GetProperty("orphaned").GetBoolean());
                    }
                    cursor = response.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
                    if (cursor is null) break;
                }
                Assert.Null(cursor);
                Assert.True(shrunk, "fixture must exercise byte-budget shrinking");
                Assert.Equal(Enumerable.Range(1, count), seen.Order());
            });
    }

    private static string CsProject(string items) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
        </Project>
        """;

    private static void Write(string root, string path, string content)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void WithWorkspace(Action<string> setup, Action<string, IndexManager, NavigationTools> verify)
    {
        string root = Directory.CreateTempSubdirectory("codenav-text-orphan-").FullName;
        try
        {
            setup(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath, fsharpProjectModel: ProjectModelMode.Simple);
            using var manager = new IndexManager(root, dbPath, fsharpProjectModel: ProjectModelMode.Simple);
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(30), "text ownership fixture startup");
            using var semantic = new SemanticService(manager, enableRoslynPersistence: false);
            verify(root, manager, new NavigationTools(manager, semantic));
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }
}
