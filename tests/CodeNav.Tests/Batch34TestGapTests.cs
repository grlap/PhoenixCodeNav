using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// The four standing test-gap beads, closed:
///  - 5hs: outline partialFilesTruncated when a partial type spans &gt;10 sibling files.
///  - 9xg: semantic definition caps declarations at MaxDeclarationSites=20 with
///    declarationsTruncated.
///  - trp: BuildDeclarationBody's three omitted-object reasons.
///  - tof: the 24n deadline-exhaustion salvage branch, made deterministic via the
///    TestOnlyPerLocationCounted seam (a real deadline landing mid-count is not reproducible
///    on demand — the gap the salvage shipped with).
/// </summary>
public class Batch34TestGapTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string SdkCsproj => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    // ------------------------------------------------------------------ 5hs

    [Fact]
    public void OutlineMarksPartialFilesTruncatedBeyondTen()
    {
        string root = Directory.CreateTempSubdirectory("codenav-5hs").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"), SdkCsproj);
            // 12 partial halves: from any one file there are 11 OTHERS — over the 10-sibling cap.
            for (int i = 0; i < 12; i++)
            {
                File.WriteAllText(Path.Combine(proj, $"Mega{i:D2}.cs"),
                    $"namespace M {{ public partial class Mega {{ public void M{i}() {{ }} }} }}");
            }

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 15000));
            var tools = new NavigationTools(m, new SemanticService(m));

            var mega = Parse(tools.Outline("P/Mega00.cs"))
                .GetProperty("symbols")[0].GetProperty("members").EnumerateArray()
                .Single(n => n.GetProperty("name").GetString() == "Mega");
            Assert.Equal(10, mega.GetProperty("partialFiles").GetArrayLength()); // capped list
            Assert.True(mega.GetProperty("partialFilesTruncated").GetBoolean(),
                ">10 partial siblings must be marked truncated, not silently capped");
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ 9xg

    [Fact]
    public void SemanticDefinitionCapsDeclarationSites()
    {
        string root = Directory.CreateTempSubdirectory("codenav-9xg").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"), SdkCsproj);
            // 22 declaration sites — over MaxDeclarationSites=20.
            for (int i = 0; i < 22; i++)
            {
                File.WriteAllText(Path.Combine(proj, $"Wide{i:D2}.cs"),
                    $"namespace W {{ public partial class Wide {{ public void W{i}() {{ }} }} }}");
            }

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 15000));
                if (!semantic.FrameworkRefsAvailable) return;
                var tools = new NavigationTools(m, semantic);

                var def = SemanticRetry.ParseExactWithRetry(() => tools.Definition(name: "Wide", timeoutMs: 60000));
                Assert.Equal("exact", def.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.Equal(20, def.GetProperty("declarations").GetArrayLength());
                Assert.True(def.GetProperty("declarationsTruncated").GetBoolean(),
                    "22 declaration sites must cap at 20 WITH the truncation marker");
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ trp

    [Fact]
    public void DeclarationBodyReportsEveryOmissionReason()
    {
        string root = Directory.CreateTempSubdirectory("codenav-trp").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"), SdkCsproj);
            File.WriteAllText(Path.Combine(proj, "Short.cs"),
                "namespace T { public class Short { } }"); // 1 line
            // A single line far larger than the 512-byte budget floor.
            File.WriteAllText(Path.Combine(proj, "Huge.cs"),
                $"namespace T {{ public class Huge {{ public string S = \"{new string('x', 4000)}\"; }} }}");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 15000));
            var tools = new NavigationTools(m, new SemanticService(m));

            static JsonElement AsJson(object body) => JsonDocument.Parse(Json.Serialize(body)).RootElement;

            // content_unavailable: neither on disk nor in the index.
            var missing = AsJson(tools.BuildDeclarationBody("P/Nope.cs", 1, 3, 4096, preferLive: true));
            Assert.True(missing.GetProperty("omitted").GetBoolean());
            Assert.Equal("content_unavailable", missing.GetProperty("reason").GetString());

            // span_beyond_content: a stale span pointing past EOF of the (index) content.
            var beyond = AsJson(tools.BuildDeclarationBody("P/Short.cs", 9999, 10002, 4096, preferLive: false));
            Assert.True(beyond.GetProperty("omitted").GetBoolean());
            Assert.Equal("span_beyond_content", beyond.GetProperty("reason").GetString());
            Assert.True(beyond.GetProperty("contentLines").GetInt32() >= 1);

            // first_line_exceeds_budget: even line one cannot fit the (floored 512-byte) budget.
            var huge = AsJson(tools.BuildDeclarationBody("P/Huge.cs", 1, 1, 1, preferLive: false));
            Assert.True(huge.GetProperty("omitted").GetBoolean());
            Assert.Equal("first_line_exceeds_budget", huge.GetProperty("reason").GetString());
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ tof

    [Fact]
    public void DeadlineExhaustionMidCountSalvagesALowerBound()
    {
        string root = Directory.CreateTempSubdirectory("codenav-tof").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"), SdkCsproj);
            File.WriteAllText(Path.Combine(proj, "Core.cs"),
                "namespace S { public class Core { public void Ping() { } } }");
            File.WriteAllText(Path.Combine(proj, "Uses.cs"),
                """
                namespace S
                {
                    public class Uses
                    {
                        public void A(Core c) => c.Ping();
                        public void B(Core c) => c.Ping();
                        public void C(Core c) => c.Ping();
                    }
                }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 15000));
                if (!semantic.FrameworkRefsAvailable) return;
                var tools = new NavigationTools(m, semantic);

                // The seam: a "deadline" fires after the second counted location — exactly the
                // mid-count OCE shape the 24n salvage exists for.
                semantic.TestOnlyPerLocationCounted = total =>
                {
                    if (total >= 2) throw new OperationCanceledException();
                };
                var refs = SemanticRetry.ParseExactWithRetry(() => tools.References(name: "Ping", timeoutMs: 60000));
                Assert.Equal("exact", refs.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.Equal(2, refs.GetProperty("totalReferences").GetInt32()); // counted-so-far survives
                Assert.True(refs.GetProperty("totalIsLowerBound").GetBoolean());
                Assert.True(refs.GetProperty("partial").GetBoolean());
                Assert.StartsWith("at least 2", refs.GetProperty("summary").GetString());
                Assert.Contains("deadline exhausted", refs.GetProperty("partialReason").GetString());

                // Seam off: the same query is a full census again — no hedge, all 3 counted.
                semantic.TestOnlyPerLocationCounted = null;
                var full = SemanticRetry.ParseExactWithRetry(() => tools.References(name: "Ping", timeoutMs: 60000));
                Assert.Equal(3, full.GetProperty("totalReferences").GetInt32());
                Assert.False(full.TryGetProperty("totalIsLowerBound", out _));
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- helpers

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
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
