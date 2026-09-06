using System.Text.Json;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void ImplementationsFindNamedAndNestedObjectExpressionWorkspaceDependents()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-workspace").FullName;
        try
        {
            WriteProject(root, "Contracts/Contracts.fsproj",
                SdkProject("net10.0", "Contracts.fs"));
            WriteProject(root, "Contracts/Contracts.fs", """
                namespace Contracts
                type IFoo<'T> =
                    abstract Run: 'T -> string
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Contracts/Contracts.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs", """
                namespace Consumer
                open Contracts
                type Named() =
                    interface IFoo<int> with
                        member _.Run value = string value
                module Factory =
                    let make flag =
                        match flag with
                        | true ->
                            let inner =
                                { new IFoo<int> with
                                    member _.Run value = string (value + 1) }
                            inner
                        | false -> Named() :> IFoo<int>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(2, response.GetProperty("totalImplementations").GetInt32());
            Assert.False(response.TryGetProperty("totalIsLowerBound", out _));
            Assert.True(response.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
            var implementations = response.GetProperty("implementations")
                .EnumerateArray().ToList();
            Assert.Contains(implementations, item =>
                item.GetProperty("implementationKind").GetString() ==
                "interfaceImplementation" &&
                item.GetProperty("symbol").GetProperty("name").GetString() == "Named");
            JsonElement objectExpression = Assert.Single(implementations, item =>
                item.GetProperty("implementationKind").GetString() == "objectExpression");
            Assert.Equal("IFoo", objectExpression.GetProperty("symbol")
                .GetProperty("name").GetString());
            Assert.Equal("objectExpression", objectExpression.GetProperty("symbol")
                .GetProperty("kind").GetString());
            Assert.Equal("Consumer/Consumer.fs", objectExpression.GetProperty("symbol")
                .GetProperty("path").GetString());
            Assert.Equal(2, response.GetProperty("concreteCount").GetInt32());
            Assert.False(response.TryGetProperty("likelyImplementation", out _));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(raw) <= Json.HardBudgetBytes);

            string memberDeclarationRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 3, column: 14,
                timeoutMs: 60_000));
            JsonElement fromDeclaration = Parse(memberDeclarationRaw);
            Assert.False(fromDeclaration.TryGetProperty("error", out _), memberDeclarationRaw);
            Assert.Equal(2, fromDeclaration.GetProperty("totalImplementations").GetInt32());

            string memberRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Consumer/Consumer.fs", line: 5, column: 18,
                timeoutMs: 60_000));
            JsonElement fromImplementation = Parse(memberRaw);
            Assert.False(fromImplementation.TryGetProperty("error", out _), memberRaw);
            Assert.Equal(2, fromImplementation.GetProperty("totalImplementations").GetInt32());
            Assert.Contains("IFoo.Run", fromImplementation.GetProperty("resolvedFromOverride")
                .EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(["objectExpression", "override"],
                fromImplementation.GetProperty("implementations").EnumerateArray()
                    .Select(item => item.GetProperty("implementationKind").GetString()!)
                    .Order().ToArray());
            Assert.Equal(
                fromDeclaration.GetProperty("implementations").EnumerateArray()
                    .Select(item => $"{item.GetProperty("implementationKind").GetString()}:" +
                        item.GetProperty("symbol").GetProperty("line").GetInt32())
                    .Order().ToArray(),
                fromImplementation.GetProperty("implementations").EnumerateArray()
                    .Select(item => $"{item.GetProperty("implementationKind").GetString()}:" +
                        item.GetProperty("symbol").GetProperty("line").GetInt32())
                    .Order().ToArray());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsMatchOverloadedSlotsAndRetargetOverridePositions()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-slots").FullName;
        try
        {
            WriteProject(root, "Slots/Slots.fsproj", SdkProject("net10.0", "Slots.fs"));
            WriteProject(root, "Slots/Slots.fs", """
                namespace Slots
                [<AbstractClass>]
                type Base() =
                    abstract member Run: int -> string
                    abstract member Run: string -> string
                type First() =
                    inherit Base()
                    override _.Run(value: int) = string value
                    override _.Run(value: string) = value
                type Second() =
                    inherit Base()
                    override _.Run(value: int) = string (value + 1)
                    override _.Run(value: string) = value + "!"
                """);

            using var fixture = Fixture.Create(root);
            string declarationRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Slots/Slots.fs", line: 4, column: 22,
                timeoutMs: 60_000));
            JsonElement declaration = Parse(declarationRaw);
            Assert.False(declaration.TryGetProperty("error", out _), declarationRaw);
            Assert.Equal(2, declaration.GetProperty("totalImplementations").GetInt32());
            Assert.All(declaration.GetProperty("implementations").EnumerateArray(), item =>
            {
                Assert.Equal("override", item.GetProperty("implementationKind").GetString());
                Assert.Equal("Run", item.GetProperty("symbol").GetProperty("name").GetString());
                Assert.Contains(item.GetProperty("symbol").GetProperty("line").GetInt32(),
                    new[] { 8, 12 });
            });

            string overrideRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Slots/Slots.fs", line: 8, column: 17,
                timeoutMs: 60_000));
            JsonElement fromOverride = Parse(overrideRaw);
            Assert.False(fromOverride.TryGetProperty("error", out _), overrideRaw);
            Assert.Equal(2, fromOverride.GetProperty("totalImplementations").GetInt32());
            Assert.True(fromOverride.TryGetProperty("resolvedFromOverride",
                out JsonElement resolvedFromOverride), overrideRaw);
            Assert.Contains("Base.Run", resolvedFromOverride
                .EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(
                declaration.GetProperty("implementations").EnumerateArray()
                    .Select(item => item.GetProperty("symbol").GetProperty("line").GetInt32())
                    .Order().ToArray(),
                fromOverride.GetProperty("implementations").EnumerateArray()
                    .Select(item => item.GetProperty("symbol").GetProperty("line").GetInt32())
                    .Order().ToArray());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsMatchRenamedGenericMethodSlotsFromDeclarationAndOverride()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-generic-slots").FullName;
        try
        {
            WriteProject(root, "GenericSlots/GenericSlots.fsproj",
                SdkProject("net10.0", "GenericSlots.fs"));
            WriteProject(root, "GenericSlots/GenericSlots.fs", """
                namespace GenericSlots
                [<AbstractClass>]
                type Base() =
                    abstract member Map<'U>: 'U -> 'U
                type Derived() =
                    inherit Base()
                    override _.Map<'V>(value: 'V) = value
                """);

            using var fixture = Fixture.Create(root);
            string declarationRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "GenericSlots/GenericSlots.fs", line: 4, column: 22,
                timeoutMs: 60_000));
            JsonElement declaration = Parse(declarationRaw);
            Assert.False(declaration.TryGetProperty("error", out _), declarationRaw);
            JsonElement declarationHit = Assert.Single(declaration
                .GetProperty("implementations").EnumerateArray());
            Assert.Equal("override",
                declarationHit.GetProperty("implementationKind").GetString());
            Assert.Equal(7, declarationHit.GetProperty("symbol").GetProperty("line")
                .GetInt32());

            string overrideRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "GenericSlots/GenericSlots.fs", line: 7, column: 17,
                timeoutMs: 60_000));
            JsonElement fromOverride = Parse(overrideRaw);
            Assert.False(fromOverride.TryGetProperty("error", out _), overrideRaw);
            JsonElement overrideHit = Assert.Single(fromOverride
                .GetProperty("implementations").EnumerateArray());
            Assert.Equal(7,
                overrideHit.GetProperty("symbol").GetProperty("line").GetInt32());
            Assert.Contains("Base.Map", fromOverride.GetProperty("resolvedFromOverride")
                .EnumerateArray().Select(value => value.GetString()));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsFindNamedTypesAndOverridesHiddenBySignatureFiles()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-signature-hidden").FullName;
        try
        {
            WriteProject(root, "Hidden/Hidden.fsproj",
                SdkProject("net10.0", "Api.fsi", "Api.fs"));
            WriteProject(root, "Hidden/Api.fsi", """
                namespace Hidden
                type IService =
                    abstract Run: unit -> int
                [<AbstractClass>]
                type Base =
                    new: unit -> Base
                    abstract Map: int -> int
                type IVisible =
                    abstract Show: unit -> int
                type PublicVisible =
                    new: unit -> PublicVisible
                    interface IVisible
                val makeService: unit -> IService
                val makeBase: unit -> Base
                """);
            WriteProject(root, "Hidden/Api.fs", """
                namespace Hidden
                type IService =
                    abstract Run: unit -> int
                [<AbstractClass>]
                type Base() =
                    abstract Map: int -> int
                type HiddenService() =
                    interface IService with
                        member _.Run() = 1
                type HiddenDerived() =
                    inherit Base()
                    override _.Map(value: int) = value + 1
                type IVisible =
                    abstract Show: unit -> int
                type PublicVisible() =
                    interface IVisible with
                        member _.Show() = 2
                let makeService() = HiddenService() :> IService
                let makeBase() = HiddenDerived() :> Base
                """);

            using var fixture = Fixture.Create(root);
            string typeRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Hidden/Api.fsi", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement typeResponse = Parse(typeRaw);
            Assert.False(typeResponse.TryGetProperty("error", out _), typeRaw);
            JsonElement hiddenType = Assert.Single(typeResponse
                .GetProperty("implementations").EnumerateArray());
            Assert.Equal("HiddenService",
                hiddenType.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("Hidden/Api.fs",
                hiddenType.GetProperty("symbol").GetProperty("path").GetString());

            string memberRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Hidden/Api.fsi", line: 7, column: 14,
                timeoutMs: 60_000));
            JsonElement memberResponse = Parse(memberRaw);
            Assert.False(memberResponse.TryGetProperty("error", out _), memberRaw);
            JsonElement hiddenOverride = Assert.Single(memberResponse
                .GetProperty("implementations").EnumerateArray());
            Assert.Equal("override",
                hiddenOverride.GetProperty("implementationKind").GetString());
            Assert.Equal("Map",
                hiddenOverride.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("Hidden/Api.fs",
                hiddenOverride.GetProperty("symbol").GetProperty("path").GetString());

            string visibleRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Hidden/Api.fsi", line: 8, column: 7,
                timeoutMs: 60_000));
            JsonElement visibleResponse = Parse(visibleRaw);
            Assert.False(visibleResponse.TryGetProperty("error", out _), visibleRaw);
            JsonElement visibleImplementation = Assert.Single(visibleResponse
                .GetProperty("implementations").EnumerateArray());
            Assert.Equal("PublicVisible",
                visibleImplementation.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("Hidden/Api.fs",
                visibleImplementation.GetProperty("symbol").GetProperty("path").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsMatchGenericEntityKindsAndRankTransitiveDerivations()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-entities").FullName;
        try
        {
            WriteProject(root, "Kinds/Kinds.fsproj", SdkProject("net10.0", "Kinds.fs"));
            WriteProject(root, "Kinds/Kinds.fs", """
                namespace Kinds
                type IBox<'T> =
                    abstract Value: 'T
                type RecordBox =
                    { Item: int }
                    interface IBox<int> with
                        member this.Value = this.Item
                [<Struct>]
                type StructBox =
                    { Item: int }
                    interface IBox<int> with
                        member this.Value = this.Item
                type ClassBox(value: int) =
                    interface IBox<int> with
                        member _.Value = value
                type UnionBox =
                    | UnionBox of int
                    interface IBox<int> with
                        member this.Value =
                            let (UnionBox value) = this
                            value
                [<AbstractClass>]
                type Base() = class end
                [<AbstractClass>]
                type Mid() =
                    inherit Base()
                type Leaf() =
                    inherit Mid()
                """);

            using var fixture = Fixture.Create(root);
            string interfaceRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Kinds/Kinds.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement interfaceResponse = Parse(interfaceRaw);
            Assert.False(interfaceResponse.TryGetProperty("error", out _), interfaceRaw);
            Assert.Equal(4, interfaceResponse.GetProperty("totalImplementations").GetInt32());
            var entityNames = interfaceResponse.GetProperty("implementations")
                .EnumerateArray()
                .Select(item => item.GetProperty("symbol").GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("RecordBox", entityNames);
            Assert.Contains("StructBox", entityNames);
            Assert.Contains("ClassBox", entityNames);
            Assert.Contains("UnionBox", entityNames);
            Assert.All(interfaceResponse.GetProperty("implementations").EnumerateArray(),
                item => Assert.Equal("interfaceImplementation",
                    item.GetProperty("implementationKind").GetString()));

            string baseRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Kinds/Kinds.fs", line: 23, column: 7,
                timeoutMs: 60_000));
            JsonElement baseResponse = Parse(baseRaw);
            Assert.False(baseResponse.TryGetProperty("error", out _), baseRaw);
            Assert.Equal(2, baseResponse.GetProperty("totalImplementations").GetInt32());
            var derived = baseResponse.GetProperty("implementations").EnumerateArray().ToList();
            Assert.Equal("Leaf", derived[0].GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("concrete", derived[0].GetProperty("rank").GetString());
            Assert.Equal("Mid", derived[0].GetProperty("via").GetString());
            Assert.Equal("Mid", derived[1].GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("abstract", derived[1].GetProperty("rank").GetString());
            Assert.Equal("Kinds.Leaf", baseResponse.GetProperty("likelyImplementation").GetString());
            Assert.True(baseResponse.TryGetProperty("symbol", out _));
            Assert.Equal("interfaceImplementation",
                interfaceResponse.GetProperty("implementations")[0]
                    .GetProperty("implementationKind").GetString());
            JsonElement concrete = derived[0];
            Assert.True(concrete.TryGetProperty("symbol", out _));
            Assert.False(concrete.TryGetProperty("isAbstract", out _));
            Assert.Equal("concrete", concrete.GetProperty("rank").GetString());
            Assert.Equal("Mid", concrete.GetProperty("via").GetString());
            Assert.Equal("Kinds/Kinds.fsproj", concrete.GetProperty("project").GetString());
            Assert.Equal(["net10.0"], concrete.GetProperty("targetFrameworksScanned")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Equal(1, baseResponse.GetProperty("concreteCount").GetInt32());
            Assert.True(baseResponse.TryGetProperty("coverage", out _));
            Assert.True(baseResponse.GetProperty("partial").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                baseResponse.GetProperty("partialReason").GetString()));
            Assert.False(baseResponse.TryGetProperty("retryRecommended", out _));
            Assert.False(baseResponse.TryGetProperty("retryHint", out _));
            Assert.True(baseResponse.TryGetProperty("timing", out _));
            Assert.False(baseResponse.TryGetProperty("truncated", out _));
            Assert.Contains("likely the runtime target",
                baseResponse.GetProperty("hint").GetString());
            Assert.Contains("Exactly 2 compiler-bound implementations",
                baseResponse.GetProperty("summary").GetString());
            Assert.Equal(2, baseResponse.GetProperty("totalImplementations").GetInt32());
            Assert.False(baseResponse.TryGetProperty("totalIsLowerBound", out _));
            JsonElement meta = baseResponse.GetProperty("meta");
            Assert.Equal("exact", meta.GetProperty("confidence").GetString());
            Assert.Equal("semantic", meta.GetProperty("navigationLayer").GetString());
            Assert.Equal($"{BuildInfo.Version}+{BuildInfo.Commit}",
                meta.GetProperty("build").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsDedupeDependentTfmsAndDiscloseTestAndGeneratedFilters()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-filters").FullName;
        try
        {
            WriteProject(root, "Contracts/Contracts.fsproj",
                SdkProject("net8.0;net10.0", "Contracts.fs"));
            WriteProject(root, "Contracts/Contracts.fs", """
                namespace Contracts
                type IService =
                    abstract Run: unit -> int
                """);
            WriteProject(root, "Consumer.Tests/Consumer.Tests.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Normal.fs" />
                    <Compile Include="Generated.g.fs" />
                    <ProjectReference Include="../Contracts/Contracts.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer.Tests/Normal.fs", """
                namespace Consumer
                open Contracts
                type Normal() =
                    interface IService with
                        member _.Run() = 1
                """);
            WriteProject(root, "Consumer.Tests/Generated.g.fs", """
                namespace Consumer
                open Contracts
                type Generated() =
                    interface IService with
                        member _.Run() = 2
                """);

            using var fixture = Fixture.Create(root);
            string generatedExcludedRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                projectPath: "Contracts/Contracts.fsproj", targetFramework: "net10.0",
                includeTests: true, includeGenerated: false, timeoutMs: 60_000));
            JsonElement generatedExcluded = Parse(generatedExcludedRaw);
            Assert.False(generatedExcluded.TryGetProperty("error", out _),
                generatedExcludedRaw);
            Assert.Equal(1, generatedExcluded.GetProperty("totalImplementations").GetInt32());
            JsonElement normal = Assert.Single(generatedExcluded.GetProperty("implementations")
                .EnumerateArray());
            Assert.Equal("Normal", normal.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal(["net10.0", "net8.0"], normal.GetProperty("targetFrameworksScanned")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Contains("Generated files were excluded before counting",
                generatedExcluded.GetProperty("summary").GetString());

            string generatedIncludedRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                projectPath: "Contracts/Contracts.fsproj", targetFramework: "net10.0",
                includeTests: true, includeGenerated: true, timeoutMs: 60_000));
            JsonElement generatedIncluded = Parse(generatedIncludedRaw);
            Assert.False(generatedIncluded.TryGetProperty("error", out _), generatedIncludedRaw);
            Assert.Equal(2, generatedIncluded.GetProperty("totalImplementations").GetInt32());
            Assert.All(generatedIncluded.GetProperty("implementations").EnumerateArray(), item =>
                Assert.Equal(2, item.GetProperty("targetFrameworksScanned").GetArrayLength()));

            string testsExcludedRaw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                projectPath: "Contracts/Contracts.fsproj", targetFramework: "net10.0",
                includeTests: false, includeGenerated: true, timeoutMs: 60_000));
            JsonElement testsExcluded = Parse(testsExcludedRaw);
            Assert.False(testsExcluded.TryGetProperty("error", out _), testsExcludedRaw);
            Assert.Equal(0, testsExcluded.GetProperty("totalImplementations").GetInt32());
            JsonElement filtered = Assert.Single(testsExcluded.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                    "Consumer.Tests/Consumer.Tests.fsproj");
            Assert.Equal("filtered", filtered.GetProperty("status").GetString());
            Assert.Equal("test_project", filtered.GetProperty("reason").GetString());
            Assert.Contains("Test projects were excluded before counting",
                testsExcluded.GetProperty("summary").GetString());
            Assert.True(testsExcluded.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsExcludeQuotationBodiesAndKeepTypedEvidenceOutsideThem()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-quotations").FullName;
        try
        {
            WriteProject(root, "Quotes/Quotes.fsproj", SdkProject("net10.0", "Quotes.fs"));
            WriteProject(root, "Quotes/Quotes.fs", """
                namespace Quotes
                type IService =
                    abstract Run: unit -> int
                module Factory =
                    let quoted =
                        <@ { new IService with member _.Run() = 1 } @>
                    let live =
                        { new IService with member _.Run() = 2 }
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Quotes/Quotes.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalImplementations").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Contains("fsharp_workspace_quotation_bodies_excluded",
                response.GetProperty("partialReason").GetString());
            Assert.True(response.GetProperty("coverage")
                .GetProperty("quotationBodiesExcluded").GetBoolean());
            JsonElement group = Assert.Single(response.GetProperty("groups")
                .EnumerateArray());
            Assert.Equal("partial", group.GetProperty("status").GetString());
            Assert.Equal("quotation_bodies", group.GetProperty("reason").GetString());
            Assert.Equal("indexed",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal("semantic",
                response.GetProperty("meta").GetProperty("navigationLayer").GetString());
            Assert.Equal("objectExpression", Assert.Single(response
                .GetProperty("implementations").EnumerateArray())
                .GetProperty("implementationKind").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsTrimItemsWithoutChangingExactWorkspaceCounts()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-budget").FullName;
        try
        {
            WriteProject(root, "Many/Many.fsproj", SdkProject("net10.0", "Many.fs"));
            string implementations = string.Join('\n', Enumerable.Range(1, 20).Select(index =>
                $"type Implementation{index:D2}() = interface IService with member _.Run() = {index}"));
            WriteProject(root, "Many/Many.fs", $"""
                namespace Many
                type IService =
                    abstract Run: unit -> int
                {implementations}
                """);

            using var fixture = Fixture.Create(root);
            fixture.Tools.TestOnlyReferencesResponseMaxBytes = 3_000;
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Many/Many.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(20, response.GetProperty("totalImplementations").GetInt32());
            Assert.True(response.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
            Assert.True(response.GetProperty("implementations").GetArrayLength() < 20);
            Assert.True(response.GetProperty("truncated").GetBoolean());
            Assert.Equal(NoteIds.FSharpImplementationItemsByteBudget,
                response.GetProperty("truncationNoteId").GetString());
            Assert.True(Json.Utf8Bytes(raw) <= 3_000, raw);
            Assert.True(Json.Utf8Bytes(raw) <= Json.HardBudgetBytes, raw);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsPreserveAnOversizedCompilerTargetIdentity()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-indivisible-identity").FullName;
        try
        {
            string name = "I" + new string('A', 40_000);
            WriteProject(root, "Oversized/Oversized.fsproj",
                SdkProject("net10.0", "Oversized.fs"));
            WriteProject(root, "Oversized/Oversized.fs", $"""
                namespace Oversized
                type {name} =
                    abstract Run: unit -> int
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Oversized/Oversized.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(name,
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal($"Oversized.{name}",
                response.GetProperty("symbol").GetProperty("fullName").GetString());
            JsonElement budget = response.GetProperty("responseBudget");
            Assert.True(budget.GetProperty("exceeded").GetBoolean());
            Assert.True(budget.GetProperty("completeIdentity").GetBoolean());
            Assert.Equal("indivisible_semantic_identity",
                budget.GetProperty("reason").GetString());
            Assert.Equal(Json.Utf8Bytes(raw),
                budget.GetProperty("serializedBytes").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsKeepCompletedRootEvidenceOnDeterministicDependentCancellation()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-deadline").FullName;
        try
        {
            WriteProject(root, "Contracts/Contracts.fsproj",
                SdkProject("net10.0", "Contracts.fs"));
            WriteProject(root, "Contracts/Contracts.fs", """
                namespace Contracts
                type IService =
                    abstract Run: unit -> int
                type RootImplementation() =
                    interface IService with
                        member _.Run() = 1
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Contracts/Contracts.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs", """
                namespace Consumer
                open Contracts
                type DependentImplementation() =
                    interface IService with
                        member _.Run() = 2
                """);

            using var fixture = Fixture.Create(root);
            fixture.Semantic.BeforeFSharpSemanticSourceReadForTest = path =>
            {
                if (path == "Consumer/Consumer.fs") throw new OperationCanceledException();
            };
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalImplementations").GetInt32());
            Assert.Equal("RootImplementation", Assert.Single(response
                .GetProperty("implementations").EnumerateArray())
                .GetProperty("symbol").GetProperty("name").GetString());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Contains("fsharp_workspace_deadline",
                response.GetProperty("partialReason").GetString());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Equal(1, coverage.GetProperty("dependentsPending").GetInt32());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal("semantic",
                response.GetProperty("meta").GetProperty("navigationLayer").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsDoNotRecommendASoleConcreteHitWithIndexedConfidence()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-indexed-confidence").FullName;
        try
        {
            WriteProject(root, "Diagnostics/Diagnostics.fsproj",
                SdkProject("net10.0", "Diagnostics.fs"));
            WriteProject(root, "Diagnostics/Diagnostics.fs", """
                namespace Diagnostics
                type IService =
                    abstract Run: unit -> int
                type Implementation() =
                    interface IService with
                        member _.Run() = 1
                module Broken =
                    let impossible: int = "text"
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Diagnostics/Diagnostics.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalImplementations").GetInt32());
            Assert.True(response.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
            Assert.Contains("fsharp_semantic_diagnostics_present",
                response.GetProperty("partialReason").GetString());
            Assert.Equal("indexed",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.False(response.TryGetProperty("likelyImplementation", out _));
            Assert.False(response.TryGetProperty("hint", out _));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsKeepRootEvidenceWhenCancellationInterruptsTypedTraversal()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-traversal-cancellation").FullName;
        try
        {
            WriteProject(root, "Contracts/Contracts.fsproj",
                SdkProject("net10.0", "Contracts.fs"));
            WriteProject(root, "Contracts/Contracts.fs", """
                namespace Contracts
                type IService =
                    abstract Run: unit -> int
                type RootImplementation() =
                    interface IService with
                        member _.Run() = 1
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Contracts/Contracts.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs", """
                namespace Consumer
                open Contracts
                type DependentImplementation() =
                    interface IService with
                        member _.Run() = 2
                """);

            using var fixture = Fixture.Create(root);
            fixture.Semantic.BeforeFSharpImplementationTraversalForTest = project =>
            {
                string normalized = project.Replace('\\', '/');
                if (normalized.EndsWith("/Consumer/Consumer.fsproj",
                        StringComparison.Ordinal))
                    throw new OperationCanceledException();
            };
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalImplementations").GetInt32());
            Assert.Equal("RootImplementation", Assert.Single(response
                .GetProperty("implementations").EnumerateArray())
                .GetProperty("symbol").GetProperty("name").GetString());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Contains("fsharp_workspace_deadline",
                response.GetProperty("partialReason").GetString());
            Assert.Equal(1, response.GetProperty("coverage")
                .GetProperty("dependentsPending").GetInt32());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal("semantic",
                response.GetProperty("meta").GetProperty("navigationLayer").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsDiscloseIncompleteDiscoveryAndBinaryDependentBoundaries()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-boundaries").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", """
                namespace Library
                type IService =
                    abstract Run: unit -> int
                type RootImplementation() =
                    interface IService with
                        member _.Run() = 1
                """);
            WriteProject(root, "Broken/Broken.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Broken.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Broken/Broken.fs", "module Broken\nlet marker = 1\n");
            WriteProject(root, "Binary/Binary.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Binary.fs" />
                    <Reference Include="Library" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Binary/Binary.fs", "module Binary\nlet marker = 1\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Library/Library.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalImplementations").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            string reasons = response.GetProperty("partialReason").GetString()!;
            Assert.Contains("fsharp_workspace_dependent_discovery_incomplete", reasons);
            Assert.Contains("fsharp_workspace_binary_dependents_not_scanned", reasons);
            JsonElement coverage = response.GetProperty("coverage");
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Equal(1, coverage.GetProperty("potentialConsumersUnevaluated").GetInt32());
            Assert.Equal(1, coverage.GetProperty("excludedByReason")
                .GetProperty("binary_reference").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImplementationsDiscardChangedDependentBinaryHitsAndKeepHealthyGroups()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-implementations-dependent-binary-snapshot").FullName;
        try
        {
            WriteProject(root, "Contracts/Contracts.fsproj",
                SdkProject("net10.0", "Contracts.fs"));
            WriteProject(root, "Contracts/Contracts.fs", """
                namespace Contracts
                type IService =
                    abstract Run: unit -> int
                """);
            WriteProject(root, "A_Binary/A_Binary.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="A_Binary.fs" />
                    <ProjectReference Include="../Contracts/Contracts.fsproj" />
                    <Reference Include="CodeNav.Core">
                      <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "A_Binary/A_Binary.fs", """
                namespace A_Binary
                open Contracts
                type UnverifiedImplementation() =
                    interface IService with
                        member _.Run() = 1
                """);
            WriteProject(root, "Z_Healthy/Z_Healthy.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Z_Healthy.fs" />
                    <ProjectReference Include="../Contracts/Contracts.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Z_Healthy/Z_Healthy.fs", """
                namespace Z_Healthy
                open Contracts
                type HealthyImplementation() =
                    interface IService with
                        member _.Run() = 2
                """);
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(binary);

            using var fixture = Fixture.Create(root);
            int completedChecks = 0;
            fixture.Semantic.FSharpSemanticCheckCompletedForTest = _ =>
            {
                completedChecks++;
                if (completedChecks != 2) return;
                using var stream = new FileStream(binary, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Read);
                stream.Position = 32;
                int original = stream.ReadByte();
                Assert.True(original >= 0);
                stream.Position = 32;
                stream.WriteByte((byte)(original ^ 1));
                File.SetLastWriteTimeUtc(binary, originalWriteTime);
            };

            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalImplementations").GetInt32());
            JsonElement healthy = Assert.Single(response.GetProperty("implementations")
                .EnumerateArray());
            Assert.Equal("HealthyImplementation",
                healthy.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal(["net10.0"], healthy.GetProperty("targetFrameworksScanned")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
            JsonElement binaryGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                    "A_Binary/A_Binary.fsproj");
            Assert.Equal("failed", binaryGroup.GetProperty("status").GetString());
            Assert.Equal("fsharp_semantic_reference_changed",
                binaryGroup.GetProperty("reason").GetString());
            Assert.Equal(0, binaryGroup.GetProperty("count").GetInt32());
            JsonElement healthyGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                    "Z_Healthy/Z_Healthy.fsproj");
            Assert.Equal("scanned", healthyGroup.GetProperty("status").GetString());
            Assert.Equal(1, healthyGroup.GetProperty("count").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Equal(1, response.GetProperty("coverage")
                .GetProperty("dependentsFailed").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }
}
