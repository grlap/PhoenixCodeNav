using System.Text.Json;
using CodeNav.Core.Diagnostics;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

[Collection(CSharpCpmEnvironmentIsolationCollection.Name)]
public sealed class MsBuildDiagnosticTests
{
    [Theory]
    [InlineData(null, true, "Marker", "assumed_empty", true)]
    [InlineData(null, true, "Other", "absent", false)]
    [InlineData("$(Unknown)", false, "Marker", "stored_incomplete", false)]
    [InlineData("false", true, "Marker", "resolved", true)]
    public void DiagnosticPropertyDecisionsExplainTheEmptyExemption(string? storedValue,
        bool storedComplete, string selfProperty, string decision, bool expectedSuccess)
    {
        var records = new List<JsonElement>();
        using var diagnostics = Session(records);
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase);
        if (storedValue is not null) properties["Marker"] = new(storedValue, storedComplete);
        var evaluator = Expressions(properties, diagnostics);
        const string condition = " '$(Marker)' != 'true' ";
        bool success = evaluator.TryEvaluateCondition(condition, "Directory.Build.targets",
            out bool result, out string? error, selfProperty);
        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedSuccess, result);
        Assert.Equal(expectedSuccess ? null : "condition_property_unresolved", error);
        JsonElement read = records.First(record => Kind(record) == "expansion.property").GetProperty("data");
        Assert.Equal("Marker", read.GetProperty("name").GetString());
        Assert.Equal(selfProperty, read.GetProperty("selfProperty").GetString());
        Assert.Equal(selfProperty == "Marker", read.GetProperty("matchesSelf").GetBoolean());
        Assert.Equal(storedValue is not null, read.GetProperty("found").GetBoolean());
        Assert.Equal(decision, read.GetProperty("decision").GetString());
        if (storedValue is not null)
        {
            Assert.Equal(storedValue, read.GetProperty("storedValue").GetString());
            Assert.Equal(storedComplete, read.GetProperty("storedComplete").GetBoolean());
        }
        Assert.Equal(condition, records.Single(record => Kind(record) == "condition.begin")
            .GetProperty("data").GetProperty("condition").GetString());
        Assert.Equal(success, records.Last(record => Kind(record) == "condition.end")
            .GetProperty("data").GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("$(Unknown)", "containsPropertyToken")]
    [InlineData("%(Metadata)", "containsMetadataToken")]
    [InlineData("@(Items)", "containsItemToken")]
    public void DiagnosticResidualTokensAreSeparateFromStoredCompleteness(string value, string tokenFlag)
    {
        var records = new List<JsonElement>();
        using var diagnostics = Session(records);
        var evaluator = Expressions(new() { ["Marker"] = new(value, true) }, diagnostics);
        Assert.False(evaluator.TryEvaluateCondition("'$(Marker)' != 'true'", "p.fsproj", out _, out var error));
        Assert.Equal("condition_property_unresolved", error);
        Assert.True(records.Single(record => Kind(record) == "expansion.property")
            .GetProperty("data").GetProperty("storedComplete").GetBoolean());
        JsonElement end = records.Single(record => Kind(record) == "expansion.end").GetProperty("data");
        Assert.True(end.GetProperty(tokenFlag).GetBoolean());
        Assert.False(end.GetProperty("complete").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticStringFunctionFailureAndCancellationRemainDistinct(bool stored)
    {
        var records = new List<JsonElement>();
        using (var diagnostics = Session(records))
        {
            var properties = new Dictionary<string, BoundedMsBuildProperty>();
            if (stored) properties["Missing"] = new("$(Other)", false);
            Assert.False(Expressions(properties, diagnostics).TryEvaluateCondition(
                "$(Missing.StartsWith('x'))", "p.fsproj", out _, out string? error));
            Assert.Equal("condition_property_unresolved", error);
            Assert.Contains(records, record => Kind(record) == "expansion.property" &&
                record.GetProperty("data").GetProperty("decision").GetString() ==
                (stored ? "function_stored_incomplete" : "function_absent"));
            Assert.False(records.Single(record => Kind(record) == "expansion.functions")
                .GetProperty("data").GetProperty("complete").GetBoolean());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => Expressions(new(), diagnostics, cancellation.Token)
                .TryEvaluateCondition("true", "p.fsproj", out _, out _));
        }
        Assert.Equal("aborted", records.Last().GetProperty("data").GetProperty("outcome").GetString());
    }

    [Fact]
    public void DiagnosticListenerFailureCannotChangeConditionOrPathResults()
    {
        using var diagnostics = new MsBuildDiagnosticSession("p.fsproj", "net10.0", "test",
            _ => throw new IOException("diagnostic sink unavailable"));
        var properties = new Dictionary<string, BoundedMsBuildProperty> { ["Root"] = BoundedMsBuildProperty.Native("native\\root") };
        var plain = Expressions(properties, null);
        var traced = Expressions(properties, diagnostics);
        const string input = "$(Root)\\File.fs";
        Assert.True(plain.TryExpandPropertyValue(input, "p.fsproj", null, out var expected, out var expectedError));
        Assert.True(traced.TryExpandPropertyValue(input, "p.fsproj", null, out var actual, out var actualError));
        Assert.Equal(expected, actual);
        Assert.Equal(expectedError, actualError);
        Assert.True(traced.TryEvaluateCondition("'$(Marker)' != 'true'", "p.fsproj", out var result, out _, "Marker"));
        Assert.True(result);
    }

    [Fact]
    public void DiagnosticSidecarRecordsTheRealImportRetryWithoutChangingTheSnapshot()
    {
        string root = Directory.CreateTempSubdirectory("codenav-msbuild-diag").FullName;
        string? previous = Environment.GetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, null);
            var baseline = ParseImport(root);
            Assert.False(Directory.Exists(Path.Combine(root, ".codenav")));
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, "1");
            var traced = ParseImport(root);
            Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(traced));
            Assert.Null(traced.Error);
            Assert.Equal(["Core/Core.fs"], traced.SourceFiles);
            string file = Assert.Single(Directory.GetFiles(Path.Combine(root, ".codenav", "telemetry")));
            Assert.StartsWith("msbuild-", Path.GetFileName(file));
            var records = ReadRecords(file);
            Assert.Single(records.Select(record => record.GetProperty("evaluationId").GetString()).Distinct());
            Assert.Equal(Enumerable.Range(1, records.Length).Select(value => (long)value),
                records.Select(record => record.GetProperty("sequence").GetInt64()));
            Assert.All(records, record => Assert.Equal("Core/Core.fsproj", record.GetProperty("project").GetString()));
            Assert.Contains(records, record => Kind(record) == "import.marker_retry" &&
                record.GetProperty("data").GetProperty("evaluated").GetBoolean() &&
                record.GetProperty("data").GetProperty("process").GetBoolean());
            Assert.Contains(records, record => Kind(record) == "expansion.property" &&
                record.GetProperty("data").GetProperty("decision").GetString() == "assumed_empty");
            Assert.Contains(records, record => Kind(record) == "import.skipped" &&
                record.GetProperty("data").GetProperty("reason").GetString() == "condition_false");
            Assert.Equal("succeeded", records.Last().GetProperty("data").GetProperty("outcome").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, previous);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DiagnosticFileFailureDoesNotRefuseAnOtherwiseValidImport()
    {
        string root = Directory.CreateTempSubdirectory("codenav-msbuild-diag-fail").FullName;
        string? previous = Environment.GetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable);
        try
        {
            File.WriteAllText(Path.Combine(root, ".codenav"), "not a directory");
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, "1");
            Assert.Null(ParseImport(root).Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, previous);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DiagnosticSidecarCoversColdDeltaAndPublicSemanticEvaluation()
    {
        string root = Directory.CreateTempSubdirectory("codenav-msbuild-diag-live").FullName;
        string? previous = Environment.GetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, "1");
            File.WriteAllText(Path.Combine(root, "Core.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
                """);
            File.WriteAllText(Path.Combine(root, "Core.fs"), "module Core\nlet value = 42\nlet answer = value\n");
            const string target = """
                <Project><Import Project="Marker.props" Condition="'$(Imported)' != 'true'" /></Project>
                """;
            File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), target);
            File.WriteAllText(Path.Combine(root, "Marker.props"),
                "<Project><PropertyGroup><Imported>true</Imported></PropertyGroup></Project>");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using (var store = new IndexStore(db, createNew: false))
            {
                File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), target + "\n");
                DeltaRefresher.Refresh(store, root, ["Directory.Build.targets"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            }
            using (var manager = new IndexManager(root, db, fsharpProjectModel: ProjectModelMode.Evaluated))
            {
                manager.Start();
                Assert.True(SpinWait.SpinUntil(() => manager.State == "ready", TimeSpan.FromSeconds(30)), manager.Health().Error);
                using var semantic = new SemanticService(manager, enableRoslynPersistence: false);
                var tools = new NavigationTools(manager, semantic);
                string raw = tools.SymbolAt("Core.fs", 3, 15, timeoutMs: 60_000);
                using var response = JsonDocument.Parse(raw);
                Assert.True(response.RootElement.GetProperty("found").GetBoolean(), raw);
                Assert.Equal("value", response.RootElement.GetProperty("symbol").GetProperty("name").GetString());
                Assert.DoesNotContain(manager.Telemetry.Snapshot(), line => line.Contains("$(Imported)", StringComparison.Ordinal));
            }
            string file = Assert.Single(Directory.GetFiles(Path.Combine(root, ".codenav", "telemetry"), "msbuild-*.jsonl"));
            var records = ReadRecords(file);
            foreach (string origin in new[] { "index.build", "index.refresh", "semantic.query" })
            {
                var starts = records.Where(record => Kind(record) == "evaluation.start" &&
                    record.GetProperty("origin").GetString() == origin).ToArray();
                Assert.NotEmpty(starts);
                foreach (var start in starts)
                {
                    string id = start.GetProperty("evaluationId").GetString()!;
                    Assert.Contains(records, record => record.GetProperty("evaluationId").GetString() == id &&
                        Kind(record) == "import.marker_retry" && record.GetProperty("data").GetProperty("process").GetBoolean());
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, previous);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void DefaultSimpleBuildRefreshAndQueryNeverStartAdvancedDiagnostics()
    {
        const string modelVariable = "PHOENIX_FSHARP_PROJECT_MODEL";
        string? previousModel = Environment.GetEnvironmentVariable(modelVariable);
        string? previousDiagnostics = Environment.GetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable);
        string root = Directory.CreateTempSubdirectory("cn-simple-no-evaluation").FullName;
        try
        {
            Environment.SetEnvironmentVariable(modelVariable, null);
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, "1");
            const string project = """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                  <Import Project="Deploy.targets" Condition="'$(Imported)' != 'true'" />
                </Project>
                """;
            File.WriteAllText(Path.Combine(root, "Core.fsproj"), project);
            File.WriteAllText(Path.Combine(root, "Core.fs"), "module Core\nlet value = 42\nlet answer = value\n");
            File.WriteAllText(Path.Combine(root, "Deploy.targets"),
                "<Project><Target Name=\"Deploy\"><Message Text=\"not compiler input\" /></Target></Project>");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using (var store = new IndexStore(db, createNew: false))
            {
                File.WriteAllText(Path.Combine(root, "Core.fsproj"), project + "\n");
                DeltaRefresher.Refresh(store, root, ["Core.fsproj"]);
            }
            using (var manager = new IndexManager(root, db))
            {
                Assert.Equal(ProjectModelMode.Simple, manager.SelectedFSharpProjectModel);
                manager.Start();
                Assert.True(SpinWait.SpinUntil(() => manager.State == "ready", TimeSpan.FromSeconds(30)), manager.Health().Error);
                using var semantic = new SemanticService(manager, enableRoslynPersistence: false);
                using var result = JsonDocument.Parse(new NavigationTools(manager, semantic)
                    .SymbolAt("Core.fs", 3, 15, timeoutMs: 60_000));
                Assert.True(result.RootElement.GetProperty("found").GetBoolean(), result.RootElement.ToString());
                Assert.Equal("exact", result.RootElement.GetProperty("meta").GetProperty("confidence").GetString());
            }
            using var queries = new IndexQueries(db);
            Assert.False(queries.EvaluatedFSharpInputsReady());
            string telemetry = Path.Combine(root, ".codenav", "telemetry");
            Assert.Empty(Directory.Exists(telemetry) ? Directory.GetFiles(telemetry, "msbuild-*.jsonl") : []);
        }
        finally
        {
            Environment.SetEnvironmentVariable(modelVariable, previousModel);
            Environment.SetEnvironmentVariable(MsBuildDiagnosticSession.EnvironmentVariable, previousDiagnostics);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static FSharpSemanticOptionsSnapshot ParseImport(string root) =>
        ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", """
            <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Core</AssemblyName></PropertyGroup>
              <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
            """, "net10.0", "net10.0", importResolver: path => path switch
        {
            "Directory.Build.targets" => """
                <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <Import Project="Build/Deploy.targets" Condition=" '$(ExactTarget_Deploy_targets_imported)' != 'true' " />
                  <Import Project="MustNotRun.targets" Condition=" '$(ExactTarget_Deploy_targets_imported)' != 'true' " />
                </Project>
                """,
            "Build/Deploy.targets" => """
                <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup>
                  <ExactTarget_Deploy_targets_imported>true</ExactTarget_Deploy_targets_imported>
                  <McDeployServiceEnabled Condition=" '$(McDeployService)' == '' and '$(McDeployServiceName)' != '' and '$(IsWebApplication)' != 'true' ">true</McDeployServiceEnabled>
                  <DeployDir Condition=" '$(DeployDir)' == '' ">$(DeployRoot)$(McDeployServiceName)/</DeployDir>
                </PropertyGroup></Project>
                """,
            _ => throw new InvalidOperationException("unexpected import: " + path),
        }, directoryBuildTargetsPath: "Directory.Build.targets", workspaceRoot: root);

    private static JsonElement[] ReadRecords(string file) => File.ReadAllLines(file).Select(ParseRecord).ToArray();
    private static JsonElement ParseRecord(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
    private static string? Kind(JsonElement record) => record.GetProperty("kind").GetString();
    private static MsBuildDiagnosticSession Session(List<JsonElement> records) =>
        new("p.fsproj", "net10.0", "test", line => records.Add(ParseRecord(line)));
    private static BoundedMsBuildExpressionEvaluator Expressions(Dictionary<string, BoundedMsBuildProperty> properties,
        MsBuildDiagnosticSession? diagnostics, CancellationToken cancellationToken = default) =>
        new(properties, static (_, _) => new(false, ""), static (_, _) => new(false, false),
            cancellationToken, 16 * 1024, 32, diagnostics: diagnostics);
}
