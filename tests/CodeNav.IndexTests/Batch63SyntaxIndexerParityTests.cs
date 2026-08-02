using System.Text;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

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
            string[] deltaContextKeys = DumpContextKeys(deltaDb, "P/Conversions.cs");

            string fullDb = Path.Combine(root, ".codenav", "conversion-full-rebuild.db");
            IndexBuilder.Build(root, fullDb);
            string[] fullRows = DumpRows(fullDb, "P/Conversions.cs");
            string[] fullContextKeys = DumpContextKeys(fullDb, "P/Conversions.cs");

            Assert.Equal(fullRows, deltaRows);
            Assert.Equal(fullContextKeys, deltaContextKeys);
            Assert.All(deltaContextKeys, AssertFullSha256);
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
    public void ExplicitInterfaceConversionAccessibilityPersistsAsPrivate()
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
                public readonly struct Value : IConvert<Value>
                {
                    public static explicit operator int(Value value) => 0;
                    public static explicit operator checked int(Value value) => 0;
                    static explicit IConvert<Value>.operator int(Value value) => 0;
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
                using ConversionHandles;
                namespace ConversionConsumer;
                public static class Use
                {
                    public static void Run()
                    {
                        Scalar value = 7;
                        _ = (int)value;
                        _ = checked((long)value);
                    }

                    public static int ThroughInterface<T>(T value) where T : IConvert<T> => (int)value;
                    public static int RunInterface() => ThroughInterface(default(InterfaceScalar));
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
            manager.Start();
            for (int i = 0; i < 600 && !manager.IsQueryable; i++) Thread.Sleep(50);
            Assert.True(manager.IsQueryable, "conversion-handle index did not become queryable");
            var tools = new NavigationTools(manager, semantic);

            // Every handle must survive both semantic entry points and pin the same declaration.
            // The references call also proves that conversion targeting widens to all dependents;
            // Roslyn currently emits no locations for these conversion uses, tracked separately.
            AssertSemanticHandle(tools, "implicit operator Scalar",
                "implicit operator Scalar(int value)");
            AssertSemanticHandle(tools, "explicit operator int",
                "explicit operator int(Scalar value)");
            AssertSemanticHandle(tools, "explicit operator checked long",
                "explicit operator checked long(Scalar value)");
            int explicitInterfaceLine = AssertSemanticHandle(tools, "explicit operator int",
                "explicit IConvert<InterfaceScalar>.operator int(InterfaceScalar value)");
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
            // routes must still identify conversion syntax and disclose the incomplete census.
            AssertConversionReferenceGap(() => tools.References(
                    path: "Lib/Conversions.cs", line: explicitInterfaceLine,
                    mode: "semantic", timeoutMs: 90_000, includeTests: false),
                explicitInterfaceLine,
                expectedProjects: 2);
            AssertConversionReferenceGap(() => tools.References(
                    name: "explicit operator int", path: "Lib/Conversions.cs",
                    line: explicitInterfaceLine, mode: "semantic", timeoutMs: 90_000,
                    includeTests: false),
                explicitInterfaceLine, expectedProjects: 2);

            string telemetryLine = manager.Telemetry.Snapshot().Last(line =>
                line.Contains("\"tool\":\"references\"", StringComparison.Ordinal));
            using JsonDocument telemetry = JsonDocument.Parse(telemetryLine);
            Assert.Equal("partial", telemetry.RootElement.GetProperty("result").GetString());
            Assert.Equal("conversion_usage_enumeration_gap",
                telemetry.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ConversionHandleFingerprintRejectsSameLineOverloadAfterFullRebuildReorder()
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
            // These Marker rows collide under every old/fallback fingerprint input: same file,
            // line, namespace, container display name, declaration key, signature, and local arity.
            // Only the complete ancestor identity distinguishes Outer from Outer<T>.
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
                Assert.NotEqual(plain.ContextKey, generic.ContextKey);
                AssertFullSha256(plain.ContextKey!);
                AssertFullSha256(generic.ContextKey!);
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
            string[] deltaContextKeys = DumpContextKeys(deltaDb, "P/Deep.cs");

            string fullDb = Path.Combine(root, ".codenav", "full-rebuild.db");
            IndexBuilder.Build(root, fullDb);
            string[] fullRows = DumpRows(fullDb, "P/Deep.cs");
            string[] fullContextKeys = DumpContextKeys(fullDb, "P/Deep.cs");

            Assert.Equal(fullRows, deltaRows);
            Assert.Equal(fullContextKeys, deltaContextKeys);
            Assert.All(deltaContextKeys, AssertFullSha256);
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
        string signature)
    {
        string storedSignature = signature.Length > 400 ? signature[..400] : signature;
        JsonElement hit = ParseJson(tools.SearchSymbol(name, kinds: "operator", match: "exact",
                limit: 20))
            .GetProperty("symbols").EnumerateArray()
            .Single(symbol => symbol.GetProperty("signature").GetString() == storedSignature);
        Assert.Equal(Math.Min(signature.Length, 400),
            hit.GetProperty("signature").GetString()!.Length);
        string symbolId = hit.GetProperty("symbolId").GetString()!;
        int startLine = hit.GetProperty("startLine").GetInt32();

        JsonElement definition = SemanticRetry.ParseExactWithRetry(() => tools.Definition(
            symbolId: symbolId, mode: "semantic", timeoutMs: 60_000));
        Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), declaration =>
            declaration.GetProperty("path").GetString() == "Lib/Conversions.cs" &&
            declaration.GetProperty("startLine").GetInt32() == startLine);

        AssertConversionReferenceGap(() => tools.References(
                symbolId: symbolId, mode: "semantic", timeoutMs: 90_000,
                includeTests: false), startLine);
        return startLine;
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

            JsonElement references = AssertConversionReferenceGap(() => tools.References(
                    symbolId: symbolId, mode: "semantic", timeoutMs: 90_000,
                    includeTests: false), startLine);
            Assert.Equal(documentationId, references.GetProperty("symbol")
                .GetProperty("documentationCommentId").GetString());
            return documentationId;
        }).ToArray();

        Assert.Equal(2, documentationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(documentationIds, id => id.Contains(sourceAlpha, StringComparison.Ordinal));
        Assert.Contains(documentationIds, id => id.Contains(sourceBeta, StringComparison.Ordinal));
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

    private static JsonElement AssertConversionReferenceGap(Func<string> referencesCall,
        int startLine, int expectedProjects = 2)
    {
        JsonElement references = SemanticRetry.ParseWithRetry(referencesCall,
            response => response.TryGetProperty("noteId", out JsonElement noteId) &&
                        noteId.GetString() ==
                        "references.conversion_usage_enumeration_gap",
            "conversion references disclose the stable enumeration-gap note");
        Assert.Contains(references.GetProperty("symbol").GetProperty("declarations")
            .EnumerateArray(), declaration =>
            declaration.GetProperty("path").GetString() == "Lib/Conversions.cs" &&
            declaration.GetProperty("startLine").GetInt32() == startLine);
        Assert.Equal(expectedProjects, references.GetProperty("coverage").GetProperty("loadedProjects")
            .GetInt32());
        Assert.Equal(expectedProjects, references.GetProperty("coverage").GetProperty("requestedProjects")
            .GetInt32());
        Assert.False(references.GetProperty("coverage").TryGetProperty(
            "skippedProjects", out _));
        Assert.True(references.GetProperty("partial").GetBoolean());
        Assert.Contains("conversion_usage_enumeration_gap",
            references.GetProperty("partialReason").GetString());
        Assert.True(references.GetProperty("totalIsLowerBound").GetBoolean());
        Assert.Equal("indexed", references.GetProperty("meta").GetProperty("confidence")
            .GetString());
        return references;
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

    private static string[] DumpContextKeys(string dbPath, string filePath)
    {
        IndexQueries.ClearPoolsFor(dbPath);
        using var connection = new SqliteConnection(
            IndexQueries.ReadConnectionString(dbPath, pinReadSnapshot: false, pooling: false));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.context_key FROM symbols s JOIN files f ON f.id = s.file_id " +
            "WHERE f.path = $path ORDER BY s.id";
        command.Parameters.AddWithValue("$path", filePath);

        var keys = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys.ToArray();
    }

    private static void AssertFullSha256(string contextKey)
    {
        Assert.Equal(64, contextKey.Length);
        Assert.All(contextKey, character => Assert.True(
            character is >= '0' and <= '9' or >= 'a' and <= 'f',
            $"context key contains non-lowercase-hex character '{character}'"));
    }
}
