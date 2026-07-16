using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public sealed class InternalsVisibleToSemanticTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiteralFriendAssemblyControlsExactSubtypeVerification(bool grantInternals)
    {
        string root = Directory.CreateTempSubdirectory("codenav-ivt").FullName;
        try
        {
            WriteWorkspace(root, grantInternals);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20000),
                "manager did not become queryable");

            using var semantic = new SemanticService(manager);
            Assert.True(semantic.FrameworkRefsAvailable,
                "reference assemblies are required for the semantic friend-assembly regression");
            var tools = new NavigationTools(manager, semantic);

            JsonElement implementations = Parse(tools.Implementations(
                name: "ISecretContract", arity: 0, timeoutMs: 60000));
            JsonElement hierarchy = Parse(tools.TypeHierarchy(
                name: "ISecretContract", arity: 0, timeoutMs: 60000));

            Assert.Equal(1, implementations.GetProperty("implementations").GetArrayLength());
            Assert.Equal(1, hierarchy.GetProperty("derivedOrImplementing").GetArrayLength());

            if (grantInternals)
            {
                Assert.Equal("exact",
                    implementations.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.False(implementations.TryGetProperty("implementationsConfidence", out _));
                Assert.False(implementations.TryGetProperty("partialReason", out _));

                Assert.Equal("exact",
                    hierarchy.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.False(hierarchy.TryGetProperty("derivedConfidence", out _));
                Assert.False(hierarchy.TryGetProperty("partialReason", out _));
            }
            else
            {
                // The negative half proves that the fix modeled a real project declaration; it
                // did not grant every project blanket access to internal source symbols.
                Assert.Equal("heuristic",
                    implementations.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.Equal("heuristic",
                    implementations.GetProperty("implementationsConfidence").GetString());
                Assert.Equal("no_semantic_implementers",
                    implementations.GetProperty("partialReason").GetString());

                Assert.Equal("exact",
                    hierarchy.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.Equal("heuristic", hierarchy.GetProperty("derivedConfidence").GetString());
                Assert.Equal("no_semantic_derived",
                    hierarchy.GetProperty("partialReason").GetString());
            }
        }
        finally
        {
            Batch42Support.Cleanup(root);
        }
    }

    public static TheoryData<string, string> UnsupportedGenerationShapes => new()
    {
        {
            "GenerateAssemblyInfo=false",
            SdkContractsProject("""
                <PropertyGroup><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "GenerateInternalsVisibleToAttributes=false",
            SdkContractsProject("""
                <PropertyGroup><GenerateInternalsVisibleToAttributes>false</GenerateInternalsVisibleToAttributes></PropertyGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "Remove",
            SdkContractsProject("""
                <ItemGroup>
                  <InternalsVisibleTo Include="Friend.Consumer" />
                  <InternalsVisibleTo Remove="Friend.Consumer" />
                </ItemGroup>
                """)
        },
        {
            "Exclude",
            SdkContractsProject("""
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" Exclude="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "PublicKey metadata",
            SdkContractsProject("""
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" PublicKey="001122" /></ItemGroup>
                """)
        },
        {
            "Key update",
            SdkContractsProject("""
                <ItemGroup>
                  <InternalsVisibleTo Include="Friend.Consumer" />
                  <InternalsVisibleTo Update="Friend.Consumer" Key="001122" />
                </ItemGroup>
                """)
        },
        {
            "item-definition PublicKey",
            SdkContractsProject("""
                <ItemDefinitionGroup>
                  <InternalsVisibleTo><PublicKey>001122</PublicKey></InternalsVisibleTo>
                </ItemDefinitionGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "SignAssembly=true",
            SdkContractsProject("""
                <PropertyGroup><SignAssembly>true</SignAssembly></PropertyGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "PublicSign=true",
            SdkContractsProject("""
                <PropertyGroup><PublicSign>true</PublicSign></PropertyGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "AssemblyOriginatorKeyFile",
            SdkContractsProject("""
                <PropertyGroup><AssemblyOriginatorKeyFile>friend.snk</AssemblyOriginatorKeyFile></PropertyGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "mixed-case GenerateAssemblyInfo",
            SdkContractsProject("""
                <PropertyGroup><generateassemblyinfo>false</generateassemblyinfo></PropertyGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "mixed-case SignAssembly",
            SdkContractsProject("""
                <PropertyGroup><signassembly>true</signassembly></PropertyGroup>
                <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                """)
        },
        {
            "mixed-case item PublicKey",
            SdkContractsProject("""
                <ItemGroup>
                  <InternalsVisibleTo Include="Friend.Consumer"><publickey>001122</publickey></InternalsVisibleTo>
                </ItemGroup>
                """)
        },
        {
            "legacy custom item",
            """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <AssemblyName>Friend.Contracts</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="ISecretContract.cs" />
                <InternalsVisibleTo Include="Friend.Consumer" />
              </ItemGroup>
            </Project>
            """
        },
    };

    [Theory]
    [MemberData(nameof(UnsupportedGenerationShapes))]
    public void UnsupportedGenerationShapeNeverGrantsCompilerAccess(string _, string contractsProject)
    {
        string root = Directory.CreateTempSubdirectory("codenav-ivt-boundary").FullName;
        try
        {
            WriteWorkspace(root, contractsProject);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20000));
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;

            var tools = new NavigationTools(manager, semantic);
            JsonElement result = SemanticRetry.ParseWithRetry(
                () => tools.Implementations(
                    name: "ISecretContract", arity: 0, timeoutMs: 60000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        new[] { "no_semantic_implementers", "symbol_not_resolved" }
                            .Contains(reason.GetString()),
                "unsupported IVT shape denies compiler access");

            Assert.Equal("heuristic", result.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Contains(result.GetProperty("partialReason").GetString(),
                new[] { "no_semantic_implementers", "symbol_not_resolved" });
        }
        finally
        {
            Batch42Support.Cleanup(root);
        }
    }

    [Fact]
    public void WarmSemanticProjectReloadsWhenOnlyFriendGrantIsRemoved()
    {
        string root = Directory.CreateTempSubdirectory("codenav-ivt-warm").FullName;
        try
        {
            WriteWorkspace(root, grantInternals: true);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20000));
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);

            JsonElement before = Parse(tools.Implementations(
                name: "ISecretContract", arity: 0, timeoutMs: 60000));
            Assert.Equal("exact", before.GetProperty("meta").GetProperty("confidence").GetString());

            (long FileCount, long HashSum) oldFingerprint;
            using (var q = manager.OpenQueries())
                oldFingerprint = q.ProjectFingerprint("Friend.Contracts");
            WriteWorkspace(root, grantInternals: false);
            Assert.True(manager.RequestRefresh(new[] { "Contracts/Contracts.csproj" }));
            Assert.True(WaitUntil(() =>
            {
                using var q = manager.OpenQueries();
                return q.ProjectFingerprint("Friend.Contracts") != oldFingerprint;
            }, 20000), "index did not publish the project-only IVT edit");

            JsonElement after = Parse(tools.Implementations(
                name: "ISecretContract", arity: 0, timeoutMs: 60000));
            Assert.Equal("heuristic", after.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal("no_semantic_implementers", after.GetProperty("partialReason").GetString());
        }
        finally
        {
            Batch42Support.Cleanup(root);
        }
    }

    [Fact]
    public void ImportedFriendAuthorityReturnsCandidatesWithoutClaimingExactness()
    {
        string root = Directory.CreateTempSubdirectory("codenav-ivt-imported").FullName;
        try
        {
            WriteWorkspace(root, grantInternals: true);
            File.WriteAllText(Path.Combine(root, "Directory.Build.targets"),
                """
                <Project>
                  <ItemGroup>
                    <InternalsVisibleTo Remove="Friend.Consumer" />
                  </ItemGroup>
                </Project>
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20000));
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);

            JsonElement ParseUnproven(Func<string> call) => SemanticRetry.ParseWithRetry(
                call,
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        reason.GetString() == "project_model_unproven",
                "project_model_unproven semantic result");

            JsonElement implementations = ParseUnproven(() => tools.Implementations(
                name: "ISecretContract", arity: 0, timeoutMs: 60000));
            JsonElement hierarchy = ParseUnproven(() => tools.TypeHierarchy(
                name: "ISecretContract", arity: 0, timeoutMs: 60000));
            JsonElement definition = ParseUnproven(() => tools.Definition(
                name: "ISecretContract", path: "Consumer/SecretImplementation.cs", line: 2,
                mode: "semantic", timeoutMs: 60000));
            JsonElement references = ParseUnproven(() => tools.References(
                name: "ISecretContract", path: "Contracts/ISecretContract.cs", line: 2,
                mode: "semantic", timeoutMs: 60000));
            JsonElement callers = ParseUnproven(() => tools.Callers(
                name: "Run", path: "Contracts/ISecretContract.cs", line: 4,
                timeoutMs: 60000));
            JsonElement callees = ParseUnproven(() => tools.Callees(
                name: "Invoke", path: "Consumer/SecretImplementation.cs", line: 6,
                timeoutMs: 60000));

            // Phoenix deliberately keeps the useful local-model candidate, but an imported target
            // can revoke that grant. The relationship must therefore never be advertised as an
            // exact fact from the real build.
            Assert.Equal(1, implementations.GetProperty("implementations").GetArrayLength());
            Assert.Equal("indexed",
                implementations.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.True(implementations.GetProperty("partial").GetBoolean());
            Assert.Equal("project_model_unproven",
                implementations.GetProperty("partialReason").GetString());

            Assert.Equal(1, hierarchy.GetProperty("derivedOrImplementing").GetArrayLength());
            Assert.Equal("indexed",
                hierarchy.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.True(hierarchy.GetProperty("partial").GetBoolean());
            Assert.Equal("project_model_unproven",
                hierarchy.GetProperty("partialReason").GetString());

            AssertProjectModelUnproven(definition);
            AssertProjectModelUnproven(references);
            AssertProjectModelUnproven(callers);
            AssertProjectModelUnproven(callees);
            Assert.False(references.TryGetProperty("totalIsLowerBound", out _));
            string summary = references.GetProperty("summary").GetString()!;
            Assert.False(summary.StartsWith("at least ", StringComparison.OrdinalIgnoreCase),
                $"non-monotonic project-model uncertainty is not a lower bound: {summary}");
        }
        finally
        {
            Batch42Support.Cleanup(root);
        }
    }

    private static void WriteWorkspace(string root, bool grantInternals)
    {
        string contracts = Path.Combine(root, "Contracts");
        Directory.CreateDirectory(contracts);
        string friendItem = grantInternals
            ? "<InternalsVisibleTo Include=\"Friend.Consumer\" />"
            : "";
        WriteWorkspace(root, SdkContractsProject($$"""
            <ItemGroup>{{friendItem}}</ItemGroup>
            """));
    }

    private static void WriteWorkspace(string root, string contractsProject)
    {
        string contracts = Path.Combine(root, "Contracts");
        Directory.CreateDirectory(contracts);
        File.WriteAllText(Path.Combine(contracts, "Contracts.csproj"), contractsProject);
        File.WriteAllText(Path.Combine(contracts, "ISecretContract.cs"),
            """
            namespace FriendContracts;
            internal interface ISecretContract
            {
                void Run();
            }
            """);

        string consumer = Path.Combine(root, "Consumer");
        Directory.CreateDirectory(consumer);
        File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>Friend.Consumer</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Contracts/Contracts.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(consumer, "SecretImplementation.cs"),
            """
            namespace FriendConsumer;
            internal sealed class SecretImplementation : FriendContracts.ISecretContract
            {
                public void Run() { }

                internal static void Invoke(FriendContracts.ISecretContract contract) => contract.Run();
            }
            """);
    }

    private static string SdkContractsProject(string extra) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
            <AssemblyName>Friend.Contracts</AssemblyName>
          </PropertyGroup>
          {{extra}}
        </Project>
        """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static void AssertProjectModelUnproven(JsonElement result)
    {
        Assert.False(result.TryGetProperty("error", out JsonElement error),
            error.ValueKind == JsonValueKind.Undefined ? null : error.GetString());
        Assert.Equal("indexed", result.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.True(result.GetProperty("partial").GetBoolean());
        Assert.Equal("project_model_unproven", result.GetProperty("partialReason").GetString());
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
