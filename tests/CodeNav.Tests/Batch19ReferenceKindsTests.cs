using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// et2: references usage-kind buckets. Field evidence: "'500 references' is often ~480 xmldoc/comment
/// mentions and 20 real calls" — without kinds a caller re-reads every sample to find the executions.
/// The exact path classifies each location (call/construction/typeMention/attribute/nameof/xmldoc/...),
/// reports a kinds breakdown, honors a usageKinds filter in COUNTS (not just samples), and offers
/// publicConsumersOnly (drop usages inside the declaring project). The indexed fallback cannot
/// classify and must say so instead of silently ignoring the filters.
/// </summary>
public class Batch19ReferenceKindsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string ClassifyAt(string code, string token, bool isType = false)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code);
        int pos = code.IndexOf(token, StringComparison.Ordinal);
        Assert.True(pos >= 0, $"token '{token}' not in snippet");
        return SemanticReferenceKinds.Classify(tree.GetRoot(), pos, isType);
    }

    // Review defects: constructor initializers and target-typed new() are REAL EXECUTIONS and must
    // land in call/construction — 'usageKinds:call,construction' is sold as "real executions only".
    [Fact]
    public void ClassifierCoversCtorInitializersAndImplicitNew()
    {
        Assert.Equal("call", ClassifyAt("class D : B { public D() : base(1) { } }", "base"));
        Assert.Equal("call", ClassifyAt("class D { public D(int x) : this() { } public D() { } }", "this()"));
        Assert.Equal("construction", ClassifyAt("class C { W w = new(5); }", "new(5)"));
        Assert.Equal("construction", ClassifyAt("class C { void M() { var w = new W(); } }", "W()", isType: true));
        Assert.Equal("call", ClassifyAt("class C { void M() { Guard.NotNull(1); } }", "NotNull"));
        Assert.Equal("nameof", ClassifyAt("class C { string n = nameof(C); }", "C)", isType: true));
        Assert.Equal("xmldoc", ClassifyAt("/// <summary><see cref=\"W\"/></summary>\nclass C { }", "W\"", isType: true));
    }

    [Fact]
    public void KindsBreakdownFilterAndPublicConsumers()
    {
        string root = Directory.CreateTempSubdirectory("codenav-kinds").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 6, seed: 21);
            // A probe file in the DECLARING project — written NEXT TO Guard.cs so it lands inside that
            // project's compile set regardless of the generator's directory layout (a file above the
            // project dir would be orphaned: indexed for search, absent from the compilation):
            // one xmldoc cref, one nameof, one real call — three distinct kinds for Guard.NotNull.
            string guardDir = Path.GetDirectoryName(
                Directory.GetFiles(root, "Guard.cs", SearchOption.AllDirectories).First())!;
            File.WriteAllText(Path.Combine(guardDir, "KindProbeUses.cs"),
                """
                namespace Acme.Platform.Common
                {
                    /// <summary>Wraps <see cref="Guard.NotNull(object, string)"/> for probing.</summary>
                    public class KindProbeUses
                    {
                        public string NameOfIt = nameof(Guard.NotNull);
                        public KindProbeUses()
                        {
                            Guard.NotNull(new object(), "probe");
                        }
                    }
                }
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            var manager = new IndexManager(root, dbPath);
            var semantic = new SemanticService(manager);
            try
            {
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable);
                var tools = new NavigationTools(manager, semantic);

                if (!semantic.FrameworkRefsAvailable) return; // review C2: deterministic env skip (fast), retry handles transients
                var all = SemanticRetry.ParseExactWithRetry( // n7ly sweep: retries transient degrades
                    () => tools.References(name: "NotNull", timeoutMs: 90000));

                // Kinds breakdown covers the probe's three usage forms; samples carry per-hit kinds.
                var kinds = all.GetProperty("kinds");
                Assert.True(kinds.GetProperty("call").GetInt32() >= 1, "expected at least the probe's call");
                Assert.True(kinds.GetProperty("xmldoc").GetInt32() >= 1, "expected the cref mention");
                Assert.True(kinds.GetProperty("nameof").GetInt32() >= 1, "expected the nameof usage");
                int unfilteredTotal = all.GetProperty("totalReferences").GetInt32();
                Assert.True(all.GetProperty("groups").EnumerateArray()
                        .SelectMany(g => g.GetProperty("samples").EnumerateArray())
                        .All(s => s.TryGetProperty("kind", out _)),
                    "every exact sample carries its usage kind");

                // usageKinds filters COUNTS, not just samples (same discipline as includeGenerated).
                var onlyNameof = SemanticRetry.ParseExactWithRetry( // n7ly sweep: retries transient degrades
                    () => tools.References(name: "NotNull", usageKinds: "nameof", timeoutMs: 90000));
                Assert.True(onlyNameof.GetProperty("totalReferences").GetInt32() >= 1);
                Assert.True(onlyNameof.GetProperty("totalReferences").GetInt32() < unfilteredTotal,
                    "nameof-only total should be smaller than the unfiltered total");
                Assert.Single(onlyNameof.GetProperty("kinds").EnumerateObject()); // nameof is the only bucket

                // publicConsumersOnly: the declaring project's own usages (incl. the probe file) drop out.
                var external = SemanticRetry.ParseExactWithRetry( // n7ly sweep: retries transient degrades
                    () => tools.References(name: "NotNull", publicConsumersOnly: true, timeoutMs: 90000));
                int externalTotal = external.GetProperty("totalReferences").GetInt32();
                Assert.True(externalTotal < unfilteredTotal, "declaring-project usages were not excluded");
                Assert.DoesNotContain(external.GetProperty("groups").EnumerateArray(),
                    g => g.GetProperty("project").GetString() == "Acme.Platform.Common");

                // Review defect: publicConsumersOnly must anchor on the DECLARING project even when the
                // caller targets a USAGE position — the first cut anchored on the position's project
                // and INVERTED the filter (kept the declaring project, dropped the usage's).
                using (var q = manager.OpenQueries())
                {
                    var use = q.SearchText("Guard.NotNull", 20)
                        .FirstOrDefault(h => !h.FilePath.Contains("Common", StringComparison.OrdinalIgnoreCase));
                    if (use is not null)
                    {
                        var posExt = SemanticRetry.ParseExactWithRetry( // n7ly sweep: retries transient degrades
                            () => tools.References(path: use.FilePath, line: use.Line, publicConsumersOnly: true, timeoutMs: 90000));
                        if (posExt.TryGetProperty("meta", out var pm) && pm.GetProperty("confidence").GetString() == "exact")
                            Assert.DoesNotContain(posExt.GetProperty("groups").EnumerateArray(),
                                g => g.GetProperty("project").GetString() == "Acme.Platform.Common");
                    }
                }

                // Review defect: an unknown usageKinds value must be a bad_request, not a silent-empty
                // exact result that reads as "dead code".
                var bad = Parse(tools.References(name: "Guard", usageKinds: "calls"));
                Assert.Equal("bad_request", bad.GetProperty("error").GetString());
                Assert.Contains("call", bad.GetProperty("detail").GetString());

                // Indexed fallback: cannot classify — must say so, not silently ignore the filter.
                var indexed = Parse(tools.References(name: "Guard", mode: "indexed", usageKinds: "call", maxFiles: 50));
                Assert.Contains("NOT applied", indexed.GetProperty("note").GetString());
            }
            finally
            {
                semantic.Dispose();
                manager.Dispose();
            }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }
}
