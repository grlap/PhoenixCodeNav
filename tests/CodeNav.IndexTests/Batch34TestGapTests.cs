using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Batch34DeadlineSalvageIsolationCollection
{
    public const string Name = "Batch34 deadline-salvage SQLite isolation";
}

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
[Collection(Batch34DeadlineSalvageIsolationCollection.Name)]
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
    public void DeadlineExhaustionAfterConversionClassificationSalvagesALowerBound()
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

                int counted = 0;
                int conversionClassifications = 0;
                // Roslyn does not yet enumerate positive conversion-use locations (4rk), so classify
                // this positive-reference method as a conversion through the instance test seam.
                // The decisive assertion is ordering: classification must finish before the first
                // cancellable count, never after the salvage catch with an expired token.
                semantic.TestOnlyUserDefinedConversionClassifier = (_, _, token) =>
                {
                    Assert.Equal(0, counted);
                    Assert.False(token.IsCancellationRequested);
                    conversionClassifications++;
                    return true;
                };
                // The seam: a "deadline" fires after the second counted location — exactly the
                // mid-count OCE shape the 24n salvage exists for.
                semantic.TestOnlyPerLocationCounted = total =>
                {
                    counted = total;
                    if (total >= 2) throw new OperationCanceledException();
                };
                var refs = SemanticRetry.ParseWithRetry(
                    () => tools.References(name: "Ping", timeoutMs: 60000),
                    json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                            (reason.GetString() ?? "").Contains(
                                "semantic_timeout", StringComparison.Ordinal),
                    "salvaged deadline lower bound");
                Assert.Equal("indexed", refs.GetProperty("meta")
                    .GetProperty("confidence").GetString());
                Assert.Equal(2, refs.GetProperty("totalReferences").GetInt32()); // counted-so-far survives
                Assert.True(refs.GetProperty("totalIsLowerBound").GetBoolean());
                Assert.True(refs.GetProperty("partial").GetBoolean());
                Assert.Contains("2 compiler-reported locations", refs.GetProperty("summary").GetString());
                Assert.Contains("lower bound", refs.GetProperty("summary").GetString());
                Assert.Contains("deadline exhausted", refs.GetProperty("partialReason").GetString());
                Assert.Contains("conversion_usage_enumeration_gap",
                    refs.GetProperty("partialReason").GetString());
                Assert.Equal(1, conversionClassifications);
                JsonElement salvagedGroup = Assert.Single(refs.GetProperty("groups")
                    .EnumerateArray());
                JsonElement salvagedSample = Assert.Single(salvagedGroup
                    .GetProperty("samples").EnumerateArray());
                Assert.False(string.IsNullOrWhiteSpace(
                    salvagedSample.GetProperty("text").GetString()));

                // Seam off: the same query is a full census again — no hedge, all 3 counted.
                semantic.TestOnlyPerLocationCounted = null;
                semantic.TestOnlyUserDefinedConversionClassifier = null;
                var full = SemanticRetry.ParseExactWithRetry(() => tools.References(name: "Ping", timeoutMs: 60000));
                Assert.Equal(3, full.GetProperty("totalReferences").GetInt32());
                Assert.False(full.TryGetProperty("totalIsLowerBound", out _));
                Assert.False(full.TryGetProperty("sampleCoverage", out _));

                int textPassCancellation = 0;
                semantic.TestOnlyBeforeReferenceSampleText = () =>
                {
                    if (Interlocked.Exchange(ref textPassCancellation, 1) == 0)
                        throw new OperationCanceledException();
                };
                var textDegraded = SemanticRetry.ParseExactWithRetry(
                    () => tools.References(name: "Ping", timeoutMs: 60000));
                Assert.Equal(1, textPassCancellation);
                Assert.Equal(3, textDegraded.GetProperty("totalReferences").GetInt32());
                Assert.Equal("exact", textDegraded.GetProperty("meta")
                    .GetProperty("confidence").GetString());
                Assert.False(textDegraded.TryGetProperty("totalIsLowerBound", out _));
                Assert.False(textDegraded.GetProperty("partial").GetBoolean());
                Assert.False(textDegraded.TryGetProperty("sampleCoverage", out _));

                semantic.TestOnlyReferenceSampleTextUnavailable = _ => true;
                semantic.TestOnlyBeforeReferenceSampleText = () =>
                    throw new OperationCanceledException();
                var samplesDeadline = SemanticRetry.ParseExactWithRetry(
                    () => tools.References(name: "Ping", timeoutMs: 60000));
                Assert.Equal(3, samplesDeadline.GetProperty("totalReferences").GetInt32());
                Assert.Equal("exact", samplesDeadline.GetProperty("meta")
                    .GetProperty("confidence").GetString());
                Assert.False(samplesDeadline.GetProperty("partial").GetBoolean());
                JsonElement deadlineCoverage = samplesDeadline.GetProperty("sampleCoverage");
                JsonElement deadlineReason = Assert.Single(deadlineCoverage
                    .GetProperty("reasons").EnumerateArray());
                Assert.Equal("references.samples_deadline",
                    deadlineReason.GetProperty("noteId").GetString());
                Assert.Contains("timeoutMs", deadlineReason.GetProperty("guidance").GetString());

                semantic.TestOnlyBeforeReferenceSampleText = null;
                var samplesTrimmed = SemanticRetry.ParseExactWithRetry(
                    () => tools.References(name: "Ping", timeoutMs: 60000));
                Assert.Equal(3, samplesTrimmed.GetProperty("totalReferences").GetInt32());
                Assert.Equal("exact", samplesTrimmed.GetProperty("meta")
                    .GetProperty("confidence").GetString());
                Assert.False(samplesTrimmed.GetProperty("partial").GetBoolean());
                JsonElement sampleCoverage = samplesTrimmed.GetProperty("sampleCoverage");
                Assert.True(sampleCoverage.GetProperty("selected").GetInt32() > 0);
                Assert.Equal(0, sampleCoverage.GetProperty("returned").GetInt32());
                Assert.False(sampleCoverage.GetProperty("complete").GetBoolean());
                JsonElement textLossReason = Assert.Single(sampleCoverage
                    .GetProperty("reasons").EnumerateArray());
                Assert.Equal("references.samples_trimmed",
                    textLossReason.GetProperty("noteId").GetString());
                Assert.Equal(sampleCoverage.GetProperty("selected").GetInt32(),
                    textLossReason.GetProperty("omitted").GetInt32());
                Assert.All(samplesTrimmed.GetProperty("groups").EnumerateArray(), group =>
                    Assert.Empty(group.GetProperty("samples").EnumerateArray()));
                semantic.TestOnlyReferenceSampleTextUnavailable = null;
            }
            finally
            {
                semantic.TestOnlyBeforeReferenceSampleText = null;
                semantic.TestOnlyReferenceSampleTextUnavailable = null;
                semantic.Dispose();
                m.Dispose();
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReferenceSampleCoverageCountsOnlySamplesThatSurviveResponseBudgeting()
    {
        string root = Directory.CreateTempSubdirectory("codenav-reference-sample-budget").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"), SdkCsproj);
            File.WriteAllText(Path.Combine(proj, "Core.cs"),
                "namespace S { public class Core { public void Ping() { } } }");
            File.WriteAllText(Path.Combine(proj, "Uses.cs"),
                "namespace S { public class Uses { public void Call(Core c) { c.Ping(); } } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 15_000));
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic)
            {
                // Instance-scoped shaping seam: exercise the actual response-budget callback
                // without creating hundreds of projects merely to exceed the public 64 KiB cap.
                TestOnlyReferencesResponseMaxBytes = 1024,
            };

            JsonElement response = SemanticRetry.ParseExactWithRetry(() =>
                tools.References(name: "Ping", samplesPerGroup: 10,
                    timeoutMs: 60_000));

            Assert.True(response.GetProperty("truncated").GetBoolean());
            Assert.Empty(response.GetProperty("groups").EnumerateArray());
            JsonElement coverage = response.GetProperty("sampleCoverage");
            Assert.Equal(1, coverage.GetProperty("selected").GetInt32());
            Assert.Equal(0, coverage.GetProperty("returned").GetInt32());
            Assert.False(coverage.GetProperty("complete").GetBoolean());
            JsonElement budgetReason = Assert.Single(coverage.GetProperty("reasons")
                .EnumerateArray());
            Assert.Equal(1, budgetReason.GetProperty("omitted").GetInt32());
            Assert.Equal("references.samples_byte_budget",
                budgetReason.GetProperty("noteId").GetString());
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
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }
}
