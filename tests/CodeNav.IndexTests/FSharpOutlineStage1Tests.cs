using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public class FSharpOutlineStage1Tests
{
    [Theory]
    [InlineData("sdk")]
    [InlineData("old-style")]
    public void OutlineReturnsFcsDeclarationsForProjectOwnedImplementationAndSignatureFiles(
        string projectStyle)
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Core");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Core.fsproj"),
                ProjectXml(projectStyle));
            File.WriteAllText(Path.Combine(projectDirectory, "Contract.fsi"),
                """
                namespace StageOne

                type Contract =
                    abstract Name: string

                module Signatures =
                    val transform: int -> int
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Library.fs"),
                """
                namespace StageOne

                type Widget(name: string) =
                    member _.Name = name

                module Api =
                    let transform value = value + 1
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement implementation = Parse(tools.Outline("Core/Library.fs", depth: 2));
            AssertOutline(implementation,
                ("Widget", 3), ("Name", 4), ("Api", 6), ("transform", 7));

            JsonElement signature = Parse(tools.Outline("Core/Contract.fsi", depth: 2));
            AssertOutline(signature,
                ("Contract", 3), ("Name", 4), ("Signatures", 6), ("transform", 7));

            JsonElement rootsOnly = Parse(tools.Outline("Core/Library.fs", depth: 1));
            Assert.All(rootsOnly.GetProperty("symbols").EnumerateArray(), symbol =>
                Assert.False(symbol.TryGetProperty("members", out _)));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("sdk", "net9.0", "NET9_0")]
    [InlineData("old-style", "net472", "NET472")]
    public void OutlineUsesOwningProjectDefinesTargetSymbolAndLanguageVersion(
        string projectStyle, string indexedTfm, string targetDefine)
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline-options").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Core");
            Directory.CreateDirectory(projectDirectory);
            string projectXml = OptionsProjectXml(
                projectStyle, "PHOENIX_PROJECT", "Conditional.fs");
            File.WriteAllText(Path.Combine(projectDirectory, "Core.fsproj"), projectXml);
            File.WriteAllText(Path.Combine(projectDirectory, "Conditional.fs"),
                $"""
                module Conditional

                #if PHOENIX_PROJECT
                #if {targetDefine}
                let selected value = value + 1
                #else
                let missingTargetDefine = 0
                #endif
                #else
                let missingProjectDefine = 0
                #endif
                """);

            FSharpParsingOptionsSnapshot options =
                ProjectFileParser.ParseFSharpParsingOptionsSnapshot(
                    "Core/Core.fsproj", projectXml, indexedTfm);
            Assert.Null(options.Error);
            Assert.Contains("--define:PHOENIX_PROJECT", options.CommandLineArgs);
            Assert.Contains($"--define:{targetDefine}", options.CommandLineArgs);
            Assert.Contains("--langversion:preview", options.CommandLineArgs);

            using var fixture = OutlineFixture.Create(root);
            JsonElement response = Parse(fixture.Tools.Outline("Core/Conditional.fs", depth: 2));
            JsonElement module = Assert.Single(response.GetProperty("symbols").EnumerateArray());
            AssertNode(module, "Conditional", "module", "module Conditional", "public");
            JsonElement selected = Assert.Single(Members(module));
            AssertNode(selected, "selected", "function", "selected value", "public");
            if (projectStyle == "sdk")
            {
                Assert.True(response.GetProperty("partial").GetBoolean());
                Assert.Contains("fsharp_project_options_imported",
                    response.GetProperty("partialReason").GetString());
            }
            else
            {
                Assert.False(response.TryGetProperty("partial", out _));
                Assert.False(response.TryGetProperty("partialReason", out _));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void OutlineRejectsDivergentMultiOwnerParserOptions()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline-conflict").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "A"));
            Directory.CreateDirectory(Path.Combine(root, "B"));
            Directory.CreateDirectory(Path.Combine(root, "Shared"));
            File.WriteAllText(Path.Combine(root, "A", "A.fsproj"),
                OptionsProjectXml("sdk", "OWNER_A", "../Shared/Shared.fs"));
            File.WriteAllText(Path.Combine(root, "B", "B.fsproj"),
                OptionsProjectXml("sdk", "OWNER_B", "../Shared/Shared.fs"));
            File.WriteAllText(Path.Combine(root, "Shared", "Shared.fs"),
                "module Shared\nlet value = 1\n");

            using var fixture = OutlineFixture.Create(root);
            JsonElement response = Parse(fixture.Tools.Outline("Shared/Shared.fs"));
            Assert.Equal("fsharp_project_options_conflict",
                response.GetProperty("error").GetString());
            Assert.False(response.TryGetProperty("symbols", out _));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectOptionSelectionReportsConditionedAndMultiTargetShapes()
    {
        const string projectXml = """
            <Project>
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <DefineConstants>DEBUG_ONLY</DefineConstants>
              </PropertyGroup>
            </Project>
            """;

        FSharpParsingOptionsSnapshot conditioned =
            ProjectFileParser.ParseFSharpParsingOptionsSnapshot(
                "Core/Core.fsproj", projectXml, "net9.0");
        Assert.Null(conditioned.Error);
        Assert.Contains("fsharp_project_options_conditioned", conditioned.PartialReason);
        Assert.DoesNotContain("--define:DEBUG_ONLY", conditioned.CommandLineArgs);

        FSharpParsingOptionsSnapshot multiTarget =
            ProjectFileParser.ParseFSharpParsingOptionsSnapshot(
                "Core/Core.fsproj", projectXml, "net8.0;net9.0");
        Assert.Null(multiTarget.Error);
        Assert.Equal("net8.0", multiTarget.SelectedTargetFramework);
        Assert.Equal(["net8.0", "net9.0"], multiTarget.AvailableTargetFrameworks);
        Assert.Contains("fsharp_target_framework_defaulted", multiTarget.PartialReason);
        Assert.Contains("--define:NET8_0", multiTarget.CommandLineArgs);
        Assert.DoesNotContain("--define:NET9_0", multiTarget.CommandLineArgs);
    }

    [Fact]
    public void OutlinePrefersExactLegacyBaseOverDualTargetNetCompanion()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline-pair").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Migration");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Project.fsproj"), """
                <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                    <DefineConstants>LEGACY_BASE</DefineConstants>
                    <LangVersion>preview</LangVersion>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Shared.fs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Project.Net.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net472;net8.0</TargetFrameworks>
                    <DefineConstants>SDK_COMPANION</DefineConstants>
                    <LangVersion>preview</LangVersion>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Shared.fs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Shared.fs"), """
                module Shared

                #if LEGACY_BASE
                let legacyBaseSelected = 1
                #else
                let companionSelected = 2
                #endif
                """);

            using var fixture = OutlineFixture.Create(root);
            JsonElement response = Parse(fixture.Tools.Outline("Migration/Shared.fs", depth: 2));
            JsonElement module = Assert.Single(response.GetProperty("symbols").EnumerateArray());
            Assert.Equal("legacyBaseSelected", NodeName(Assert.Single(Members(module))));

            JsonElement selected = response.GetProperty("selectedContext");
            Assert.Equal("Migration/Project.fsproj", selected.GetProperty("project").GetString());
            Assert.Equal("net472", selected.GetProperty("targetFramework").GetString());
            Assert.True(response.GetProperty("partial").GetBoolean());
            Assert.Equal("fsharp_alternate_syntax_contexts",
                response.GetProperty("partialReason").GetString());

            var contexts = response.GetProperty("availableContexts").EnumerateArray()
                .Select(context => (
                    context.GetProperty("project").GetString(),
                    context.GetProperty("targetFramework").GetString()))
                .ToList();
            Assert.Equal([
                ("Migration/Project.fsproj", "net472"),
                ("Migration/Project.Net.fsproj", "net472"),
                ("Migration/Project.Net.fsproj", "net8.0"),
            ], contexts);
            Assert.Equal(3, response.GetProperty("availableContextsTotal").GetInt32());
            Assert.Equal(3, response.GetProperty("availableContextsReturned").GetInt32());
            Assert.False(response.GetProperty("availableContextsTruncated").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void OutlineDefaultsCompanionOnlyProjectToFirstDeclaredTargetFramework()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline-multitarget").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Migration");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Project.Net.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net472;net8.0</TargetFrameworks>
                    <LangVersion>preview</LangVersion>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Shared.fs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Shared.fs"), """
                module Shared

                #if NET472
                let netFrameworkSelected = 1
                #else
                let netEightSelected = 2
                #endif
                """);

            using var fixture = OutlineFixture.Create(root);
            JsonElement response = Parse(fixture.Tools.Outline("Migration/Shared.fs", depth: 2));
            JsonElement module = Assert.Single(response.GetProperty("symbols").EnumerateArray());
            Assert.Equal("netFrameworkSelected", NodeName(Assert.Single(Members(module))));

            JsonElement selected = response.GetProperty("selectedContext");
            Assert.Equal("Migration/Project.Net.fsproj",
                selected.GetProperty("project").GetString());
            Assert.Equal("net472", selected.GetProperty("targetFramework").GetString());
            Assert.Contains("fsharp_target_framework_defaulted",
                response.GetProperty("partialReason").GetString());
            Assert.Equal(2, response.GetProperty("availableContexts").GetArrayLength());
            Assert.Equal(2, response.GetProperty("availableContextsTotal").GetInt32());
            Assert.Equal(2, response.GetProperty("availableContextsReturned").GetInt32());
            Assert.False(response.GetProperty("availableContextsTruncated").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, true)]
    public void OutlineCapsAvailableProjectContextsAtSixtyFourAndReportsCoverage(
        int ownerCount, bool expectedTruncated)
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline-context-cap").FullName;
        try
        {
            string sharedDirectory = Path.Combine(root, "Shared");
            Directory.CreateDirectory(sharedDirectory);
            File.WriteAllText(Path.Combine(sharedDirectory, "Shared.fs"),
                "module Shared\nlet value = 1\n");

            for (int i = 0; i < ownerCount; i++)
            {
                string ownerName = $"Owner{i:D2}";
                string ownerDirectory = Path.Combine(root, ownerName);
                Directory.CreateDirectory(ownerDirectory);
                File.WriteAllText(Path.Combine(ownerDirectory, $"{ownerName}.fsproj"), """
                    <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                      <PropertyGroup>
                        <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                        <DefineConstants>SHARED_OWNER</DefineConstants>
                      </PropertyGroup>
                      <ItemGroup>
                        <Compile Include="../Shared/Shared.fs" />
                      </ItemGroup>
                    </Project>
                    """);
            }

            using var fixture = OutlineFixture.Create(root);
            string json = fixture.Tools.Outline("Shared/Shared.fs", depth: 2);
            JsonElement response = Parse(json);

            Assert.Equal(64 * 1024, Json.HardBudgetBytes);
            Assert.True(Json.Utf8Bytes(json) <= Json.HardBudgetBytes);
            Assert.Equal(ownerCount,
                response.GetProperty("availableContextsTotal").GetInt32());
            int expectedReturned = Math.Min(ownerCount,
                NavigationTools.MaxFSharpOutlineContexts);
            Assert.Equal(expectedReturned,
                response.GetProperty("availableContextsReturned").GetInt32());
            Assert.Equal(expectedTruncated,
                response.GetProperty("availableContextsTruncated").GetBoolean());
            var contexts = response.GetProperty("availableContexts").EnumerateArray().ToList();
            Assert.Equal(expectedReturned, contexts.Count);
            Assert.Equal(response.GetProperty("selectedContext").GetProperty("project").GetString(),
                contexts[0].GetProperty("project").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void OutlineReturnsCanonicalFactsForImplementationAndSignatureSyntax()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline-facts").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Facts");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Facts.fsproj"),
                OptionsProjectXml("sdk", "", "Contract.fsi", "Facts.fs"));
            File.WriteAllText(Path.Combine(projectDirectory, "Facts.fs"),
                """
                namespace Facts

                exception Broken of int

                type Shape =
                    | Circle of float
                    | Point

                type Record = { Value: int }

                [<AbstractClass>]
                type Widget(name: string) =
                    member _.Name = name
                    member _.Secret with get() = 1 and private set (value: int) = ()
                    static member Create(value: string) = Widget(value)
                    static member Convert(value: int) = string value
                    static member Convert(value: string) = value
                    abstract Describe: unit -> string
                    default _.Describe() = name

                module Api =
                    let transform value = value + 1
                    let transformPair left right = left + right
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Contract.fsi"),
                """
                namespace Facts.Contract

                exception ContractFailure of string

                type Contract =
                    abstract Name: string
                    abstract Describe: unit -> string

                module ContractApi =
                    val transform: int -> int
                """);

            using var fixture = OutlineFixture.Create(root);
            JsonElement implementation = Parse(fixture.Tools.Outline("Facts/Facts.fs", depth: 2));
            JsonElement implementationNamespace =
                Assert.Single(implementation.GetProperty("symbols").EnumerateArray());
            AssertNode(implementationNamespace, "Facts", "namespace", "namespace Facts", "public");
            Assert.Equal(["Broken", "Shape", "Record", "Widget", "Api"],
                Members(implementationNamespace).Select(NodeName));

            JsonElement broken = Child(implementationNamespace, "Broken");
            AssertNode(broken, "Broken", "exception", "exception Broken of int", "public");

            JsonElement shape = Child(implementationNamespace, "Shape");
            AssertNode(shape, "Shape", "union", "type Shape", "public");
            Assert.Equal(["Circle", "Point"], Members(shape).Select(NodeName));
            AssertNode(Child(shape, "Circle"), "Circle", "unionCase", "Circle of float", "public");

            JsonElement record = Child(implementationNamespace, "Record");
            AssertNode(record, "Record", "record", "type Record", "public");
            AssertNode(Assert.Single(Members(record)), "Value", "field", "Value: int", "public");

            JsonElement widget = Child(implementationNamespace, "Widget");
            AssertNode(widget, "Widget", "class", "type Widget(name: string)", "public");
            Assert.Equal(["Name", "Secret", "Create", "Convert", "Convert", "Describe", "Describe"],
                Members(widget).Select(NodeName));
            AssertNode(Child(widget, "Name"), "Name", "property", "_.Name", "public");
            JsonElement secret = Child(widget, "Secret");
            AssertNode(secret, "Secret", "property", "member _.Secret with get()", "public");
            Assert.Equal("get=public;set=private", secret.GetProperty("accessors").GetString());
            JsonElement create = Child(widget, "Create");
            AssertNode(create, "Create", "method", "Create(value: string)", "public");
            Assert.Equal("static", create.GetProperty("modifiers").GetString());
            JsonElement[] converts = Members(widget)
                .Where(node => NodeName(node) == "Convert")
                .ToArray();
            Assert.Equal(2, converts.Length);
            Assert.Equal(["Convert(value: int)", "Convert(value: string)"],
                converts.Select(node => OptionalString(node, "signature")));
            Assert.All(converts, node =>
                Assert.Equal("static", OptionalString(node, "modifiers")));
            JsonElement abstractDescribe = Assert.Single(Members(widget), node =>
                NodeName(node) == "Describe" &&
                OptionalString(node, "modifiers") == "abstract");
            Assert.Contains("unit -> string", OptionalString(abstractDescribe, "signature"));
            JsonElement overrideDescribe = Assert.Single(Members(widget), node =>
                NodeName(node) == "Describe" &&
                OptionalString(node, "modifiers") == "override");
            Assert.Equal("_.Describe()", OptionalString(overrideDescribe, "signature"));

            JsonElement api = Child(implementationNamespace, "Api");
            AssertNode(api, "Api", "module", "module Api", "public");
            AssertNode(Child(api, "transform"), "transform", "function", "transform value", "public");
            AssertNode(Child(api, "transformPair"), "transformPair", "function",
                "transformPair left right", "public");

            JsonElement signature = Parse(fixture.Tools.Outline("Facts/Contract.fsi", depth: 2));
            JsonElement signatureNamespace =
                Assert.Single(signature.GetProperty("symbols").EnumerateArray());
            AssertNode(signatureNamespace, "Facts.Contract", "namespace",
                "namespace Facts.Contract", "public");
            Assert.Equal(["ContractFailure", "Contract", "ContractApi"],
                Members(signatureNamespace).Select(NodeName));
            AssertNode(Child(signatureNamespace, "ContractFailure"), "ContractFailure",
                "exception", "exception ContractFailure of string", "public");
            JsonElement contract = Child(signatureNamespace, "Contract");
            AssertNode(contract, "Contract", "class", "type Contract", "public");
            AssertNode(Child(contract, "Name"), "Name", "property",
                "abstract Name: string", "public");
            Assert.Equal("abstract", Child(contract, "Name").GetProperty("modifiers").GetString());
            JsonElement contractApi = Child(signatureNamespace, "ContractApi");
            AssertNode(Child(contractApi, "transform"), "transform", "function",
                "val transform: int -> int", "public");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void OutlineFailsClosedForMalformedOrOwnerlessFSharpAndLeavesScriptsUnsupported()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-outline-boundary").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Core");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Core.fsproj"), ProjectXml("sdk"));
            File.WriteAllText(Path.Combine(projectDirectory, "Contract.fsi"),
                "namespace StageOne\ntype Contract = class end\n");
            File.WriteAllText(Path.Combine(projectDirectory, "Library.fs"),
                "module Broken\nlet value =\n");
            File.WriteAllText(Path.Combine(root, "Ownerless.fs"),
                "module Ownerless\nlet value = 1\n");
            File.WriteAllText(Path.Combine(root, "Scratch.fsx"), "let value = 1\n");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement malformed = Parse(tools.Outline("Core/Library.fs"));
            Assert.Equal("fsharp_parse_failed", malformed.GetProperty("error").GetString());
            Assert.False(malformed.TryGetProperty("symbols", out _));

            JsonElement ownerless = Parse(tools.Outline("Ownerless.fs"));
            Assert.Equal("fsharp_project_not_found", ownerless.GetProperty("error").GetString());

            JsonElement script = Parse(tools.Outline("Scratch.fsx"));
            Assert.Equal("unsupported_language", script.GetProperty("error").GetString());
            Assert.Equal("fs", script.GetProperty("language").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void AssertOutline(JsonElement response,
        params (string Name, int StartLine)[] expected)
    {
        Assert.Equal("indexed", response.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.Equal("syntax", response.GetProperty("meta").GetProperty("navigationLayer").GetString());
        var declarations = Descendants(response.GetProperty("symbols")).ToList();
        foreach ((string name, int startLine) in expected)
        {
            JsonElement declaration = Assert.Single(declarations,
                item => item.GetProperty("name").GetString() == name);
            Assert.Equal(startLine, declaration.GetProperty("startLine").GetInt32());
            Assert.True(declaration.GetProperty("endLine").GetInt32() >= startLine);
        }
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement symbols)
    {
        foreach (JsonElement symbol in symbols.EnumerateArray())
        {
            yield return symbol;
            if (symbol.TryGetProperty("members", out JsonElement members) &&
                members.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement member in Descendants(members))
                    yield return member;
            }
        }
    }

    private static List<JsonElement> Members(JsonElement node)
    {
        if (!node.TryGetProperty("members", out JsonElement members) ||
            members.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return members.EnumerateArray().ToList();
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        Assert.Single(Members(parent), node => NodeName(node) == name);

    private static string NodeName(JsonElement node) =>
        node.GetProperty("name").GetString()!;

    private static string? OptionalString(JsonElement node, string property) =>
        node.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void AssertNode(JsonElement node, string name, string kind,
        string signature, string accessibility)
    {
        Assert.Equal(name, NodeName(node));
        Assert.Equal(kind, node.GetProperty("kind").GetString());
        Assert.Equal(signature, node.GetProperty("signature").GetString());
        Assert.Equal(accessibility, node.GetProperty("accessibility").GetString());
        Assert.True(node.GetProperty("startLine").GetInt32() >= 1);
        Assert.True(node.GetProperty("endLine").GetInt32() >=
                    node.GetProperty("startLine").GetInt32());
    }

    private static string ProjectXml(string style) => style == "sdk"
        ? """
          <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup>
              <TargetFramework>net9.0</TargetFramework>
              <AssemblyName>StageOne.Core</AssemblyName>
            </PropertyGroup>
            <ItemGroup>
              <Compile Include="Contract.fsi" />
              <Compile Include="Library.fs" />
            </ItemGroup>
          </Project>
          """
        : """
          <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
            <PropertyGroup>
              <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
              <AssemblyName>StageOne.Core</AssemblyName>
            </PropertyGroup>
            <ItemGroup>
              <Compile Include="Contract.fsi" />
              <Compile Include="Library.fs" />
            </ItemGroup>
          </Project>
          """;

    private static string OptionsProjectXml(string style, string define,
        params string[] compileItems)
    {
        string items = string.Join(Environment.NewLine,
            compileItems.Select(item => $"<Compile Include=\"{item}\" />"));
        return style == "sdk"
            ? $$"""
              <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <TargetFramework>net9.0</TargetFramework>
                  <DefineConstants>{{define}}</DefineConstants>
                  <LangVersion>preview</LangVersion>
                </PropertyGroup>
                <ItemGroup>
                  {{items}}
                </ItemGroup>
              </Project>
              """
            : $$"""
              <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                <PropertyGroup>
                  <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                  <DefineConstants>{{define}}</DefineConstants>
                  <LangVersion>preview</LangVersion>
                </PropertyGroup>
                <ItemGroup>
                  {{items}}
                </ItemGroup>
              </Project>
              """;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    private sealed class OutlineFixture : IDisposable
    {
        private readonly IndexManager _manager;
        private readonly SemanticService _semantic;

        private OutlineFixture(IndexManager manager, SemanticService semantic)
        {
            _manager = manager;
            _semantic = semantic;
            Tools = new NavigationTools(manager, semantic);
        }

        public NavigationTools Tools { get; }

        public static OutlineFixture Create(string root)
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var semantic = new SemanticService(manager);
            return new OutlineFixture(manager, semantic);
        }

        public void Dispose()
        {
            _semantic.Dispose();
            _manager.Dispose();
        }
    }

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
