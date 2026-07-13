using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

public class Batch48VariantResolutionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EvaluatorProducesStableConditionedTargetFrameworkOutputs()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net472;net8.0</TargetFrameworks>
                <Configurations>Release</Configurations>
                <AssemblyName>Partner.Framework.Net</AssemblyName>
                <TargetName>Partner.Framework</TargetName>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net472'">
                <OutputPath>Build/Net472</OutputPath>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <OutputPath>Build/Net8</OutputPath>
              </PropertyGroup>
            </Project>
            """;

        ProjectVariantEvaluation evaluation = ProjectVariantEvaluator.Evaluate(
            "src/Partner.Framework.Net.csproj", System.Text.Encoding.UTF8.GetBytes(xml));

        Assert.Equal(2, evaluation.Variants.Count);
        Assert.Equal(2, evaluation.Variants.Select(variant => variant.StableVariantKey).Distinct().Count());
        Assert.Contains(evaluation.Variants, variant => variant.TargetFramework == "net472" &&
            variant.Outputs.Single().TargetPath == "src/Build/Net472/Partner.Framework.dll");
        Assert.Contains(evaluation.Variants, variant => variant.TargetFramework == "net8.0" &&
            variant.Outputs.Single().TargetPath == "src/Build/Net8/Partner.Framework.dll");
    }

    [Fact]
    public void BaseTypeFactsRetainGenericArityAndAliasEvidence()
    {
        const string source = """
            using GenericAlias = Contracts.ITemplate<int>;
            namespace Consumer;
            class ViaAlias : GenericAlias { }
            class Generic : Contracts.ITemplate<string> { }
            class Plain : Contracts.ITemplate { }
            class GlobalGeneric : global::Contracts.ITemplate<long> { }
            """;
        IReadOnlyList<BaseTypeFact> facts = BaseTypeIndexer.Parse(source,
            new BaseTypeParseContext("latest", []));

        Assert.Contains(facts, fact => fact.LookupName == "ITemplate" &&
            fact.SyntacticArity == 1 && fact.ResolutionKind == "syntaxAlias");
        Assert.Contains(facts, fact => fact.LookupName == "ITemplate" &&
            fact.SyntacticArity == 1 && fact.ResolutionKind == "direct");
        Assert.Contains(facts, fact => fact.LookupName == "ITemplate" &&
            fact.SyntacticArity == 0 && fact.ResolutionKind == "direct");
        Assert.Contains(facts, fact => fact.LookupName == "ITemplate" &&
            fact.SyntacticArity == 1 && fact.QualifierText == "global::Contracts" &&
            fact.ResolutionKind == "direct");
    }

    [Fact]
    public void EvaluatorNormalizesLegacyProjectReferencePaths()
    {
        const string xml = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><AssemblyName>Consumer</AssemblyName><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Common\Common.csproj" /></ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """;
        ProjectVariantEvaluation evaluation = ProjectVariantEvaluator.Evaluate(
            "src/Consumer/Consumer.csproj", System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal("src/Common/Common.csproj",
            Assert.Single(Assert.Single(evaluation.Variants).ProjectReferenceFacts).Include);
    }

    [Fact]
    public void EvaluatorEnumeratesConditionOnlyConfigurationsAndPackagesConfig()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Conditional.Output</AssemblyName></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'"><OutputPath>Build/Debug</OutputPath></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'"><OutputPath>Build/Release</OutputPath></PropertyGroup>
            </Project>
            """;
        const string packages = """
            <packages><package id="Legacy.Dependency" version="4.2.0" targetFramework="net472" /></packages>
            """;
        ProjectVariantEvaluation evaluation = ProjectVariantEvaluator.Evaluate(
            "src/Conditional.csproj", System.Text.Encoding.UTF8.GetBytes(xml),
            System.Text.Encoding.UTF8.GetBytes(packages));

        Assert.Equal(["Debug", "Release"], evaluation.Variants
            .Select(variant => variant.Configuration).OrderBy(value => value).ToArray());
        Assert.Contains(evaluation.Variants, variant => variant.Outputs.Single().TargetPath ==
            "src/Build/Debug/net8.0/Conditional.Output.dll");
        Assert.Contains(evaluation.Variants, variant => variant.Outputs.Single().TargetPath ==
            "src/Build/Release/net8.0/Conditional.Output.dll");
        Assert.All(evaluation.Variants, variant => Assert.Contains(variant.PackageReferenceFacts,
            package => package.Include == "Legacy.Dependency" && package.Metadata == "4.2.0"));
    }

    [Fact]
    public void HintPathSelectsTheExactOutputVariant()
    {
        string root = Directory.CreateTempSubdirectory("codenav-variant-output").FullName;
        try
        {
            WriteVariantWorkspace(root);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using var q = new IndexQueries(db);
            ProjectVariantRow consumer = Assert.Single(q.VariantsForProjectPath(
                "Consumer/Consumer.csproj"));
            ProjectVariantRow selected = Assert.Single(q.VariantDependencies(consumer.Id));

            Assert.Equal("Framework/Partner.Framework.Net.csproj", selected.ProjectPath);
            Assert.Equal("net8.0", selected.TargetFramework);
            Assert.Equal("Framework/Build/Net8/Partner.Framework.dll",
                q.VariantOutputs(selected.Id).Single().TargetPath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SemanticCandidatesIgnoreFtsAndRespectTargetArity()
    {
        string root = Directory.CreateTempSubdirectory("codenav-variant-arity").FullName;
        try
        {
            WriteVariantWorkspace(root);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using var manager = new IndexManager(root, db);
            manager.Start();
            Assert.True(WaitUntil(() => manager.Health().State == "ready", 20_000),
                manager.Health().Error);

            // Remove the FTS row only after IndexManager has completed any startup refresh. This
            // pins the semantic-authority regression without racing the background writer.
            using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM fts_content WHERE rowid=(SELECT id FROM files WHERE path='Consumer/Implementations.cs')";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement generic = Parse(tools.Implementations(path: "Framework/Contracts.cs",
                line: 3, name: "ITemplate", timeoutMs: 90_000));
            Assert.True(generic.GetProperty("meta").GetProperty("confidence").GetString() == "exact",
                generic.ToString());
            Assert.Contains(generic.GetProperty("implementations").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("display").GetString()!.Contains("GenericImplementation"));

            JsonElement plain = Parse(tools.Implementations(path: "Framework/Contracts.cs",
                line: 2, name: "ITemplate", timeoutMs: 90_000));
            Assert.Equal("exact", plain.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.DoesNotContain(plain.GetProperty("implementations").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("display").GetString()!.Contains("GenericImplementation"));
        }
        finally { Cleanup(root); }
    }

    private static void WriteVariantWorkspace(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Framework"));
        Directory.CreateDirectory(Path.Combine(root, "Consumer"));
        File.WriteAllText(Path.Combine(root, "Framework", "Partner.Framework.Net.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net472;net8.0</TargetFrameworks>
                <Configurations>Release</Configurations>
                <AssemblyName>Partner.Framework.Net</AssemblyName>
                <TargetName>Partner.Framework</TargetName>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net472'"><OutputPath>Build/Net472</OutputPath></PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'"><OutputPath>Build/Net8</OutputPath></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Framework", "Contracts.cs"), """
            namespace Partner.Framework;
            public interface ITemplate { }
            public interface ITemplate<T> { }
            """);
        File.WriteAllText(Path.Combine(root, "Consumer", "Consumer.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>Partner.Consumer</AssemblyName></PropertyGroup>
              <ItemGroup>
                <Reference Include="Partner.Framework.Net"><HintPath>../Framework/Build/Net8/Partner.Framework.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Consumer", "Implementations.cs"), """
            namespace Partner.Consumer;
            public sealed class GenericImplementation : Partner.Framework.ITemplate<int> { }
            """);
    }

    private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            Thread.Sleep(50);
        }
        return predicate();
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
