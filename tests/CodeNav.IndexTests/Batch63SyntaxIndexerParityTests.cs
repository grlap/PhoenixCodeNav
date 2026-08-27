using System.Text;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Tests;

public sealed class Batch63SyntaxIndexerParityTests
{
    [Fact]
    public void ConversionOperatorDeltaRowsMatchFreshBuildAndExactStorage()
    {
        string root = Directory.CreateTempSubdirectory("codenav-63-conversion-parity").FullName;
        try
        {
            string projectDir = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            string sourcePath = Path.Combine(projectDir, "Conversions.cs");
            File.WriteAllText(sourcePath, InitialConversionSource);

            string deltaDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, deltaDb);

            File.WriteAllText(sourcePath, FinalConversionSource);
            using (var store = new IndexStore(deltaDb, createNew: false))
            {
                RefreshResult refreshed = DeltaRefresher.Refresh(store, root, ["P/Conversions.cs"]);
                Assert.Equal(1, refreshed.ChangedFiles);
                Assert.Equal(0, refreshed.AddedFiles);
                Assert.Equal(0, refreshed.DeletedFiles);
            }
            string[] deltaRows = DumpRows(deltaDb, "P/Conversions.cs");

            string fullDb = Path.Combine(root, ".codenav", "conversion-full-rebuild.db");
            IndexBuilder.Build(root, fullDb);
            string[] fullRows = DumpRows(fullDb, "P/Conversions.cs");

            Assert.Equal(fullRows, deltaRows);
            Assert.DoesNotContain("context_key", SymbolTableColumns(deltaDb));
            Assert.DoesNotContain("context_key", SymbolTableColumns(fullDb));
            Assert.Equal(
            [
                StoredConversion(
                    "implicit operator Scalar<T>",
                    "implicit operator Scalar<T>(int value)", 5,
                    "operator\u001e\u001eimplicit operator Scalar\u001d<\u001d$type0_0\u001d>\u001e0\u001eint",
                    "struct", "Scalar", "Scalar"),
                StoredConversion(
                    "explicit operator int",
                    "explicit operator int(Scalar<T> value)", 6,
                    "operator\u001e\u001eexplicit operator int\u001e0\u001eScalar\u001d<\u001d$type0_0\u001d>",
                    "struct", "Scalar", "Scalar"),
                StoredConversion(
                    "explicit operator long",
                    "explicit operator long(Scalar<T> value)", 7,
                    "operator\u001e\u001eexplicit operator long\u001e0\u001eScalar\u001d<\u001d$type0_0\u001d>",
                    "struct", "Scalar", "Scalar"),
                StoredConversion(
                    "implicit operator Nested",
                    "implicit operator Nested(string value)", 11,
                    "operator\u001e\u001eimplicit operator Nested\u001e0\u001estring",
                    "struct", "Nested", "Scalar.Nested"),
            ], deltaRows.Where(row => row.StartsWith("operator\u001f", StringComparison.Ordinal)).ToArray());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ExplicitInterfaceOperatorAccessibilityPersistsAsPrivate()
    {
        string root = Directory.CreateTempSubdirectory("codenav-63-conversion-access").FullName;
        try
        {
            string projectDir = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework><LangVersion>preview</LangVersion>" +
                "</PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(projectDir, "Conversions.cs"),
                """
                namespace ConversionAccess;
                public interface IConvert<TSelf> where TSelf : IConvert<TSelf>
                {
                    static abstract explicit operator int(TSelf value);
                }
                public interface IAdd<TSelf> where TSelf : IAdd<TSelf>
                {
                    static abstract TSelf operator +(TSelf left, TSelf right);
                }
                public readonly struct Value : IConvert<Value>, IAdd<Value>
                {
                    public static explicit operator int(Value value) => 0;
                    public static explicit operator checked int(Value value) => 0;
                    static explicit IConvert<Value>.operator int(Value value) => 0;
                    public static Value operator +(Value left, Value right) => default;
                    static Value IAdd<Value>.operator +(Value left, Value right) => default;
                }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var queries = new IndexQueries(dbPath);
            SymbolHit[] implementations = queries
                .SearchSymbols("explicit operator int", "exact", ["operator"], 10)
                .Where(hit => hit.Container == "Value")
                .ToArray();
            Assert.Equal(2, implementations.Length);
            Assert.Equal("public", implementations.Single(hit =>
                hit.Signature == "explicit operator int(Value value)").Accessibility);
            Assert.Equal("private", implementations.Single(hit =>
                hit.Signature == "explicit IConvert<Value>.operator int(Value value)").Accessibility);

            SymbolHit checkedConversion = Assert.Single(queries.SearchSymbols(
                "explicit operator checked int", "exact", ["operator"], 10));
            Assert.Equal("public", checkedConversion.Accessibility);
            Assert.Equal(3, implementations.Append(checkedConversion)
                .Select(hit => hit.DeclarationKey).Distinct(StringComparer.Ordinal).Count());

            SymbolHit[] additions = queries
                .SearchSymbols("operator +", "exact", ["operator"], 10)
                .Where(hit => hit.Container == "Value")
                .ToArray();
            Assert.Equal(2, additions.Length);
            Assert.Equal("public", additions.Single(hit =>
                hit.Signature == "Value operator +(Value left, Value right)")
                .Accessibility);
            Assert.Equal("private", additions.Single(hit =>
                hit.Signature ==
                "Value IAdd<Value>.operator +(Value left, Value right)")
                .Accessibility);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ConversionOperatorHandlesPinSemanticDefinitionsAndReferences()
    {
        string root = Directory.CreateTempSubdirectory("codenav-63-conversion-handles").FullName;
        try
        {
            string lib = Path.Combine(root, "Lib");
            string consumer = Path.Combine(root, "Consumer");
            string fsharpTests = Path.Combine(root, "FSharpTests");
            Directory.CreateDirectory(lib);
            Directory.CreateDirectory(consumer);
            Directory.CreateDirectory(fsharpTests);
            File.WriteAllText(Path.Combine(lib, "Lib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <LangVersion>preview</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            string targetAtSignatureCap = "Target" + new string('X', 362);
            string targetPastSignatureCap = "Target" + new string('Y', 363);
            string sameLineTarget = "Target" + new string('Z', 362);
            string sameLineSourcePrefix = "Source" + new string('Q', 40);
            string sameLineSourceAlpha = sameLineSourcePrefix + "Alpha";
            string sameLineSourceBeta = sameLineSourcePrefix + "Beta";
            File.WriteAllText(Path.Combine(lib, "Conversions.cs"),
                $$"""
                namespace ConversionHandles;

                public readonly struct {{targetAtSignatureCap}} { }
                public readonly struct {{targetPastSignatureCap}} { }
                public readonly struct {{sameLineSourceAlpha}} { }
                public readonly struct {{sameLineSourceBeta}} { }
                public readonly struct {{sameLineTarget}}
                {
                    public static explicit operator {{sameLineTarget}}({{sameLineSourceAlpha}} value) => default; public static explicit operator {{sameLineTarget}}({{sameLineSourceBeta}} value) => default;
                }

                public interface IConvert<TSelf> where TSelf : IConvert<TSelf>
                {
                    static abstract explicit operator int(TSelf value);
                }

                public readonly struct Scalar
                {
                    private readonly int _value;
                    public Scalar(int value) => _value = value;
                    public static implicit operator Scalar(int value) => new(value);
                    public static explicit operator int(Scalar value) => value._value;
                    public static explicit operator long(Scalar value) => value._value;
                    public static explicit operator checked long(Scalar value) => value._value;
                    public static explicit operator {{targetAtSignatureCap}}(Scalar value) => default;
                    public static explicit operator {{targetPastSignatureCap}}(Scalar value) => default;
                }

                public readonly struct InterfaceScalar : IConvert<InterfaceScalar>
                {
                    static explicit IConvert<InterfaceScalar>.operator int(InterfaceScalar value) => 42;
                }

                public readonly struct ImplicitInterfaceScalar : IConvert<ImplicitInterfaceScalar>
                {
                    public static explicit operator int(ImplicitInterfaceScalar value) => 24;
                }

                public readonly struct LoopValue
                {
                    public static implicit operator int(LoopValue value) => 0;
                }

                public readonly struct ExplicitLoopValue
                {
                    public static explicit operator int(ExplicitLoopValue value) => 0;
                }

                public readonly struct CheckedLoopValue
                {
                    public static explicit operator int(CheckedLoopValue value) => 0;
                    public static explicit operator checked int(CheckedLoopValue value) => 0;
                }

                public readonly struct StackSource
                {
                    public static explicit operator StackMid(StackSource value) => default;
                }

                public readonly struct StackMid
                {
                    public static implicit operator StackTarget(StackMid value) => default;
                }

                public readonly struct StackTarget { }

                public readonly struct DeconstructValue
                {
                    public static implicit operator int(DeconstructValue value) => 0;
                }

                public readonly struct CompoundValue
                {
                    private readonly int _value;
                    private CompoundValue(int value) => _value = value;
                    public static implicit operator int(CompoundValue value) => value._value;
                    public static implicit operator CompoundValue(int value) => new(value);
                }
                """);
            File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(consumer, "Use.cs"),
                """
                using System.Collections.Generic;
                using System.Runtime.CompilerServices;
                using System.Threading.Tasks;
                using ConversionHandles;
                namespace ConversionConsumer;
                public sealed class DeconstructSource
                {
                    public void Deconstruct(
                        out DeconstructValue left, out DeconstructValue right) =>
                        (left, right) = (default, default);
                }
                public class PrimaryBase
                {
                    public PrimaryBase(int value) { }
                }
                public sealed class PrimaryDerived(LoopValue value) : PrimaryBase(value) { }
                public sealed class AsyncLoopValues<T>
                {
                    public AsyncLoopEnumerator<T> GetAsyncEnumerator() => new();
                }
                public sealed class AsyncLoopEnumerator<T>
                {
                    public T Current => default!;
                    public BoolAwaitable MoveNextAsync() => default;
                    public VoidAwaitable DisposeAsync() => default;
                }
                public readonly struct BoolAwaitable
                {
                    public BoolAwaiter GetAwaiter() => default;
                }
                public readonly struct BoolAwaiter : INotifyCompletion
                {
                    public bool IsCompleted => true;
                    public bool GetResult() => false;
                    public void OnCompleted(System.Action continuation) => continuation();
                }
                public readonly struct VoidAwaitable
                {
                    public VoidAwaiter GetAwaiter() => default;
                }
                public readonly struct VoidAwaiter : INotifyCompletion
                {
                    public bool IsCompleted => true;
                    public void GetResult() { }
                    public void OnCompleted(System.Action continuation) => continuation();
                }
                public static class Use
                {
                    public static void Run()
                    {
                        Scalar value = 7;
                        _ = (int)value;
                        _ = checked((long)value);
                        CompoundValue compound = 1;
                        compound += 1;
                    }

                    public static int ThroughInterface<T>(T value) where T : IConvert<T> => (int)value;
                    public static int RunInterface() => ThroughInterface(default(InterfaceScalar));
                    public static int RunImplicitInterface() =>
                        ThroughInterface(default(ImplicitInterfaceScalar));
                    public static void Loop(IEnumerable<LoopValue> values)
                    {
                        foreach (int item in values) _ = item;
                    }
                    public static async Task LoopAsync(AsyncLoopValues<LoopValue> values)
                    {
                        await foreach (int item in values) _ = item;
                    }
                    public static void ExplicitLoop(IEnumerable<ExplicitLoopValue> values)
                    {
                        foreach (int item in values) _ = item;
                    }
                    public static async Task ExplicitLoopAsync(
                        AsyncLoopValues<ExplicitLoopValue> values)
                    {
                        await foreach (int item in values) _ = item;
                    }
                    public static void CheckedLoop(IEnumerable<CheckedLoopValue> values)
                    {
                        checked
                        {
                            foreach (int item in values) _ = item;
                        }
                    }
                    public static async Task CheckedLoopAsync(
                        AsyncLoopValues<CheckedLoopValue> values)
                    {
                        checked
                        {
                            await foreach (int item in values) _ = item;
                        }
                    }
                    public static int[] Spread(IEnumerable<LoopValue> values) => [.. values];
                    public static int Coalesce(CompoundValue? value) => value ?? 0;
                    public static StackTarget Stack(StackSource value) => (StackMid)value;
                    public static void Deconstruct(
                        IEnumerable<(DeconstructValue Left, DeconstructValue Right)> values)
                    {
                        (int first, int second) = (new DeconstructValue(), new DeconstructValue());
                        (int third, int fourth) = new DeconstructSource();
                        (DeconstructValue Left, DeconstructValue Right) pair = default;
                        (int fifth, int sixth) = pair;
                        foreach ((int left, int right) in values) _ = left + right;
                    }
                    public static (int Left, int Right)? NullableTuple(
                        (DeconstructValue Left, DeconstructValue Right)? pair) => pair;
                }
                """);
            File.WriteAllText(Path.Combine(fsharpTests, "Consumer.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>consumer</AssemblyName>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <ProjectReference Include="../Lib/Lib.csproj" />
                    <PackageReference Include="xunit" Version="2.9.0" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(fsharpTests, "Use.fs"),
                """
                namespace ConversionFSharpTests
                module Use =
                    let value = 1
                """);

            AssertConversionFixtureCompiles(
                Path.Combine(lib, "Conversions.cs"),
                Path.Combine(consumer, "Use.cs"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Equal("Consumer", Assert.Single(
                    queries.ProjectsContaining("Consumer/Use.cs")).Name);
                Assert.Equal("consumer", Assert.Single(
                    queries.ProjectsContaining("FSharpTests/Use.fs")).Name);
                Assert.True(queries.AllProjectTestFlags()["Consumer"]);
                Assert.False(queries.AllProjectTestOnlyFlags()["Consumer"]);
            }
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            manager.Start();
            for (int i = 0; i < 600 && !manager.IsQueryable; i++) Thread.Sleep(50);
            Assert.True(manager.IsQueryable, "conversion-handle index did not become queryable");
            var tools = new NavigationTools(manager, semantic);

            // Every handle must survive both semantic entry points and pin the same declaration.
            // Positive compiler-operation scans prove implicit assignment, explicit cast, and
            // checked-cast sites independently; unused conversions prove an exact zero.
            AssertSemanticHandle(tools, "implicit operator Scalar",
                "implicit operator Scalar(int value)",
                SemanticReferenceKinds.ImplicitConversion);
            AssertSemanticHandle(tools, "explicit operator int",
                "explicit operator int(Scalar value)",
                SemanticReferenceKinds.ExplicitConversion);
            AssertSemanticHandle(tools, "explicit operator checked long",
                "explicit operator checked long(Scalar value)",
                SemanticReferenceKinds.CheckedConversion);
            JsonElement loopHit = IndexedOperatorHit(tools, "implicit operator int",
                "implicit operator int(LoopValue value)");
            semantic.TestOnlyConversionSiteDiscovered = total =>
            {
                if (total >= 1) throw new OperationCanceledException();
            };
            JsonElement partialLoop = SemanticRetry.ParseWithRetry(
                () => tools.References(symbolId: loopHit.GetProperty("symbolId").GetString(),
                    mode: "semantic", timeoutMs: 90_000, includeTests: false),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        (reason.GetString() ?? "").Contains("semantic_timeout",
                            StringComparison.Ordinal),
                "conversion-operation deadline salvage");
            Assert.Equal(1, partialLoop.GetProperty("totalReferences").GetInt32());
            Assert.True(partialLoop.GetProperty("partial").GetBoolean());
            Assert.True(partialLoop.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Equal("indexed", partialLoop.GetProperty("meta")
                .GetProperty("confidence").GetString());
            semantic.TestOnlyConversionSiteDiscovered = null;

            AssertSemanticHandle(tools, "implicit operator int",
                "implicit operator int(LoopValue value)",
                SemanticReferenceKinds.ImplicitConversion, expectedTotal: 4);
            AssertSemanticHandle(tools, "explicit operator int",
                "explicit operator int(ExplicitLoopValue value)",
                SemanticReferenceKinds.ExplicitConversion, expectedTotal: 2);
            AssertSemanticHandle(tools, "explicit operator checked int",
                "explicit operator checked int(CheckedLoopValue value)",
                SemanticReferenceKinds.CheckedConversion, expectedTotal: 2);
            AssertSemanticHandle(tools, "explicit operator int",
                "explicit operator int(CheckedLoopValue value)", expectedTotal: 0);
            AssertSemanticHandle(tools, "explicit operator StackMid",
                "explicit operator StackMid(StackSource value)",
                SemanticReferenceKinds.ExplicitConversion);
            AssertSemanticHandle(tools, "implicit operator StackTarget",
                "implicit operator StackTarget(StackMid value)",
                SemanticReferenceKinds.ImplicitConversion);
            AssertSemanticHandle(tools, "implicit operator int",
                "implicit operator int(CompoundValue value)",
                SemanticReferenceKinds.ImplicitConversion, expectedTotal: 2);
            AssertSemanticHandle(tools, "implicit operator CompoundValue",
                "implicit operator CompoundValue(int value)",
                SemanticReferenceKinds.ImplicitConversion, expectedTotal: 3);
            // The valid async-enumerable fixture lets Roslyn bind the entire Consumer compilation.
            // CompoundValue(int) then has assignment, compound-assignment writeback, and nullable
            // coalesce sites. DeconstructValue(int) has 2 tuple assignment + 2 DeconstructSource +
            // 2 named-pair + 2 foreach + 1 lifted nullable-tuple sites = 9. These counts deliberately
            // pin compiler-bound operations, including distinct tuple elements on one physical line.
            int identityCacheHits = 0;
            int identityCacheMisses = 0;
            semantic.TestOnlyConversionIdentityCacheLookup = hit =>
            {
                if (hit) identityCacheHits++;
                else identityCacheMisses++;
            };
            var deconstructionSites = new Dictionary<string, int>(StringComparer.Ordinal);
            var deconstructionKinds = new HashSet<string>(StringComparer.Ordinal);
            semantic.TestOnlyConversionSiteAdded = (location, kind) =>
            {
                string sourceLine = location.SourceTree!.GetText()
                    .Lines.GetLineFromPosition(location.SourceSpan.Start).ToString().Trim();
                deconstructionSites[sourceLine] =
                    deconstructionSites.GetValueOrDefault(sourceLine) + 1;
                deconstructionKinds.Add(kind);
            };
            AssertSemanticHandle(tools, "implicit operator int",
                "implicit operator int(DeconstructValue value)",
                SemanticReferenceKinds.ImplicitConversion, expectedTotal: 9);
            semantic.TestOnlyConversionSiteAdded = null;
            semantic.TestOnlyConversionIdentityCacheLookup = null;
            Assert.True(identityCacheMisses > 0);
            Assert.True(identityCacheHits >= 8,
                $"expected the nine repeated conversion sites to reuse identity; " +
                $"hits={identityCacheHits}, misses={identityCacheMisses}");
            Assert.Equal(SemanticReferenceKinds.ImplicitConversion,
                Assert.Single(deconstructionKinds));
            Assert.Equal(2, SiteCount("(int first, int second)"));
            Assert.Equal(2, SiteCount("(int third, int fourth)"));
            Assert.Equal(2, SiteCount("(int fifth, int sixth)"));
            Assert.Equal(2, SiteCount("foreach ((int left, int right)"));
            Assert.Equal(1, SiteCount("(DeconstructValue Left, DeconstructValue Right)? pair)"));
            Assert.Equal(9, deconstructionSites.Values.Sum());

            int SiteCount(string sourceFragment) => deconstructionSites
                .Where(pair => pair.Key.Contains(sourceFragment, StringComparison.Ordinal))
                .Sum(pair => pair.Value);

            int explicitInterfaceLine = AssertSemanticHandle(tools, "explicit operator int",
                "explicit IConvert<InterfaceScalar>.operator int(InterfaceScalar value)",
                SemanticReferenceKinds.ExplicitConversion, expectedTotal: 1);
            int implicitInterfaceLine = AssertSemanticHandle(tools, "explicit operator int",
                "explicit operator int(ImplicitInterfaceScalar value)",
                SemanticReferenceKinds.ExplicitConversion, expectedTotal: 1);
            string signatureAtCap = $"explicit operator {targetAtSignatureCap}(Scalar value)";
            string signaturePastCap = $"explicit operator {targetPastSignatureCap}(Scalar value)";
            Assert.Equal(400, signatureAtCap.Length);
            Assert.Equal(401, signaturePastCap.Length);
            AssertSemanticHandle(tools, $"explicit operator {targetAtSignatureCap}", signatureAtCap);
            AssertSemanticHandle(tools, $"explicit operator {targetPastSignatureCap}", signaturePastCap);
            AssertSameLineCappedSemanticHandles(tools, sameLineTarget,
                sameLineSourceAlpha, sameLineSourceBeta);

            // Public position/name routes carry no indexed declaration key, and Roslyn exposes an
            // explicit-interface conversion as MethodKind.ExplicitInterfaceImplementation. Both
            // routes must still identify conversion syntax and prove the unused declaration's zero.
            AssertExactOperatorReferences(() => tools.References(
                    path: "Lib/Conversions.cs", line: explicitInterfaceLine,
                    mode: "semantic", timeoutMs: 90_000, includeTests: false),
                explicitInterfaceLine,
                expectedProjects: 2, expectedTotal: 1,
                expectedKind: SemanticReferenceKinds.ExplicitConversion);
            AssertExactOperatorReferences(() => tools.References(
                    name: "explicit operator int", path: "Lib/Conversions.cs",
                    line: explicitInterfaceLine, mode: "semantic", timeoutMs: 90_000,
                    includeTests: false),
                explicitInterfaceLine, expectedProjects: 2, expectedTotal: 1,
                expectedKind: SemanticReferenceKinds.ExplicitConversion);
            AssertExactOperatorReferences(() => tools.References(
                    path: "Lib/Conversions.cs", line: implicitInterfaceLine,
                    mode: "semantic", timeoutMs: 90_000, includeTests: false),
                implicitInterfaceLine,
                expectedProjects: 2, expectedTotal: 1,
                expectedKind: SemanticReferenceKinds.ExplicitConversion);
            AssertExactOperatorReferences(() => tools.References(
                    name: "explicit operator int", path: "Lib/Conversions.cs",
                    line: implicitInterfaceLine, mode: "semantic", timeoutMs: 90_000,
                    includeTests: false),
                implicitInterfaceLine, expectedProjects: 2, expectedTotal: 1,
                expectedKind: SemanticReferenceKinds.ExplicitConversion);

            string telemetryLine = manager.Telemetry.Snapshot().Last(line =>
                line.Contains("\"tool\":\"references\"", StringComparison.Ordinal));
            using JsonDocument telemetry = JsonDocument.Parse(telemetryLine);
            Assert.Equal("exact", telemetry.RootElement.GetProperty("result").GetString());
            Assert.False(telemetry.RootElement.TryGetProperty("reason", out _));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ConversionIdentityFallbackDistinguishesSameNameAssemblies()
    {
        using var workspace = new AdhocWorkspace();
        ProjectId firstId = ProjectId.CreateNewId("FirstTwin");
        ProjectId secondId = ProjectId.CreateNewId("SecondTwin");
        ProjectId consumerId = ProjectId.CreateNewId("Consumer");
        MetadataReference core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        Solution solution = workspace.CurrentSolution;
        solution = solution.AddProject(ProjectInfo.Create(firstId, VersionStamp.Create(),
                "FirstTwin", "TwinConversions", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary), metadataReferences: [core]))
            .AddDocument(DocumentId.CreateNewId(firstId), "Value.cs", SourceText.From(
                "namespace Twin; public readonly struct Value { " +
                "public static explicit operator int(Value value) => 0; }"));
        solution = solution.AddProject(ProjectInfo.Create(secondId, VersionStamp.Create(),
                "SecondTwin", "TwinConversions", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary), metadataReferences: [core]))
            .AddDocument(DocumentId.CreateNewId(secondId), "Value.cs", SourceText.From(
                "namespace Twin; public readonly struct Value { " +
                "public static explicit operator int(Value value) => 0; }"));
        DocumentId useId = DocumentId.CreateNewId(consumerId);
        solution = solution.AddProject(ProjectInfo.Create(consumerId, VersionStamp.Create(),
                "Consumer", "Consumer", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary), metadataReferences: [core]))
            .AddProjectReference(consumerId, new ProjectReference(firstId))
            .AddDocument(useId, "Use.cs", SourceText.From(
                "using Twin; public static class Use { " +
                "public static int Run(Value value) => (int)value; }"));
        Assert.True(workspace.TryApplyChanges(solution));
        solution = workspace.CurrentSolution;

        IMethodSymbol first = ConversionOperator(await solution.GetProject(firstId)!
            .GetCompilationAsync());
        IMethodSymbol second = ConversionOperator(await solution.GetProject(secondId)!
            .GetCompilationAsync());
        Document use = solution.GetDocument(useId)!;
        SyntaxNode root = (await use.GetSyntaxRootAsync())!;
        SemanticModel model = (await use.GetSemanticModelAsync())!;
        CastExpressionSyntax cast = root.DescendantNodes().OfType<CastExpressionSyntax>().Single();
        IMethodSymbol retargeted = Assert.IsAssignableFrom<IConversionOperation>(
            model.GetOperation(cast)).OperatorMethod!;

        Assert.True(await SemanticService.SameUserDefinedConversionAsync(
            retargeted, first, solution, CancellationToken.None));
        Assert.False(await SemanticService.SameUserDefinedConversionAsync(
            second, first, solution, CancellationToken.None));

        static IMethodSymbol ConversionOperator(Compilation? compilation) =>
            compilation!.GetTypeByMetadataName("Twin.Value")!.GetMembers()
                .OfType<IMethodSymbol>().Single(method => method.MethodKind ==
                    MethodKind.Conversion);
    }

    [Fact]
    public void RegularOperatorHandlesPinExactDeclarationsOrRejectUnsupportedTools()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-63-regular-operator-handles").FullName;
        try
        {
            string project = Path.Combine(root, "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework><LangVersion>preview</LangVersion>" +
                "</PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(project, "Operators.cs"),
                """
                namespace RegularOperatorHandles;
                public interface IAdd<TSelf> where TSelf : IAdd<TSelf>
                {
                    static abstract TSelf operator +(TSelf left, TSelf right);
                }
                public readonly struct Alpha { }
                public readonly struct Beta { }
                public readonly struct Box : IAdd<Box>
                {
                    public static Box operator +(Box left, Alpha right) => default; public static Box operator +(Box left, Beta right) => default;
                    public static Box operator +(Box left, Box right) => default;
                    public static Box operator checked +(Box left, Box right) => default;
                    static Box IAdd<Box>.operator +(Box left, Box right) => default;
                }
                public static class Use
                {
                    public static Box AddAlpha(Box left, Alpha right) => left + right;
                    public static Box AddChecked(Box left, Box right) => checked(left + right);
                    public static T AddGeneric<T>(T left, T right) where T : IAdd<T> => left + right;
                }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            // A freshly opened index is queryable while its mandatory startup sweep is still
            // converging. Exact semantic handles intentionally refuse that stale window, so wait
            // for the lifecycle state the assertions below actually require.
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready", 30_000),
                manager.Health().Error);
            var tools = new NavigationTools(manager, semantic);

            JsonElement[] ordinaryHits = ParseJson(tools.SearchSymbol(
                    "operator +", kinds: "operator", match: "exact", limit: 20))
                .GetProperty("symbols").EnumerateArray()
                .Where(hit => hit.GetProperty("containingType").GetString() == "Box")
                .ToArray();
            JsonElement alpha = Assert.Single(ordinaryHits, hit =>
                hit.GetProperty("signature").GetString() ==
                "Box operator +(Box left, Alpha right)");
            JsonElement beta = Assert.Single(ordinaryHits, hit =>
                hit.GetProperty("signature").GetString() ==
                "Box operator +(Box left, Beta right)");
            Assert.Equal(alpha.GetProperty("startLine").GetInt32(),
                beta.GetProperty("startLine").GetInt32());

            string alphaDocumentationId = AssertRegularOperatorHandle(
                tools, alpha, expectedReferences: 1);
            string betaDocumentationId = AssertRegularOperatorHandle(
                tools, beta, expectedReferences: 0);
            Assert.NotEqual(alphaDocumentationId, betaDocumentationId);
            Assert.Contains("Alpha", alphaDocumentationId, StringComparison.Ordinal);
            Assert.Contains("Beta", betaDocumentationId, StringComparison.Ordinal);

            JsonElement[] checkedHits = ParseJson(tools.SearchSymbol(
                    "operator checked +", kinds: "operator", match: "exact", limit: 20))
                .GetProperty("symbols").EnumerateArray()
                .ToArray();
            JsonElement checkedHit = Assert.Single(checkedHits,
                hit => hit.GetProperty("containingType").GetString() == "Box");
            Assert.Contains("op_CheckedAddition",
                AssertRegularOperatorHandle(tools, checkedHit, expectedReferences: 1),
                StringComparison.Ordinal);

            JsonElement explicitInterface = Assert.Single(ordinaryHits, hit =>
                hit.GetProperty("signature").GetString() ==
                "Box IAdd<Box>.operator +(Box left, Box right)");
            AssertRegularOperatorHandle(tools, explicitInterface,
                expectedReferences: 1);

            string operatorHandle = alpha.GetProperty("symbolId").GetString()!;
            JsonElement implementations = ParseJson(tools.Implementations(
                symbolId: operatorHandle));
            Assert.Equal("unsupported_symbol_kind",
                implementations.GetProperty("error").GetString());
            Assert.Contains("does not model implementations for operator declarations",
                implementations.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            JsonElement interfaceOperator = Assert.Single(ParseJson(tools.SearchSymbol(
                    "operator +", kinds: "operator", match: "exact", limit: 20))
                .GetProperty("symbols").EnumerateArray(), hit =>
                (hit.GetProperty("containingType").GetString() ?? "")
                .StartsWith("IAdd", StringComparison.Ordinal));
            JsonElement interfaceImplementations = ParseJson(tools.Implementations(
                symbolId: interfaceOperator.GetProperty("symbolId").GetString()));
            Assert.Equal("unsupported_symbol_kind",
                interfaceImplementations.GetProperty("error").GetString());
            Assert.Contains("static abstract interface operators",
                interfaceImplementations.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            JsonElement hierarchy = ParseJson(tools.TypeHierarchy(
                symbolId: operatorHandle));
            Assert.Equal("bad_request", hierarchy.GetProperty("error").GetString());
            Assert.Contains("type declaration", hierarchy.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ConversionHandleFingerprintRejectsReusedRowAfterFileEpochChanges()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-63-conversion-stale-handle").FullName;
        try
        {
            string project = Path.Combine(root, "Lib");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Lib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            const string intConversion =
                "public static implicit operator Scalar(int value) => new();";
            const string longConversion =
                "public static implicit operator Scalar(long value) => new();";
            // These Marker rows share every declaration-local fingerprint input: same file, line,
            // namespace, container display name, declaration key, signature, and local arity. The
            // file hash changes when the containing declarations are reordered, conservatively
            // invalidating every handle from the previous file epoch.
            const string plainMarker =
                "namespace ContextStale { public class Outer { public class Marker {} } }";
            const string genericMarker =
                "namespace ContextStale { public class Outer<T> { public class Marker {} } }";
            string sourcePath = Path.Combine(project, "Conversions.cs");
            File.WriteAllText(sourcePath,
                $"{plainMarker} {genericMarker} namespace ConversionStale {{ " +
                $"public readonly struct Scalar {{ {intConversion} {longConversion} }} }}");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            for (int i = 0; i < 600 && !manager.IsQueryable; i++) Thread.Sleep(50);
            Assert.True(manager.IsQueryable, "stale-handle index did not become queryable");
            var tools = new NavigationTools(manager, semantic);

            JsonElement initialInt = IndexedConversionHit(tools,
                "implicit operator Scalar(int value)");
            string staleIntHandle = initialInt.GetProperty("symbolId").GetString()!;
            string staleRowHandle = staleIntHandle[..staleIntHandle.IndexOf('~')];
            string legacyIntFingerprint = LegacyFingerprint(
                initialInt.GetProperty("name").GetString()!,
                initialInt.GetProperty("kind").GetString()!,
                initialInt.GetProperty("arity").GetInt32(),
                initialInt.GetProperty("startLine").GetInt32(),
                initialInt.GetProperty("path").GetString()!);
            Assert.Equal("stale_handle", ParseJson(tools.Definition(
                    symbolId: staleRowHandle + "~" + legacyIntFingerprint,
                    mode: "indexed"))
                .GetProperty("error").GetString());
            JsonElement initialPlainMarker = MarkerHitByParentArity(
                tools, manager, parentArity: 0);
            JsonElement initialGenericMarker = MarkerHitByParentArity(
                tools, manager, parentArity: 1);
            string stalePlainMarkerHandle = initialPlainMarker.GetProperty("symbolId").GetString()!;
            string stalePlainMarkerRow = stalePlainMarkerHandle[
                ..stalePlainMarkerHandle.IndexOf('~')];
            using (var queries = manager.OpenQueries())
            {
                SymbolHit plain = queries.SymbolById(IndexedRowId(initialPlainMarker))!;
                SymbolHit generic = queries.SymbolById(IndexedRowId(initialGenericMarker))!;
                Assert.Equal(plain.Kind, generic.Kind);
                Assert.Equal(plain.Name, generic.Name);
                Assert.Equal(plain.Ns, generic.Ns);
                Assert.Equal(plain.Container, generic.Container);
                Assert.Equal(plain.Signature, generic.Signature);
                Assert.Equal(plain.Arity, generic.Arity);
                Assert.Equal(plain.StartLine, generic.StartLine);
                Assert.Equal(plain.FilePath, generic.FilePath);
                Assert.Equal(plain.DeclarationKey, generic.DeclarationKey);
                Assert.NotEqual(0, plain.FileHash);
                Assert.Equal(plain.FileHash, generic.FileHash);
            }

            using var fullRebuildCompleted = new ManualResetEventSlim();
            manager.FullRebuildCompletedForTest = () => fullRebuildCompleted.Set();
            File.WriteAllText(sourcePath,
                $"{genericMarker} {plainMarker} namespace ConversionStale {{ " +
                $"public readonly struct Scalar {{ {longConversion} {intConversion} }} }}");
            Assert.True(manager.RequestFullRebuild());
            bool reordered = false;
            for (int i = 0; i < 1200 && !reordered; i++)
            {
                Thread.Sleep(50);
                // The watcher may publish this file through delta refresh before the queued full
                // rebuild. Row-id reuse is meaningful only after that requested rebuild completes.
                if (!fullRebuildCompleted.IsSet || !manager.IsQueryable) continue;
                using var queries = manager.OpenQueries();
                string content = queries.ContentByPath("Lib/Conversions.cs") ?? "";
                reordered = content.IndexOf(longConversion, StringComparison.Ordinal) <
                            content.IndexOf(intConversion, StringComparison.Ordinal);
            }
            Assert.True(reordered,
                "requested full rebuild did not publish the reordered conversions");

            JsonElement currentLong = IndexedConversionHit(tools,
                "implicit operator Scalar(long value)");
            string currentLongHandle = currentLong.GetProperty("symbolId").GetString()!;
            Assert.Equal(staleRowHandle,
                currentLongHandle[..currentLongHandle.IndexOf('~')]);
            Assert.Equal("stale_handle", ParseJson(tools.Definition(
                    symbolId: staleIntHandle, mode: "indexed"))
                .GetProperty("error").GetString());
            JsonElement currentGenericMarker = MarkerHitByParentArity(
                tools, manager, parentArity: 1);
            string currentGenericMarkerHandle = currentGenericMarker
                .GetProperty("symbolId").GetString()!;
            Assert.Equal(stalePlainMarkerRow,
                currentGenericMarkerHandle[..currentGenericMarkerHandle.IndexOf('~')]);
            Assert.Equal("stale_handle", ParseJson(tools.Definition(
                    symbolId: stalePlainMarkerHandle, mode: "indexed"))
                .GetProperty("error").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ConversionHandleFingerprintRejectsReusedRowWhenTwinFileIsUnchanged()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-63-conversion-stale-unchanged-file").FullName;
        try
        {
            string project = Path.Combine(root, "Lib");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Lib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            string precedingPath = Path.Combine(project, "A.cs");
            string precedingSource =
                "public class ShiftOne {} public class ShiftTwo {} public class ShiftThree {} " +
                new string(' ', 256);
            File.WriteAllText(precedingPath, precedingSource);
            string twinsPath = Path.Combine(project, "Z.cs");
            File.WriteAllText(twinsPath,
                "namespace ContextStale { public class Outer { public class Marker {} } } " +
                "namespace ContextStale { public class Outer<T> { public class Marker {} } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            var buildHooks = new BuildCaptureTestHooks(
                (workspaceRoot, gitPath, maxBytes) =>
                    GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath, maxBytes),
                CSharpProducerMaxDegreeOfParallelism: 1);
            IndexBuilder.BuildWithSourceBatchSizeForTest(
                root, sourceWriteBatchSize: 1, buildCaptureTestHooks: buildHooks);

            string stalePlainHandle;
            string stalePlainRow;
            long originalFileHash;
            long plainOrdinal;
            long genericOrdinal;
            using (var manager = new IndexManager(root, dbPath))
            using (var semantic = new SemanticService(manager))
            {
                manager.Start();
                Assert.True(SpinWait.SpinUntil(() => manager.IsQueryable, 20_000));
                var tools = new NavigationTools(manager, semantic);
                JsonElement plain = MarkerHitByParentArity(tools, manager, parentArity: 0);
                JsonElement generic = MarkerHitByParentArity(tools, manager, parentArity: 1);
                stalePlainHandle = plain.GetProperty("symbolId").GetString()!;
                stalePlainRow = stalePlainHandle[..stalePlainHandle.IndexOf('~')];
                using var queries = manager.OpenQueries();
                SymbolHit plainHit = queries.SymbolById(IndexedRowId(plain))!;
                SymbolHit genericHit = queries.SymbolById(IndexedRowId(generic))!;
                originalFileHash = plainHit.FileHash;
                plainOrdinal = plainHit.OrdinalOnLine;
                genericOrdinal = genericHit.OrdinalOnLine;
                Assert.Equal(3, genericOrdinal - plainOrdinal);
            }

            // Remove exactly three symbols from an earlier file. The full rebuild shifts Z.cs row
            // ids by three while Z.cs itself remains byte-identical; the old plain Marker row id
            // now lands on the generic twin.
            File.WriteAllText(precedingPath,
                "// no declarations remain".PadRight(precedingSource.Length));
            IndexBuilder.BuildWithSourceBatchSizeForTest(
                root, sourceWriteBatchSize: 1, buildCaptureTestHooks: buildHooks);

            using (var manager = new IndexManager(root, dbPath))
            using (var semantic = new SemanticService(manager))
            {
                manager.Start();
                Assert.True(SpinWait.SpinUntil(() => manager.IsQueryable, 20_000));
                var tools = new NavigationTools(manager, semantic);
                JsonElement currentGeneric = MarkerHitByParentArity(
                    tools, manager, parentArity: 1);
                string currentGenericHandle = currentGeneric.GetProperty("symbolId").GetString()!;
                Assert.Equal(stalePlainRow,
                    currentGenericHandle[..currentGenericHandle.IndexOf('~')]);
                using (var queries = manager.OpenQueries())
                {
                    SymbolHit current = queries.SymbolById(IndexedRowId(currentGeneric))!;
                    Assert.Equal(originalFileHash, current.FileHash);
                    Assert.Equal(genericOrdinal, current.OrdinalOnLine);
                    Assert.NotEqual(plainOrdinal, current.OrdinalOnLine);
                }
                Assert.Equal("stale_handle", ParseJson(tools.Definition(
                        symbolId: stalePlainHandle, mode: "indexed"))
                    .GetProperty("error").GetString());
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void IndexedReferencesDiscloseCandidateFileCapAtBoundaryAndOverflow()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-63-reference-candidate-cap").FullName;
        try
        {
            string atCap = Path.Combine(root, "AtCap");
            string overflow = Path.Combine(root, "Overflow");
            Directory.CreateDirectory(atCap);
            Directory.CreateDirectory(overflow);
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(atCap, $"Use{i:D2}.cs"),
                    $"// ReferenceCandidateCapMarker\nnamespace CandidateCap; class Use{i:D2} {{ }}\n");
            }
            File.WriteAllText(Path.Combine(overflow, "Use10.cs"),
                "// ReferenceCandidateCapMarker\nnamespace CandidateCap; class Use10 { }\n");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement boundary = ParseJson(tools.References(
                name: "ReferenceCandidateCapMarker", mode: "indexed",
                pathGlob: "AtCap/**", maxFiles: 10, samplesPerGroup: 0));
            Assert.Equal(10, boundary.GetProperty("totalCandidates").GetInt32());
            Assert.False(boundary.TryGetProperty("partial", out _));
            Assert.False(boundary.TryGetProperty("totalIsLowerBound", out _));
            JsonElement boundaryCoverage = boundary.GetProperty("coverage");
            Assert.Equal(10, boundaryCoverage.GetProperty("candidateFilesScanned").GetInt32());
            Assert.Equal(10, boundaryCoverage.GetProperty("candidateFilesTotal").GetInt32());
            Assert.Equal(10, boundaryCoverage.GetProperty("candidateFilesAtLeast").GetInt32());
            Assert.False(boundaryCoverage.TryGetProperty("candidateFilesCapHit", out _));

            JsonElement overflowed = ParseJson(tools.References(
                name: "ReferenceCandidateCapMarker", mode: "indexed",
                maxFiles: 10, samplesPerGroup: 0));
            Assert.Equal(10, overflowed.GetProperty("totalCandidates").GetInt32());
            Assert.True(overflowed.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.True(overflowed.GetProperty("partial").GetBoolean());
            Assert.Contains("candidate_file_cap", overflowed.GetProperty("partialReason")
                .GetString());
            Assert.StartsWith("At least 10", overflowed.GetProperty("summary").GetString());
            Assert.Equal("references.candidate_file_cap",
                overflowed.GetProperty("noteId").GetString());
            JsonElement overflowCoverage = overflowed.GetProperty("coverage");
            Assert.Equal(10, overflowCoverage.GetProperty("candidateFilesScanned").GetInt32());
            Assert.False(overflowCoverage.TryGetProperty("candidateFilesTotal", out _));
            Assert.Equal(11, overflowCoverage.GetProperty("candidateFilesAtLeast").GetInt32());
            Assert.Equal(10, overflowCoverage.GetProperty("candidateFileLimit").GetInt32());
            Assert.True(overflowCoverage.GetProperty("candidateFilesCapHit").GetBoolean());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void DeepMixedDeltaRowsMatchFreshBuild()
    {
        string root = Directory.CreateTempSubdirectory("codenav-63-syntax-parity").FullName;
        try
        {
            string projectDir = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            string sourcePath = Path.Combine(projectDir, "Deep.cs");
            File.WriteAllText(sourcePath, DeepSource(includeInsertedMember: false));

            string deltaDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, deltaDb);

            File.WriteAllText(sourcePath, DeepSource(includeInsertedMember: true));
            using (var store = new IndexStore(deltaDb, createNew: false))
            {
                RefreshResult refreshed = DeltaRefresher.Refresh(store, root, ["P/Deep.cs"]);
                Assert.Equal(1, refreshed.ChangedFiles);
                Assert.Equal(0, refreshed.AddedFiles);
                Assert.Equal(0, refreshed.DeletedFiles);
            }
            string[] deltaRows = DumpRows(deltaDb, "P/Deep.cs");

            string fullDb = Path.Combine(root, ".codenav", "full-rebuild.db");
            IndexBuilder.Build(root, fullDb);
            string[] fullRows = DumpRows(fullDb, "P/Deep.cs");

            Assert.Equal(fullRows, deltaRows);
            Assert.Contains(deltaRows, row => row.Contains("method\u001fInsertedMidChain\u001f",
                StringComparison.Ordinal));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static string DeepSource(bool includeInsertedMember)
    {
        const int depth = 50;
        var source = new StringBuilder("namespace DeepParity;\n\n");
        for (int i = 0; i < depth; i++)
        {
            source.Append(' ', i * 2)
                .Append("public class Level").Append(i).Append("<T>")
                .AppendLine()
                .Append(' ', i * 2).AppendLine("{");
        }

        source.Append(' ', depth * 2).AppendLine("int first, second;");
        source.Append(' ', depth * 2).AppendLine("enum State");
        source.Append(' ', depth * 2).AppendLine("{");
        source.Append(' ', depth * 2 + 2).AppendLine("None,");
        source.Append(' ', depth * 2 + 2).AppendLine("Ready");
        source.Append(' ', depth * 2).AppendLine("}");

        for (int i = depth - 1; i >= 0; i--)
        {
            if (includeInsertedMember && i == depth / 2)
            {
                source.Append(' ', (i + 1) * 2)
                    .AppendLine("void InsertedMidChain() { }");
            }
            source.Append(' ', i * 2).AppendLine("}");
        }
        return source.ToString();
    }

    private const string InitialConversionSource = """
        namespace ConversionParity;

        public readonly struct Scalar<T>
        {
            public static implicit operator Scalar<T>(int value) => default;
            public static explicit operator int(Scalar<T> value) => 0;

            public readonly struct Nested
            {
                public static implicit operator Nested(string value) => default;
            }
        }
        """;

    private const string FinalConversionSource = """
        namespace ConversionParity;

        public readonly struct Scalar<T>
        {
            public static implicit operator Scalar<T>(int value) => default;
            public static explicit operator int(Scalar<T> value) => 0;
            public static explicit operator long(Scalar<T> value) => 0;

            public readonly struct Nested
            {
                public static implicit operator Nested(string value) => default;
            }
        }
        """;

    private static string StoredConversion(
        string name, string signature, int line, string declarationKey,
        string parentKind, string parentName, string container) =>
        string.Join('\u001f', "operator", name, "ConversionParity", container,
            signature, "public", line, line, 0, 0, "", "static", "",
            declarationKey, parentKind, parentName);

    private static int AssertSemanticHandle(NavigationTools tools, string name,
        string signature, string? expectedUsageKind = null, int? expectedTotal = null)
    {
        JsonElement hit = IndexedOperatorHit(tools, name, signature);
        Assert.Equal(Math.Min(signature.Length, 400),
            hit.GetProperty("signature").GetString()!.Length);
        string symbolId = hit.GetProperty("symbolId").GetString()!;
        int startLine = hit.GetProperty("startLine").GetInt32();

        JsonElement definition = SemanticRetry.ParseExactWithRetry(() => tools.Definition(
            symbolId: symbolId, mode: "semantic", timeoutMs: 60_000));
        Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), declaration =>
            declaration.GetProperty("path").GetString() == "Lib/Conversions.cs" &&
            declaration.GetProperty("startLine").GetInt32() == startLine);

        AssertExactOperatorReferences(() => tools.References(
            symbolId: symbolId, mode: "semantic", timeoutMs: 90_000,
                includeTests: false), startLine,
            expectedTotal: expectedTotal ?? (expectedUsageKind is null ? 0 : 1),
            expectedKind: expectedUsageKind);
        return startLine;
    }

    private static JsonElement IndexedOperatorHit(NavigationTools tools, string name,
        string signature)
    {
        string storedSignature = signature.Length > 400 ? signature[..400] : signature;
        return ParseJson(tools.SearchSymbol(name, kinds: "operator", match: "exact",
                limit: 20))
            .GetProperty("symbols").EnumerateArray()
            .Single(symbol => symbol.GetProperty("signature").GetString() == storedSignature);
    }

    private static void AssertSameLineCappedSemanticHandles(NavigationTools tools,
        string target, string sourceAlpha, string sourceBeta)
    {
        JsonElement[] hits = ParseJson(tools.SearchSymbol($"explicit operator {target}",
                kinds: "operator", match: "exact", limit: 20))
            .GetProperty("symbols").EnumerateArray().ToArray();
        Assert.Equal(2, hits.Length);
        Assert.All(hits, hit => Assert.Equal(400,
            hit.GetProperty("signature").GetString()!.Length));
        Assert.Single(hits.Select(hit => hit.GetProperty("signature").GetString())
            .Distinct(StringComparer.Ordinal));
        int startLine = Assert.Single(hits.Select(hit =>
            hit.GetProperty("startLine").GetInt32()).Distinct());

        string[] documentationIds = hits.Select(hit =>
        {
            string symbolId = hit.GetProperty("symbolId").GetString()!;
            JsonElement definition = SemanticRetry.ParseExactWithRetry(() => tools.Definition(
                symbolId: symbolId, mode: "semantic", timeoutMs: 60_000));
            Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() == "Lib/Conversions.cs" &&
                declaration.GetProperty("startLine").GetInt32() == startLine);
            string documentationId = definition.GetProperty("symbol")
                .GetProperty("documentationCommentId").GetString()!;

            JsonElement references = AssertExactOperatorReferences(() => tools.References(
                    symbolId: symbolId, mode: "semantic", timeoutMs: 90_000,
                    includeTests: false), startLine);
            Assert.Equal(documentationId, references.GetProperty("symbol")
                .GetProperty("documentationCommentId").GetString());

            JsonElement indexedDefinition = ParseJson(tools.Definition(
                symbolId: symbolId, mode: "indexed"));
            JsonElement indexedDeclaration = Assert.Single(indexedDefinition
                .GetProperty("declarations").EnumerateArray());
            Assert.Equal(symbolId,
                indexedDeclaration.GetProperty("symbolId").GetString());
            JsonElement indexedReferences = ParseJson(tools.References(
                symbolId: symbolId, mode: "indexed"));
            Assert.Equal("semantic_required",
                indexedReferences.GetProperty("error").GetString());
            Assert.Equal("operator_handle_indexed_mode_unavailable",
                indexedReferences.GetProperty("partialReason").GetString());
            return documentationId;
        }).ToArray();

        Assert.Equal(2, documentationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(documentationIds, id => id.Contains(sourceAlpha, StringComparison.Ordinal));
        Assert.Contains(documentationIds, id => id.Contains(sourceBeta, StringComparison.Ordinal));

        tools.TestOnlySemanticFailureReason = "forced_operator_semantic_failure";
        try
        {
            foreach (JsonElement hit in hits)
            {
                string symbolId = hit.GetProperty("symbolId").GetString()!;
                JsonElement fallbackDefinition = ParseJson(tools.Definition(
                    symbolId: symbolId, mode: "auto"));
                JsonElement declaration = Assert.Single(fallbackDefinition
                    .GetProperty("declarations").EnumerateArray());
                Assert.Equal(symbolId, declaration.GetProperty("symbolId").GetString());
                Assert.Equal("forced_operator_semantic_failure",
                    fallbackDefinition.GetProperty("partialReason").GetString());

                JsonElement fallbackReferences = ParseJson(tools.References(
                    symbolId: symbolId, mode: "auto"));
                Assert.Equal("semantic_required",
                    fallbackReferences.GetProperty("error").GetString());
                Assert.Equal("forced_operator_semantic_failure",
                    fallbackReferences.GetProperty("partialReason").GetString());
            }
        }
        finally
        {
            tools.TestOnlySemanticFailureReason = null;
        }
    }

    private static JsonElement IndexedConversionHit(NavigationTools tools, string signature) =>
        ParseJson(tools.SearchSymbol("implicit operator Scalar", kinds: "operator",
                match: "exact", limit: 20))
            .GetProperty("symbols").EnumerateArray()
            .Single(symbol => symbol.GetProperty("signature").GetString() == signature);

    private static JsonElement MarkerHitByParentArity(
        NavigationTools tools, IndexManager manager, int parentArity)
    {
        JsonElement[] hits = ParseJson(tools.SearchSymbol(
                "Marker", kinds: "class", match: "exact"))
            .GetProperty("symbols").EnumerateArray().ToArray();
        using var queries = manager.OpenQueries();
        return hits.Single(hit =>
        {
            SymbolHit? child = queries.SymbolById(IndexedRowId(hit));
            return child?.ParentId is { } parentId &&
                   queries.SymbolById(parentId)?.Arity == parentArity;
        });
    }

    private static long IndexedRowId(JsonElement hit)
    {
        string symbolId = hit.GetProperty("symbolId").GetString()!;
        int tilde = symbolId.IndexOf('~');
        return long.Parse(symbolId.AsSpan(4, tilde - 4),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string AssertRegularOperatorHandle(
        NavigationTools tools, JsonElement hit, int expectedReferences)
    {
        string symbolId = hit.GetProperty("symbolId").GetString()!;
        int startLine = hit.GetProperty("startLine").GetInt32();
        JsonElement definition = SemanticRetry.ParseExactWithRetry(() => tools.Definition(
            symbolId: symbolId, mode: "semantic", timeoutMs: 60_000));
        Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), declaration =>
            declaration.GetProperty("path").GetString() == "P/Operators.cs" &&
            declaration.GetProperty("startLine").GetInt32() == startLine);
        string documentationId = definition.GetProperty("symbol")
            .GetProperty("documentationCommentId").GetString()!;
        JsonElement references = AssertExactOperatorReferences(() => tools.References(
                symbolId: symbolId, mode: "semantic", timeoutMs: 90_000),
            startLine, expectedProjects: 1, expectedTotal: expectedReferences,
            declarationPath: "P/Operators.cs");
        Assert.Equal(documentationId, references.GetProperty("symbol")
            .GetProperty("documentationCommentId").GetString());
        return documentationId;
    }

    private static JsonElement AssertExactOperatorReferences(
        Func<string> referencesCall, int startLine, int expectedProjects = 2,
        int expectedTotal = 0, string? expectedKind = null,
        string declarationPath = "Lib/Conversions.cs")
    {
        JsonElement references = SemanticRetry.ParseExactWithRetry(referencesCall);
        Assert.Contains(references.GetProperty("symbol").GetProperty("declarations")
            .EnumerateArray(), declaration =>
            declaration.GetProperty("path").GetString() == declarationPath &&
            declaration.GetProperty("startLine").GetInt32() == startLine);
        Assert.Equal(expectedProjects, references.GetProperty("coverage").GetProperty("loadedProjects")
            .GetInt32());
        Assert.Equal(expectedProjects, references.GetProperty("coverage").GetProperty("requestedProjects")
            .GetInt32());
        Assert.False(references.GetProperty("coverage").TryGetProperty(
            "skippedProjects", out _));
        Assert.True(expectedTotal == references.GetProperty("totalReferences").GetInt32(),
            references.GetRawText());
        if (references.TryGetProperty("partial", out JsonElement partial))
            Assert.False(partial.GetBoolean(), references.GetRawText());
        Assert.False(references.TryGetProperty("partialReason", out _));
        Assert.False(references.TryGetProperty("totalIsLowerBound", out _));
        Assert.False(references.TryGetProperty("noteId", out _));
        Assert.Equal("exact", references.GetProperty("meta").GetProperty("confidence")
            .GetString());
        if (expectedKind is not null)
        {
            JsonElement kinds = references.GetProperty("kinds");
            Assert.Equal(expectedTotal, kinds.GetProperty(expectedKind).GetInt32());
            Assert.All(references.GetProperty("groups").EnumerateArray()
                    .SelectMany(group => group.GetProperty("samples").EnumerateArray()),
                sample => Assert.Equal(expectedKind,
                    sample.GetProperty("kind").GetString()));
        }
        else if (expectedTotal == 0)
        {
            Assert.False(references.TryGetProperty("kinds", out _));
        }
        return references;
    }

    private static void AssertConversionFixtureCompiles(string libraryPath,
        string consumerPath)
    {
        string trustedPlatformAssemblies = Assert.IsType<string>(
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
        MetadataReference[] platformReferences = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary);
        CSharpCompilation library = CSharpCompilation.Create("ConversionHandles",
            [CSharpSyntaxTree.ParseText(File.ReadAllText(libraryPath), parseOptions,
                path: libraryPath)], platformReferences, compilationOptions);
        using var libraryImage = new MemoryStream();
        var libraryEmit = library.Emit(libraryImage);
        Assert.True(libraryEmit.Success,
            "The conversion library fixture must compile before site counts are trusted: " +
            string.Join(Environment.NewLine, libraryEmit.Diagnostics));

        MetadataReference[] consumerReferences =
        [
            .. platformReferences,
            MetadataReference.CreateFromImage(libraryImage.ToArray()),
        ];
        CSharpCompilation consumer = CSharpCompilation.Create("Consumer",
            [CSharpSyntaxTree.ParseText(File.ReadAllText(consumerPath), parseOptions,
                path: consumerPath)], consumerReferences, compilationOptions);
        Diagnostic[] errors = consumer.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0,
            "The conversion consumer fixture must bind before site counts are trusted: " +
            string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }

    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement;

    private static string LegacyFingerprint(
        string name, string kind, int arity, int startLine, string path)
    {
        string identity = $"{name}\u0001{kind}\u0001{arity}\u0001{startLine}\u0001{path}";
        uint hash = 2166136261u;
        foreach (char character in identity) hash = (hash ^ character) * 16777619u;
        return hash.ToString("x8");
    }

    private static string[] DumpRows(string dbPath, string filePath)
    {
        IndexQueries.ClearPoolsFor(dbPath);
        using var connection = new SqliteConnection(
            IndexQueries.ReadConnectionString(dbPath, pinReadSnapshot: false, pooling: false));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.kind, s.name, COALESCE(s.ns, ''), COALESCE(s.container, ''),
                   s.signature, s.accessibility, s.start_line, s.end_line,
                   s.is_partial, s.arity, COALESCE(s.attr_markers, ''),
                   COALESCE(s.modifiers, ''), COALESCE(s.accessors, ''),
                   s.declaration_key, COALESCE(p.kind, ''), COALESCE(p.name, '')
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            LEFT JOIN symbols p ON p.id = s.parent_id
            WHERE f.path = $path
            ORDER BY s.id
            """;
        command.Parameters.AddWithValue("$path", filePath);

        var rows = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(string.Join('\u001f', Enumerable.Range(0, reader.FieldCount)
                .Select(index => Convert.ToString(reader.GetValue(index),
                    System.Globalization.CultureInfo.InvariantCulture) ?? "")));
        }
        return rows.ToArray();
    }

    private static string[] SymbolTableColumns(string dbPath)
    {
        IndexQueries.ClearPoolsFor(dbPath);
        using var connection = new SqliteConnection(
            IndexQueries.ReadConnectionString(dbPath, pinReadSnapshot: false, pooling: false));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(symbols)";

        var columns = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }
}
