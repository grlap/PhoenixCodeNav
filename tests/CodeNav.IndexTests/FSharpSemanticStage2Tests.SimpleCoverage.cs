using System.Text.Json;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void ApproximateCoverageCannotBecomeCompleteEvenAfterSuccessfulScans()
    {
        var evaluated = new FSharpReferencesCoverage(0, 0, 0, 0, 0, true, [], []);
        Assert.True(evaluated.WorkspaceComplete);
        var approximate = evaluated with { ApproximateModel = true };
        Assert.False(approximate.WorkspaceComplete);
        Assert.False((approximate with { ScansComplete = true }).WorkspaceComplete);
        var body = new FSharpCalleesCoverage(true, false, false);
        Assert.True(body.Complete);
        Assert.False((body with { ApproximateModel = true }).Complete);
    }

    [Fact]
    public void SimpleModelImportedOnlyConsumerCannotClaimWorkspaceCompleteness()
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-scope").FullName;
        try
        {
            WriteProject(root, "Dependency/Dependency.fsproj", SdkProject("net10.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 42\n");
            WriteProject(root, "Consumer/Consumer.fsproj", SdkProjectWithBody("net10.0", """
                <Import Project="Refs.props" />
                <ItemGroup><Compile Include="Consumer.fs" /></ItemGroup>
                """));
            WriteProject(root, "Consumer/Refs.props", """
                <Project><ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj" /></ItemGroup></Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs", "module Consumer\nlet result = Dependency.value\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Evaluated);
            using var simpleService = new SemanticService(fixture.Manager, enableRoslynPersistence: false,
                fsharpProjectModel: ProjectModelMode.Simple);
            var simpleTools = new NavigationTools(fixture.Manager, simpleService);
            JsonElement simple = ReadReferences(simpleTools, "Dependency/Dependency.fs", 2, 6);
            Assert.Equal(0, simple.GetProperty("totalReferences").GetInt32());
            using var evaluated = new SemanticService(fixture.Manager, enableRoslynPersistence: false,
                fsharpProjectModel: ProjectModelMode.Evaluated);
            JsonElement baseline = ReadReferences(new NavigationTools(fixture.Manager, evaluated),
                "Dependency/Dependency.fs", 2, 6);
            Assert.Equal(1, baseline.GetProperty("totalReferences").GetInt32());
            Assert.True(baseline.GetProperty("coverage").GetProperty("workspaceComplete").GetBoolean());
            Assert.DoesNotContain("approximate", baseline.GetProperty("summary").GetString());
            Assert.False(baseline.TryGetProperty("totalIsApproximate", out _));
            AssertApproximateCountScope(simple);
            Assert.Equal(2, simple.GetProperty("coverage").GetProperty("potentialConsumersEvaluated").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SimpleModelConditionedOverInclusionIsNotARealWorkspaceLowerBound()
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-over").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup><Compile Include="Core.fs" /><Compile Include="Extra.fs" Condition="false" /></ItemGroup>
                """));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\n");
            WriteProject(root, "Core/Extra.fs", "module Extra\nlet result = Core.value\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Evaluated);
            using var simpleService = new SemanticService(fixture.Manager, enableRoslynPersistence: false,
                fsharpProjectModel: ProjectModelMode.Simple);
            var simpleTools = new NavigationTools(fixture.Manager, simpleService);
            JsonElement simple = ReadReferences(simpleTools, "Core/Core.fs", 2, 6);
            Assert.Equal(1, simple.GetProperty("totalReferences").GetInt32());
            using var evaluated = new SemanticService(fixture.Manager, enableRoslynPersistence: false,
                fsharpProjectModel: ProjectModelMode.Evaluated);
            JsonElement baseline = ReadReferences(new NavigationTools(fixture.Manager, evaluated), "Core/Core.fs", 2, 6);
            Assert.Equal(0, baseline.GetProperty("totalReferences").GetInt32());
            AssertApproximateCountScope(simple);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("implementations")]
    public void SimpleModelCountOperationsDiscloseApproximateScope(string operation)
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-count").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", """
                module Core
                type IWorker =
                    abstract Run: unit -> int
                type Worker() =
                    interface IWorker with
                        member _.Run() = 42
                let target () = 42
                let caller () = target ()
                """);
            using var fixture = Fixture.Create(root, ProjectModelMode.Evaluated);
            using var simpleService = new SemanticService(fixture.Manager, enableRoslynPersistence: false,
                fsharpProjectModel: ProjectModelMode.Simple);
            var simpleTools = new NavigationTools(fixture.Manager, simpleService);
            string raw = CallSemantic(() => operation switch
            {
                "references" => simpleTools.References(path: "Core/Core.fs", line: 7, column: 6,
                    mode: "semantic", timeoutMs: 60_000),
                "callers" => simpleTools.Callers(path: "Core/Core.fs", line: 7, column: 6, timeoutMs: 60_000),
                "callees" => simpleTools.Callees(path: "Core/Core.fs", line: 8, column: 6, timeoutMs: 60_000),
                _ => simpleTools.Implementations(path: "Core/Core.fs", line: 2, column: 7, timeoutMs: 60_000),
            });
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            string total = operation switch
            {
                "references" => "totalReferences",
                "callers" => "totalCallers",
                "callees" => "totalCallees",
                _ => "totalImplementations",
            };
            Assert.Equal(1, response.GetProperty(total).GetInt32());
            AssertApproximateCountScope(response, bodyLocal: operation == "callees");
            using var evaluated = new SemanticService(fixture.Manager, enableRoslynPersistence: false,
                fsharpProjectModel: ProjectModelMode.Evaluated);
            var evaluatedTools = new NavigationTools(fixture.Manager, evaluated);
            JsonElement baseline = Parse(CallSemantic(() => operation switch
            {
                "references" => evaluatedTools.References(path: "Core/Core.fs", line: 7, column: 6,
                    mode: "semantic", timeoutMs: 60_000),
                "callers" => evaluatedTools.Callers(path: "Core/Core.fs", line: 7, column: 6, timeoutMs: 60_000),
                "callees" => evaluatedTools.Callees(path: "Core/Core.fs", line: 8, column: 6, timeoutMs: 60_000),
                _ => evaluatedTools.Implementations(path: "Core/Core.fs", line: 2, column: 7, timeoutMs: 60_000),
            }));
            Assert.False(baseline.TryGetProperty("error", out _), baseline.ToString());
            Assert.Equal(1, baseline.GetProperty(total).GetInt32());
            Assert.StartsWith("Exactly", baseline.GetProperty("summary").GetString());
            Assert.False(baseline.TryGetProperty("totalIsApproximate", out _));
            Assert.False(baseline.TryGetProperty("countScope", out _));
            Assert.False(baseline.TryGetProperty("scanIncomplete", out _));
            Assert.False(response.GetProperty("scanIncomplete").GetBoolean());
            Assert.False(baseline.GetProperty("coverage").TryGetProperty("approximateModel", out _));
        }
        finally { Cleanup(root); }
    }

    private static JsonElement ReadReferences(NavigationTools tools, string path, int line, int column)
    {
        JsonElement response = Parse(CallSemantic(() => tools.References(path: path, line: line,
            column: column, mode: "semantic", timeoutMs: 60_000)));
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        return response;
    }

    private static void AssertApproximateCountScope(JsonElement response, bool bodyLocal = false)
    {
        Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.True(response.TryGetProperty("totalIsApproximate", out JsonElement approximate), response.ToString());
        Assert.True(approximate.GetBoolean());
        Assert.Equal("approximate_project_model", response.GetProperty("countScope").GetString());
        Assert.False(response.TryGetProperty("totalIsLowerBound", out JsonElement lower) && lower.GetBoolean());
        string? summary = response.GetProperty("summary").GetString();
        Assert.Contains("approximate", summary);
        Assert.DoesNotContain("Exactly", summary);
        Assert.DoesNotContain("At least", summary);
        Assert.Contains("not a proven", summary);
        JsonElement coverage = response.GetProperty("coverage");
        Assert.True(coverage.GetProperty("approximateModel").GetBoolean());
        Assert.False(coverage.GetProperty(bodyLocal ? "complete" : "workspaceComplete").GetBoolean());
    }
}
