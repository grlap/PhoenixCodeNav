using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 24:
///  - bt7: member modifiers (static/sealed/abstract/virtual/override/new/readonly/const) indexed
///    (schema v4) and surfaced on outline + symbol payloads — in deep hierarchies "method" alone
///    cannot tell a caller which override site to edit.
///  - 24n: semantic responses carry timing {deadlineMs, elapsedMs}; deadline exhaustion mid-scan
///    salvages counted work as a hedged lower bound (service-level shape covered here; the
///    mid-scan cancellation itself is not deterministically forceable — bead tracks that gap).
/// </summary>
public class Batch24ModifiersDeadlineTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ------------------------------------------------------------------ bt7

    [Fact]
    public void ModifiersAreIndexedAndSurfaced()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bt7").FullName;
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
            File.WriteAllText(Path.Combine(proj, "Hierarchy.cs"),
                """
                namespace H
                {
                    public abstract class BaseProcessor
                    {
                        public abstract void Handle();
                        protected virtual void Prepare() { }
                        public static int Count;
                        public const int Max = 10;
                        public int Plain() => 1;
                    }
                    public sealed class LeafProcessor : BaseProcessor
                    {
                        public override void Handle() { }
                        protected override void Prepare() { }
                        public new string ToString() => "leaf";
                    }
                }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            // Query level: the column round-trips per member.
            using (var q = new IndexQueries(dbPath))
            {
                var rows = q.Outline("P/Hierarchy.cs");
                string? Mods(string name, string? container = null) =>
                    rows.Single(r => r.Name == name && r.Kind != "namespace" && (container is null || r.Container == container)).Modifiers;
                Assert.Equal("abstract", Mods("BaseProcessor"));
                Assert.Equal("abstract", Mods("Handle", "BaseProcessor"));
                Assert.Equal("virtual", Mods("Prepare", "BaseProcessor"));
                Assert.Equal("static", Mods("Count"));
                Assert.Equal("const", Mods("Max"));
                Assert.Null(Mods("Plain")); // no modifiers -> null, not ""
                Assert.Equal("sealed", Mods("LeafProcessor"));
                Assert.Equal("override", Mods("Handle", "LeafProcessor"));
                Assert.Equal("override", Mods("Prepare", "LeafProcessor"));
                Assert.Equal("new", rows.Single(r => r.Name == "ToString").Modifiers);
            }

            // Tool level: outline nodes and search_symbol hits carry (and omit) the field.
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 15000));
            var tools = new NavigationTools(m, new SemanticService(m));

            var outline = Parse(tools.Outline("P/Hierarchy.cs"));
            var types = outline.GetProperty("symbols")[0].GetProperty("members").EnumerateArray().ToList();
            var baseType = types.Single(t => t.GetProperty("name").GetString() == "BaseProcessor");
            Assert.Equal("abstract", baseType.GetProperty("modifiers").GetString());
            var handle = baseType.GetProperty("members").EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "Handle");
            Assert.Equal("abstract", handle.GetProperty("modifiers").GetString());
            var plain = baseType.GetProperty("members").EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "Plain");
            Assert.False(plain.TryGetProperty("modifiers", out _)); // WhenWritingNull omission

            var hit = Parse(tools.SearchSymbol("LeafProcessor", match: "exact"))
                .GetProperty("symbols")[0];
            Assert.Equal("sealed", hit.GetProperty("modifiers").GetString());
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ 24n

    [Fact]
    public void SemanticResponsesReportTiming()
    {
        string root = Directory.CreateTempSubdirectory("codenav-24n").FullName;
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
            File.WriteAllText(Path.Combine(proj, "A.cs"),
                "namespace P { public class Alpha { public void Go() { } } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 15000));
                var tools = new NavigationTools(m, semantic);

                // Deterministic regardless of Roslyn env: an unresolvable target on the FORCED
                // semantic path returns semantic_unavailable — which must now carry timing so the
                // caller can tell "deadline exhausted" apart from "target absent".
                var unavailable = Parse(tools.References(name: "NoSuchSymbolAnywhereXyz", mode: "semantic", timeoutMs: 7000));
                Assert.Equal("semantic_unavailable", unavailable.GetProperty("error").GetString());
                var timing = unavailable.GetProperty("timing");
                Assert.Equal(7000, timing.GetProperty("deadlineMs").GetInt32());
                Assert.True(timing.GetProperty("elapsedMs").GetInt64() >= 0);

                // deadlineMs mirrors the service clamp, so an out-of-range request reports the
                // EFFECTIVE deadline (500..120000), not the caller's fantasy number.
                var clamped = Parse(tools.References(name: "NoSuchSymbolAnywhereXyz", mode: "semantic", timeoutMs: 1));
                Assert.Equal(500, clamped.GetProperty("timing").GetProperty("deadlineMs").GetInt32());

                // Exact-path timing (env-guarded like every semantic test): present on success too.
                var exact = Parse(tools.References(name: "Go", timeoutMs: 90000));
                if (exact.TryGetProperty("meta", out var meta) &&
                    meta.GetProperty("confidence").GetString() == "exact")
                {
                    var t = exact.GetProperty("timing");
                    Assert.Equal(90000, t.GetProperty("deadlineMs").GetInt32());
                    Assert.True(t.GetProperty("elapsedMs").GetInt64() >= 0);
                    // Not exhausted -> no lower-bound hedge, summary is a plain census.
                    Assert.False(exact.TryGetProperty("totalIsLowerBound", out _));
                    Assert.False(exact.GetProperty("summary").GetString()!.StartsWith("at least"),
                        "un-exhausted summaries must not hedge");
                }
            }
            finally { semantic.Dispose(); m.Dispose(); }
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
