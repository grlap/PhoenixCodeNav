using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// P3 bug quartet:
///  - 9vw: internal exception messages (paths, connection details) leaked to clients via
///    IndexHealth.Error — clients now get the exception TYPE + a fixed phrase, log gets the rest.
///  - gep: source_context materialized ENTIRE files (File.ReadAllText) to slice a few lines —
///    the read is now streamed and bounded by the requested spans.
///  - wu1: references' summary/totalReferences/kinds counted test locations even when
///    includeTests=false filtered the groups — counts now honor the filter on both paths.
///  - szs: outline partialFiles conflated generic-arity siblings (partial Foo vs partial Foo&lt;T&gt;)
///    — the partial-file lookup now matches arity.
/// </summary>
public class Batch23QuartetTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ------------------------------------------------------------------ 9vw

    [Fact]
    public void IndexStartupErrorExposesTypeNameNotInternals()
    {
        string root = Directory.CreateTempSubdirectory("codenav-9vw").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "namespace X { class A { } }");
            // Force the startup failure: the db path's PARENT is a regular file, so the store/build
            // cannot create or open the database — the underlying exception message names the path.
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "not a directory");
            string dbPath = Path.Combine(blocker, "idx.db");

            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.State == "failed", 15000), $"expected failed, got {m.State}");

            string? err = m.Health().Error;
            Assert.NotNull(err);
            // The sanitized shape — reintroducing `_error = ex.Message` loses this fixed phrase.
            Assert.Contains("during index startup (see server log)", err);
            // And no filesystem internals: neither the workspace temp dir nor the blocker path.
            Assert.DoesNotContain("blocker", err, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, err!, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ gep

    [Fact]
    public void SourceContextReadsOnlyTheRequestedPrefixOfAHugeFile()
    {
        string root = Directory.CreateTempSubdirectory("codenav-gep").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Tiny.cs"), "namespace G { class Tiny { } }");
            // 600 short lines, then ONE ~12M-char monster line. Spans near the top must never
            // reach it: even the max contextLines clamp (500) stays under line 601.
            using (var w = new StreamWriter(Path.Combine(root, "big.txt")))
            {
                for (int i = 1; i <= 600; i++) w.WriteLine($"line{i} content");
                w.WriteLine(new string('x', 12_000_000));
            }

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 15000));
            var tools = new NavigationTools(m, new SemanticService(m));

            // Warm one call first (serializer/statics), then measure the second — the assertion is
            // about per-call file materialization, not one-time infrastructure allocations.
            _ = tools.SourceContext("big.txt", "2-4");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var res = Parse(tools.SourceContext("big.txt", "2-4"));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal("live", res.GetProperty("freshness").GetString());
            string source = res.GetProperty("spans")[0].GetProperty("source").GetString()!;
            Assert.Contains("line2 content", source);
            Assert.Contains("line4 content", source);
            // The old File.ReadAllText path allocates >= 24 MB here (the 12M-char line alone,
            // twice over with the split). The streamed read touches ~6 short lines.
            Assert.True(allocated < 4_000_000,
                $"source_context allocated {allocated:n0} bytes for a 3-line span — whole-file read is back (gep)");

            // Absurd contextLines must clamp, not overflow line arithmetic or drag in the tail.
            var wide = Parse(tools.SourceContext("big.txt", "3", contextLines: int.MaxValue));
            Assert.Equal(1, wide.GetProperty("spans")[0].GetProperty("startLine").GetInt32());

            // Review finding: zero/negative starts must CLAMP to line 1 (0-based callers are
            // common), not silently drop the whole range — the old code rendered "0-3" as 1..5.
            var zero = Parse(tools.SourceContext("big.txt", "0-3"));
            var zeroSpan = zero.GetProperty("spans")[0];
            Assert.Equal(1, zeroSpan.GetProperty("startLine").GetInt32());
            Assert.Contains("line1 content", zeroSpan.GetProperty("source").GetString());
            Assert.Contains("line3 content", zeroSpan.GetProperty("source").GetString());
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ wu1

    [Fact]
    public void ReferenceCountsHonorIncludeTests()
    {
        string tempRoot = Directory.CreateTempSubdirectory("codenav-wu1").FullName;
        string root = tempRoot;
        try
        {
            // The planner adds test projects by CHANCE per subsystem, so not every seed yields an
            // SDK-STYLE test project (needed so directory globbing compiles the probe below). Scan a
            // fixed seed list — generation is seed-deterministic, so the first hit is stable forever.
            string? testCsproj = null;
            foreach (int seed in new[] { 21, 3, 5, 7, 11, 13, 17, 29 })
            {
                root = Path.Combine(tempRoot, $"s{seed}");
                WorkspaceGenerator.Generate(root, targetProjects: 10, seed: seed);
                testCsproj = Directory.GetFiles(root, "*.Tests.csproj", SearchOption.AllDirectories)
                    .FirstOrDefault(p => File.ReadAllText(p).Contains("Sdk=", StringComparison.Ordinal));
                if (testCsproj is not null) break;
            }
            Assert.True(testCsproj is not null, "no probed seed produced an SDK-style test project");
            // Plant a Guard.NotNull usage INSIDE a test project (next to its own compiled sources,
            // matching Batch19's probe technique). Test projects inherit the target's refs, which
            // include Acme.Platform.Common, so the call resolves semantically.
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(testCsproj!)!, "TestProbeUses.cs"),
                """
                namespace Probe.Tests
                {
                    public class TestProbeUses
                    {
                        public TestProbeUses()
                        {
                            Acme.Platform.Common.Guard.NotNull(new object(), "probe");
                        }
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
                var tools = new NavigationTools(m, semantic);

                // Deterministic INDEXED path first (no Roslyn): counts must equal the groups shown.
                var idxFiltered = Parse(tools.References(name: "NotNull", mode: "indexed", includeTests: false, maxFiles: 200));
                int idxTotal = idxFiltered.GetProperty("totalCandidates").GetInt32();
                int idxSum = idxFiltered.GetProperty("groups").EnumerateArray()
                    .Sum(g => g.GetProperty("count").GetInt32());
                Assert.Equal(idxSum, idxTotal); // wu1: total previously kept the excluded test lines
                Assert.DoesNotContain(idxFiltered.GetProperty("groups").EnumerateArray(),
                    g => g.GetProperty("isTest").GetBoolean());
                Assert.Contains("test projects excluded", idxFiltered.GetProperty("summary").GetString());

                var idxAll = Parse(tools.References(name: "NotNull", mode: "indexed", maxFiles: 200));
                Assert.True(idxAll.GetProperty("totalCandidates").GetInt32() > idxTotal,
                    "the probe's test-project line should make the unfiltered count larger");

                // Semantic (exact) path — same discipline, guarded on env like Batch19.
                var all = Parse(tools.References(name: "NotNull", timeoutMs: 90000));
                if (!all.TryGetProperty("meta", out var meta0) ||
                    meta0.GetProperty("confidence").GetString() != "exact")
                {
                    return; // no framework reference assemblies — semantic path unavailable here
                }
                int allTotal = all.GetProperty("totalReferences").GetInt32();
                Assert.Contains(all.GetProperty("groups").EnumerateArray(),
                    g => g.GetProperty("isTest").GetBoolean()); // the probe's project

                var noTests = Parse(tools.References(name: "NotNull", includeTests: false, timeoutMs: 90000));
                int filteredTotal = noTests.GetProperty("totalReferences").GetInt32();
                Assert.True(filteredTotal < allTotal,
                    $"includeTests:false total ({filteredTotal}) should drop below the unfiltered total ({allTotal})");
                Assert.DoesNotContain(noTests.GetProperty("groups").EnumerateArray(),
                    g => g.GetProperty("isTest").GetBoolean());
                int groupSum = noTests.GetProperty("groups").EnumerateArray()
                    .Sum(g => g.GetProperty("count").GetInt32());
                Assert.Equal(groupSum, filteredTotal); // totals describe the same filtered set
                Assert.Contains("test projects excluded", noTests.GetProperty("summary").GetString());
                // kinds honor the filter too: kind counts must sum to the filtered total.
                if (noTests.TryGetProperty("kinds", out var kinds))
                {
                    int kindSum = kinds.EnumerateObject().Sum(k => k.Value.GetInt32());
                    Assert.Equal(filteredTotal, kindSum);
                }
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { Cleanup(tempRoot); }
    }

    // Review finding on the first wu1 cut: the tool re-derived the filtered total by SUMMING the
    // remaining GROUP counts — but a file linked into TWO production projects appears in both
    // groups, so the "filtered" total exceeded the number of physical candidate lines (4 lines
    // reported as 8). The filter now runs inside ReferenceCandidates, which counts per FILE.
    [Fact]
    public void IndexedReferenceTotalsCountLinkedFilesOnce()
    {
        string root = Directory.CreateTempSubdirectory("codenav-wu1link").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Shared"));
            File.WriteAllText(Path.Combine(root, "Shared", "Use.cs"),
                """
                namespace S
                {
                    public class Use
                    {
                        public void M1() { ProbeToken(); }
                        public void M2() { ProbeToken(); }
                        public void M3() { ProbeToken(); }
                        private void ProbeToken() { }
                    }
                }
                """); // 4 candidate lines for "ProbeToken"
            foreach (var proj in new[] { "AppA", "AppB" })
            {
                Directory.CreateDirectory(Path.Combine(root, proj));
                File.WriteAllText(Path.Combine(root, proj, $"{proj}.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                      <ItemGroup><Compile Include="../Shared/Use.cs" /></ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(root, proj, "Anchor.cs"),
                    $"namespace {proj} {{ class Anchor {{ }} }}");
            }
            // And a TEST-ONLY file mentioning the token — its lines must leave the filtered total.
            Directory.CreateDirectory(Path.Combine(root, "AppA.Tests"));
            File.WriteAllText(Path.Combine(root, "AppA.Tests", "AppA.Tests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "AppA.Tests", "UseTests.cs"),
                """
                namespace S.Tests
                {
                    public class UseTests
                    {
                        public void ShouldProbe() { var n = nameof(ProbeToken); }
                        private static void ProbeToken() { }
                    }
                }
                """); // 2 candidate lines for "ProbeToken"

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);

            var (allTotal, allGroups) = q.ReferenceCandidates("ProbeToken", 50, 3);
            Assert.Equal(6, allTotal); // 4 shared + 2 test — physical lines, never per-owner copies
            Assert.Contains(allGroups, g => g.IsTestProject);

            var (prodTotal, prodGroups) = q.ReferenceCandidates("ProbeToken", 50, 3, includeTests: false);
            Assert.Equal(4, prodTotal); // test-only lines gone; the SHARED file counted ONCE
            Assert.DoesNotContain(prodGroups, g => g.IsTestProject);
            // Both owning production groups still see the shared file's lines (attribution intact).
            Assert.Equal(2, prodGroups.Count(g => g.Count == 4));
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ szs

    [Fact]
    public void PartialFilesAreArityMatched()
    {
        string root = Directory.CreateTempSubdirectory("codenav-szs").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            // partial Widget (arity 0) split across two files, and partial Widget<T> (arity 1)
            // split across two OTHER files — same name, namespace, kind, container.
            File.WriteAllText(Path.Combine(proj, "WidgetA1.cs"),
                "namespace N { public partial class Widget { public void A() { } } }");
            File.WriteAllText(Path.Combine(proj, "WidgetA2.cs"),
                "namespace N { public partial class Widget { public void B() { } } }");
            File.WriteAllText(Path.Combine(proj, "WidgetT1.cs"),
                "namespace N { public partial class Widget<T> { public void C() { } } }");
            File.WriteAllText(Path.Combine(proj, "WidgetT2.cs"),
                "namespace N { public partial class Widget<T> { public void D() { } } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using (var q = new IndexQueries(dbPath))
            {
                // Arity 0 sees only its own other half — not the generic sibling's files.
                var arity0 = q.PartialDeclarationFiles("Widget", "N", "class", null, "P/WidgetA1.cs", arity: 0);
                Assert.Equal(new[] { "P/WidgetA2.cs" }, arity0);
                var arity1 = q.PartialDeclarationFiles("Widget", "N", "class", null, "P/WidgetT1.cs", arity: 1);
                Assert.Equal(new[] { "P/WidgetT2.cs" }, arity1);
            }

            // Tool-level: the outline caller must actually THREAD the arity (not default it to 0).
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 15000));
            var tools = new NavigationTools(m, new SemanticService(m));

            static JsonElement WidgetNode(JsonElement outline) =>
                outline.GetProperty("symbols")[0].GetProperty("members")
                    .EnumerateArray().Single(n => n.GetProperty("name").GetString() == "Widget");

            var a = WidgetNode(Parse(tools.Outline("P/WidgetA1.cs")));
            var aFiles = a.GetProperty("partialFiles").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(new[] { "P/WidgetA2.cs" }, aFiles);

            var t = WidgetNode(Parse(tools.Outline("P/WidgetT1.cs")));
            var tFiles = t.GetProperty("partialFiles").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Equal(new[] { "P/WidgetT2.cs" }, tFiles);
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ helpers

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
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { /* windows file locks */ }
    }
}
