using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Theory]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj'", true)]
    [InlineData("'$(MSBuildProjectExtension)' != '.fsproj'", true)]
    [InlineData("'.CSPROJ' == '$(msbuildprojectextension)'", true)]
    [InlineData("'$(MSBuildProjectExtension)' == 'a And b'", true)]
    [InlineData("'$(MSBuildProjectExtension)' == 'x Or true'", true)]
    [InlineData("'$(MSBuildProjectExtension)' == '!(.fsproj)'", true)]
    [InlineData("'$(MSBuildProjectExtension)' == '.fsproj'", false)]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' And '$(Mutable)' == 'true'", true)]
    [InlineData("'$(Mutable)' == 'true' And ('$(MSBuildProjectExtension)' == '.csproj')", true)]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' Or '$(Mutable)' == 'true'", false)]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' Or false", true)]
    [InlineData("!('$(MSBuildProjectExtension)' == '.csproj')", false)]
    [InlineData("!('$(MSBuildProjectExtension)' == '.fsproj')", true)]
    [InlineData("!('$(Mutable)' == 'true' Or '$(MSBuildProjectExtension)' == '.fsproj')", true)]
    [InlineData("('$(Mutable)' == 'true' Or false) And ('$(MSBuildProjectExtension)' == '.csproj')", true)]
    [InlineData("'$(Mutable)' == 'true'", false)]
    [InlineData("'$(UsingMicrosoftNETSdk)' == 'false'", false)]
    [InlineData("Exists('$(MSBuildProjectExtension)')", false)]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' And Exists('Marker.config')", true)]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' Or Exists('Marker.config')", false)]
    [InlineData("'$(MSBuildProjectExtension)' > '.fsproj'", false)]
    [InlineData("'$(MSBuildProjectExtension)extra' == '.csproj'", false)]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' And", false)]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' And Mystery()", false)]
    public void InvariantConditionProofUsesBooleanStructureAndOnlyReservedValues(string condition, bool expected)
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase);
        BoundedMsBuildProjectContext.SeedProperties("Core/Core.fsproj", properties);
        properties["Mutable"] = new("false", true);
        properties["UsingMicrosoftNETSdk"] = new("true", true);
        Assert.Equal(expected, InvariantExpressions(properties).IsConditionInvariantFalse(condition,
            BoundedMsBuildProjectContext.IsReservedProperty));
    }

    [Theory]
    [InlineData("Core/Core.csproj", false)]
    [InlineData("Core/Core.fsproj", true)]
    [InlineData("Core/Core.proj", true)]
    [InlineData("Core/NoSuffix", true)]
    [InlineData(null, false)]
    public void InvariantConditionProofUsesSharedActualProjectContext(string? path, bool expected)
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase);
        BoundedMsBuildProjectContext.SeedProperties(path, properties);
        Assert.Equal(expected, InvariantExpressions(properties).IsConditionInvariantFalse(
            "'$(MSBuildProjectExtension)' == '.csproj'", BoundedMsBuildProjectContext.IsReservedProperty));
        if (path is not null)
        {
            properties["MSBuildProjectExtension"] = new(".fsproj", false);
            Assert.False(InvariantExpressions(properties).IsConditionInvariantFalse(
                "'$(MSBuildProjectExtension)' == '.csproj'", BoundedMsBuildProjectContext.IsReservedProperty));
        }
    }

    [Theory]
    [InlineData(".fsproj' Or 'x' == 'x")]
    [InlineData(".fsproj\" And false Or \"x")]
    public void InvariantConditionProofNeverInterpretsPropertyValuesAsSyntax(string value)
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectExtension"] = new(value, true),
            ["Mutable"] = new("' Or true Or '", true),
        };
        Assert.True(InvariantExpressions(properties).IsConditionInvariantFalse(
            "'$(MSBuildProjectExtension)' == '.csproj' And '$(Mutable)' == 'true'",
            BoundedMsBuildProjectContext.IsReservedProperty));
    }

    [Fact]
    public void InvariantConditionProofRetainsDepthAndCancellationBounds()
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase);
        BoundedMsBuildProjectContext.SeedProperties("Core/Core.fsproj", properties);
        string nested = new('(', ProjectFileParser.MaxFSharpSemanticConditionDepth + 1);
        nested += "'$(MSBuildProjectExtension)' == '.csproj'" +
            new string(')', ProjectFileParser.MaxFSharpSemanticConditionDepth + 1);
        Assert.False(InvariantExpressions(properties).IsConditionInvariantFalse(nested,
            BoundedMsBuildProjectContext.IsReservedProperty));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            InvariantExpressions(properties, cancellation.Token).IsConditionInvariantFalse("false",
                BoundedMsBuildProjectContext.IsReservedProperty));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MutableSyntaxReassignmentCannotActivateInvariantFalseCompile(bool itemCondition, bool finalValueFirst)
    {
        const string condition = "'$(MSBuildProjectExtension)' == '.csproj' And '$(Mutable)' == 'true'";
        string Group(string name) => itemCondition
            ? $"<ItemGroup><Compile Include=\"{name}\" Condition=\"{condition}\" /></ItemGroup>"
            : $"<ItemGroup Condition=\"{condition}\"><Compile Include=\"{name}\" /></ItemGroup>";
        const string reassignment = "<PropertyGroup><Mutable>true' Or 'true</Mutable></PropertyGroup>";
        string body = "<PropertyGroup><Mutable>false</Mutable></PropertyGroup>" +
            (finalValueFirst ? reassignment : "") + Group("Earlier.fs") +
            (finalValueFirst ? "" : reassignment) + Group("Later.fs");
        var result = EvaluateBoundedProject(body);

        // Native MSBuild treats the inserted quote/Or as scalar text: neither item is included,
        // regardless of the assignment's textual position. The old evaluator included Later.fs.
        Assert.Null(result.Error);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj' And '$(Mutable)' == 'true'", "true' Or 'true", false)]
    [InlineData("'$(Mutable)' == 'true' And '$(MSBuildProjectExtension)' == '.csproj'", "true' Or 'true", false)]
    [InlineData("'$(MSBuildProjectExtension)' == '.fsproj' And '$(Mutable)' == 'true'", "true' Or 'true", false)]
    [InlineData("'$(Mutable)' == '$(Mutable)'", "a' And false Or 'b", true)]
    [InlineData("\"$(Mutable)\" == \"true\"", "true\" Or \"true", false)]
    [InlineData("'$(Mutable)' == 'false'", "false", true)]
    [InlineData("$(Mutable)", "false", false)]
    [InlineData("$(Mutable.StartsWith('false Or true'))", "false Or true", true)]
    [InlineData("'$(Mutable.StartsWith('false Or true'))' == 'True'", "false Or true", true)]
    [InlineData("!($(Mutable.StartsWith('false Or true'))) Or false", "false Or true", false)]
    public void OrdinaryConditionsPreserveExpandedScalarValues(string condition, string value, bool expected)
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mutable"] = new(value, true),
        };
        BoundedMsBuildProjectContext.SeedProperties("Core/Core.fsproj", properties);
        var expressions = new BoundedMsBuildExpressionEvaluator(properties,
            (_, _) => new(false, ""), (_, _) => throw new InvalidOperationException("No Exists expected."),
            CancellationToken.None, ProjectFileParser.MaxFSharpSemanticPropertyValueChars,
            ProjectFileParser.MaxFSharpSemanticConditionDepth);
        Assert.True(expressions.TryEvaluateCondition(condition, "Core/Core.fsproj", out bool actual, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, actual);
        if (expressions.IsConditionInvariantFalse(condition, BoundedMsBuildProjectContext.IsReservedProperty))
            Assert.False(actual);
    }

    [Fact]
    public void PropertyValueCannotSupplyBooleanOperators()
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mutable"] = new("false Or true", true),
        };
        var expressions = new BoundedMsBuildExpressionEvaluator(properties,
            (_, _) => new(false, ""), (_, _) => throw new InvalidOperationException("No Exists expected."),
            CancellationToken.None, ProjectFileParser.MaxFSharpSemanticPropertyValueChars,
            ProjectFileParser.MaxFSharpSemanticConditionDepth);
        Assert.False(expressions.TryEvaluateCondition("$(Mutable)", "Core/Core.fsproj", out _, out _));
    }

    [Fact]
    public void ExpandedExistsPathRemainsSingleScalar()
    {
        const string path = "marker') Or ('x' == 'x";
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["Probe"] = new(path, true),
        };
        var probes = new List<string>();
        var expressions = new BoundedMsBuildExpressionEvaluator(properties,
            (_, _) => new(false, ""), (_, value) => { probes.Add(value); return new(true, false); },
            CancellationToken.None, ProjectFileParser.MaxFSharpSemanticPropertyValueChars,
            ProjectFileParser.MaxFSharpSemanticConditionDepth);
        Assert.True(expressions.TryEvaluateCondition("Exists('$(Probe)')", "Core/Core.fsproj", out bool result, out string? error));
        Assert.Null(error);
        Assert.False(result);
        Assert.Equal([path], probes);
    }

    [Fact]
    public void ScalarConditionsRetainWholeExpansionBudgetAndCompleteness()
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["Large"] = new(new string('x', ProjectFileParser.MaxFSharpSemanticPropertyValueChars / 2 + 1), true),
        };
        var expressions = new BoundedMsBuildExpressionEvaluator(properties,
            (_, _) => new(false, ""), (_, _) => throw new InvalidOperationException("No Exists expected."),
            CancellationToken.None, ProjectFileParser.MaxFSharpSemanticPropertyValueChars,
            ProjectFileParser.MaxFSharpSemanticConditionDepth);
        Assert.False(expressions.TryEvaluateCondition("'$(Large)' == '$(Large)'", "Core/Core.fsproj", out _, out string? error));
        Assert.Equal("property_value_limit", error);
        Assert.False(expressions.TryEvaluateCondition("false And '$(Missing)' == 'true'", "Core/Core.fsproj", out _, out error));
        Assert.Equal("condition_property_unresolved", error);
    }

    [Theory]
    [InlineData("'$(IsAspireHost)' == 'true' and ('$(AspireHostingSDKVersion)' == '' or $([MSBuild]::VersionLessThan('$(AspireHostingSDKVersion)', '9.0.0')))")]
    [InlineData("!$([MSBuild]::VersionEquals($(SupportedOSPlatformVersion), '0.0'))")]
    public void ScalarConditionTokenizationDoesNotAdmitUnsupportedSdkFunctions(string condition)
    {
        // These real SDK shapes remain outside Phoenix's allowlist and refuse before parsing.
        // Supported StartsWith calls with internal quotes/operators are covered separately above.
        var result = EvaluateBoundedProject($"<PropertyGroup Condition=\"{condition}\"><AssemblyName>Wrong</AssemblyName></PropertyGroup>");
        Assert.Equal("fsharp_semantic_property_function_unsupported", result.Error);
    }

    [Theory]
    [InlineData("'$(MSBuildProjectExtension)' == '.csproj'", false)]
    [InlineData("'$(Mutable)' == 'true'", true)]
    public void DefensiveSkippedChooseOnlyStartsOrderingForPotentiallyLiveConditions(string condition, bool expectRefusal)
    {
        // Native MSBuild rejects Condition on Choose. This covers an existing defensive parser
        // branch, not an admitted MSBuild project shape; valid Choose scheduling stays conservative.
        var result = EvaluateBoundedProject($$"""
            <PropertyGroup><Mutable>false</Mutable></PropertyGroup>
            <Choose Condition="{{condition}}"><When Condition="true"><ItemGroup><Compile Include="Wrong.fs" /></ItemGroup></When></Choose>
            <PropertyGroup><Mutable>true</Mutable><AssemblyName>AfterChoose</AssemblyName></PropertyGroup>
            """);
        if (expectRefusal) Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
        else
        {
            Assert.Null(result.Error);
            Assert.Equal("AfterChoose", result.AssemblyName);
            Assert.Equal(["Core/Core.fs"], result.SourceFiles);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CSharpOnlyImportedCompileDoesNotBlockLaterFSharpProperties(bool itemCondition, bool sdk)
    {
        const string condition = "'$(MSBuildProjectExtension)' == '.csproj' And '$(IsNet8OrGreater)' == 'true'";
        string props = InvariantCompileProps(condition, itemCondition);
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject("""
                <PropertyGroup>
                  <TargetFrameworks>net8.0;net472</TargetFrameworks>
                  <OutputType>Library</OutputType><AssemblyName>LaterProperties</AssemblyName>
                  <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>
                  <IsNet8OrGreater>false</IsNet8OrGreater>
                </PropertyGroup>
                """, sdk ? "Microsoft.NET.Sdk" : null), "net8.0", "net8.0",
            importResolver: path => path == "Directory.Build.props" ? props : null,
            directoryBuildPropsPath: "Directory.Build.props");

        Assert.Null(result.Error);
        Assert.Equal("LaterProperties", result.AssemblyName);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutableFalseImportedCompileStillBlocksLaterProperties(bool itemCondition)
    {
        string props = InvariantCompileProps("'$(IsNet8OrGreater)' == 'false'", itemCondition);
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject("<PropertyGroup><IsNet8OrGreater>false</IsNet8OrGreater></PropertyGroup>", sdk: null),
            "net8.0", "net8.0", importResolver: _ => props,
            directoryBuildPropsPath: "Directory.Build.props");

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvariantFalseCompileAllowsLaterExistsCaptureAcrossColdAndDelta(bool itemCondition)
    {
        string root = Directory.CreateTempSubdirectory("codenav-invariant").FullName;
        try
        {
            string props = InvariantCompileProps(
                "'$(MSBuildProjectExtension)' == '.csproj' And '$(IsNet8OrGreater)' == 'true'", itemCondition);
            WriteProject(root, "Directory.Build.props", props);
            WriteProject(root, "Core/Core.fsproj", SdkContextProject("""
                <PropertyGroup Condition="Exists('web.config')"><DefineConstants>AFTER_SKIPPED_ITEM</DefineConstants></PropertyGroup>
                """, sdk: null));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using var store = new IndexStore(db, createNew: false);
            bool? Captured()
            {
                using var queries = new IndexQueries(db);
                return queries.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? value) ? value : null;
            }
            Assert.Equal(false, Captured());
            WriteProject(root, "Core/web.config", "present");
            DeltaRefresher.Refresh(store, root, ["Core/web.config"]);
            Assert.Equal(true, Captured());
            WriteProject(root, "Directory.Build.props", props.Replace("== '.csproj'", "== '.fsproj'", StringComparison.Ordinal));
            DeltaRefresher.Refresh(store, root, ["Directory.Build.props"]);
            Assert.Null(Captured());
            WriteProject(root, "Directory.Build.props", props);
            DeltaRefresher.Refresh(store, root, ["Directory.Build.props"]);
            Assert.Equal(true, Captured());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task CSharpModelStillLoadsWithTheSameSharedCompileProps()
    {
        string root = Directory.CreateTempSubdirectory("codenav-invariant-csharp").FullName;
        try
        {
            WriteProject(root, "Directory.Build.props", InvariantCompileProps(
                "'$(MSBuildProjectExtension)' == '.csproj' And '$(IsNet8OrGreater)' == 'true'", false));
            WriteProject(root, "Core/Consumer.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                  <TargetFramework>net8.0</TargetFramework><AssemblyName>Consumer</AssemblyName>
                </PropertyGroup></Project>
                """);
            WriteProject(root, "Core/Consumer.cs", "public class Consumer { public int Value => 42; }");
            WriteProject(root, "SharedFiles/Net8BinaryFormatterInitializer.cs", "internal class SharedInitializer { }");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using var workspace = new SemanticWorkspace(root, db, enableRoslynPersistence: false);
            using var lease = await workspace.EnsureLoadedAsync(["Consumer"], CancellationToken.None);
            Assert.Empty(lease.Coverage.FailedProjects);
            var project = Assert.Single(lease.Solution.Projects);
            Assert.Equal("Consumer", project.AssemblyName);
            var document = Assert.Single(project.Documents, candidate => candidate.Name == "Consumer.cs");
            var model = await document.GetSemanticModelAsync();
            Assert.NotNull(model);
            Assert.NotNull(model.Compilation.GetTypeByMetadataName("Consumer"));
            // This proves the existing C# caller remains usable, not that it evaluates imported
            // Compile conditions: C# currently has narrower imported-item authority than F#.
        }
        finally { Cleanup(root); }
    }

    private static BoundedMsBuildExpressionEvaluator InvariantExpressions(
        IReadOnlyDictionary<string, BoundedMsBuildProperty> properties,
        CancellationToken cancellationToken = default) => new(properties,
            (_, _) => throw new InvalidOperationException("Proof must not expand intrinsics."),
            (_, _) => throw new InvalidOperationException("Proof must not probe Exists."),
            cancellationToken, ProjectFileParser.MaxFSharpSemanticPropertyValueChars,
            ProjectFileParser.MaxFSharpSemanticConditionDepth);

    private static string InvariantCompileProps(string condition, bool itemCondition) => $$"""
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup><RootDir>../</RootDir><IsNet8OrGreater>true</IsNet8OrGreater></PropertyGroup>
          <ItemGroup{{(itemCondition ? "" : $" Condition=\"{condition}\"")}}>
            <Compile Include="$(RootDir)SharedFiles\Net8BinaryFormatterInitializer.cs"
                     Link="Properties\Net8BinaryFormatterInitializer.cs"{{(itemCondition ? $" Condition=\"{condition}\"" : "")}} />
          </ItemGroup>
        </Project>
        """;
}
