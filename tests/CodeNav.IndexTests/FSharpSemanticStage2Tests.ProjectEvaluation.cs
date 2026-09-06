using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void ProjectReferenceClosureResolvesDefinitionsAndCountsOnlyRootProjectUses()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-reference").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <ProjectReference Include="../Dependency/Dependency.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs",
                "module Core\nlet value = Dependency.value\nlet second = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj", SdkProject("net10.0",
                "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 1\n");

            using var fixture = Fixture.Create(root);
            string symbolRaw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 25, timeoutMs: 60_000));
            JsonElement symbol = Parse(symbolRaw);
            Assert.True(symbol.GetProperty("found").GetBoolean(), symbolRaw);
            Assert.Equal("Dependency.value",
                symbol.GetProperty("symbol").GetProperty("fullName").GetString());
            Assert.False(symbol.TryGetProperty("declarationsOutsideSelectedProjectCount",
                out _));
            Assert.Equal(1, symbol.GetProperty(
                "declarationsFromProjectReferenceClosureCount").GetInt32());

            string definitionRaw = CallSemantic(() => fixture.Tools.Definition(
                path: "Core/Core.fs", line: 2, column: 25, mode: "auto",
                timeoutMs: 60_000));
            JsonElement definition = Parse(definitionRaw);
            Assert.True(definition.GetProperty("found").GetBoolean(), definitionRaw);
            Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() ==
                "Dependency/Dependency.fs");

            string referencesRaw = CallSemantic(() => fixture.Tools.References(
                path: "Core/Core.fs", line: 2, column: 25, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement references = Parse(referencesRaw);
            Assert.True(references.GetProperty("found").GetBoolean(), referencesRaw);
            Assert.Equal(2, references.GetProperty("totalReferences").GetInt32());
            Assert.All(references.GetProperty("groups")[0].GetProperty("samples")
                .EnumerateArray(), sample => Assert.Equal("Core/Core.fs",
                    sample.GetProperty("path").GetString()));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("net45", "netstandard1.0", true)]
    [InlineData("net44", "netstandard1.0", false)]
    [InlineData("net45", "netstandard1.1", true)]
    [InlineData("net451", "netstandard1.2", true)]
    [InlineData("net45", "netstandard1.2", false)]
    [InlineData("net46", "netstandard1.3", true)]
    [InlineData("net452", "netstandard1.3", false)]
    [InlineData("net461", "netstandard1.4", true)]
    [InlineData("net46", "netstandard1.4", false)]
    [InlineData("net461", "netstandard1.5", true)]
    [InlineData("net46", "netstandard1.5", false)]
    [InlineData("net461", "netstandard1.6", true)]
    [InlineData("net46", "netstandard1.6", false)]
    [InlineData("net461", "netstandard2.0", true)]
    [InlineData("net46", "netstandard2.0", false)]
    [InlineData("net472", "netstandard2.1", false)]
    [InlineData("netcoreapp1.0", "netstandard1.6", true)]
    [InlineData("netcoreapp1.0", "netstandard2.0", false)]
    [InlineData("netcoreapp2.0", "netstandard2.0", true)]
    [InlineData("netcoreapp2.2", "netstandard2.1", false)]
    [InlineData("netcoreapp3.0", "netstandard2.1", true)]
    [InlineData("net5.0", "netstandard2.1", true)]
    [InlineData("net8.0-windows", "netstandard2.0", true)]
    [InlineData("netstandard2.0", "netstandard1.6", true)]
    [InlineData("netstandard1.6", "netstandard2.0", false)]
    public void ProjectReferenceNetStandardCompatibilityUsesOnlyThePublishedTable(
        string consumerTargetFramework, string childTargetFramework, bool expected)
    {
        bool selected = SemanticService.TrySelectFSharpProjectReferenceTargetFramework(
            consumerTargetFramework, [childTargetFramework], out string? selectedTargetFramework,
            out string tableRow, out bool multiTargetExactMatchOnly);

        Assert.Equal(expected, selected);
        Assert.Equal(expected ? childTargetFramework : null, selectedTargetFramework);
        Assert.False(multiTargetExactMatchOnly);
        Assert.NotEmpty(tableRow);
    }

    [Fact]
    public void ProjectReferenceNetStandardCompatibilityKeepsExactAndEmptyChoicesFailClosed()
    {
        Assert.True(SemanticService.TrySelectFSharpProjectReferenceTargetFramework(
            "net8.0", ["net8.0", "netstandard2.0"], out string? exact,
            out string exactRule, out bool exactOnly));
        Assert.Equal("net8.0", exact);
        Assert.Contains("Exact target-framework match", exactRule);
        Assert.False(exactOnly);

        Assert.False(SemanticService.TrySelectFSharpProjectReferenceTargetFramework(
            "net8.0", [], out string? missing, out string missingRule,
            out bool missingExactOnly));
        Assert.Null(missing);
        Assert.Contains("no evaluated target-framework context", missingRule);
        Assert.False(missingExactOnly);
    }

    [Fact]
    public void ProjectReferenceNetStandardCompatibilityResolvesAndExactMatchWins()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-netstandard-compatibility").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net8.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet observed = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("netstandard2.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs",
                "module Dependency\nlet value = 42\n");

            using (var compatibleFixture = Fixture.Create(root))
            {
                string raw = CallSemantic(() => compatibleFixture.Tools.Definition(
                    path: "App/App.fs", line: 2, column: 28, mode: "semantic",
                    timeoutMs: 60_000));
                JsonElement response = Parse(raw);
                Assert.True(response.GetProperty("found").GetBoolean(), raw);
                Assert.Equal("exact",
                    response.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.Contains(response.GetProperty("declarations").EnumerateArray(),
                    declaration => declaration.GetProperty("path").GetString() ==
                                   "Dependency/Dependency.fs");
                Assert.Equal(1, response.GetProperty(
                    "declarationsFromProjectReferenceClosureCount").GetInt32());
                Assert.False(response.TryGetProperty("projectReferenceTypeCheckContexts", out _));
            }

            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("net8.0;netstandard2.0", "Dependency.fs"));
            using var exactFixture = Fixture.Create(root);
            string exactRaw = CallSemantic(() => exactFixture.Tools.Definition(
                path: "App/App.fs", line: 2, column: 28, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement exact = Parse(exactRaw);
            Assert.True(exact.GetProperty("found").GetBoolean(), exactRaw);
            Assert.Equal("exact",
                exact.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("netstandard2.0", "NETSTANDARD1_0_OR_GREATER", 3)]
    [InlineData("netstandard2.0", "NETSTANDARD1_6_OR_GREATER", 3)]
    [InlineData("netstandard2.0", "NETSTANDARD2_0_OR_GREATER", 3)]
    [InlineData("netstandard2.0", "NETSTANDARD2_1_OR_GREATER", 5)]
    [InlineData("netstandard2.1", "NETSTANDARD1_0_OR_GREATER", 3)]
    [InlineData("netstandard2.1", "NETSTANDARD1_6_OR_GREATER", 3)]
    [InlineData("netstandard2.1", "NETSTANDARD2_0_OR_GREATER", 3)]
    [InlineData("netstandard2.1", "NETSTANDARD2_1_OR_GREATER", 3)]
    public void CompatibleChildUsesSdkNetStandardCumulativeDefines(
        string childTargetFramework, string conditionalDefine, int expectedStartLine)
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-netstandard-defines").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net8.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet observed = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject(childTargetFramework, "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", $$"""
                module Dependency
                #if {{conditionalDefine}}
                let value = 42
                #else
                let value = -1
                #endif
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Definition(
                path: "App/App.fs", line: 2, column: 28, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            JsonElement declaration = Assert.Single(response.GetProperty("declarations")
                .EnumerateArray());
            Assert.Equal("Dependency/Dependency.fs",
                declaration.GetProperty("path").GetString());
            Assert.Equal(expectedStartLine,
                declaration.GetProperty("startLine").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void IndexedNetStandardCumulativeDefineSelectsActiveDeclaration()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-indexed-netstandard-defines").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("netstandard2.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", """
                module ConditionalDeclarations
                #if NETSTANDARD2_0_OR_GREATER
                type ActiveDeclaration = ActiveDeclaration of int
                #else
                type InactiveDeclaration = InactiveDeclaration of int
                #endif
                """);

            using var fixture = Fixture.Create(root);
            JsonElement active = Assert.Single(Parse(fixture.Tools.SearchSymbol(
                    "ActiveDeclaration", kinds: "union", match: "exact",
                    pathGlob: "Library/Library.fs"))
                .GetProperty("symbols").EnumerateArray());
            Assert.Equal("Library/Library.fs", active.GetProperty("path").GetString());
            Assert.Equal(3, active.GetProperty("startLine").GetInt32());

            JsonElement inactive = Parse(fixture.Tools.SearchSymbol(
                "InactiveDeclaration", match: "exact", pathGlob: "Library/Library.fs"));
            Assert.Empty(inactive.GetProperty("symbols").EnumerateArray());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void CompatibleChildUsesItsOwnTargetFrameworkForPackageAndFSharpCoreAssets()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-netstandard-child-assets").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            const string fsharpCoreVersion = "10.1.204";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "netstandard2.0", "System.IO.Hashing.dll")));
            Assert.True(File.Exists(Path.Combine(packagesRoot, "fsharp.core",
                fsharpCoreVersion, "lib", "netstandard2.0", "FSharp.Core.dll")));

            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net8.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            WriteProject(root, "Dependency/Dependency.fsproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Dependency.fs" />
                    <PackageReference Include="{{packageId}}" Version="{{packageVersion}}" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Dependency/Dependency.fs", """
                namespace Dependency
                open System.IO.Hashing
                module PackageEvidence =
                    let create () = XxHash64()
                """);
            WritePackageAssetsWithAutoReferencedPackage(root,
                "Dependency/Dependency.fsproj", "netstandard2.0", packageId,
                packageVersion, packagesRoot,
                "lib/netstandard2.0/System.IO.Hashing.dll",
                ("FSharp.Core", fsharpCoreVersion,
                    "lib/netstandard2.0/FSharp.Core.dll"));

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("value",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.DoesNotContain("package_assets_stale", raw,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fsharp_core_reference_host_fallback", raw,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void CompatibleClosureFailsClosedWhenOneAssemblyWouldUseTwoTargetFrameworks()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-netstandard-assembly-conflict").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net8.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Left/Left.fsproj" />
                  <ProjectReference Include="../Right/Right.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            foreach ((string side, string targetFramework) in new[]
                     {
                         ("Left", "netstandard2.0"),
                         ("Right", "netstandard2.1"),
                     })
            {
                WriteProject(root, $"{side}/{side}.fsproj",
                    SdkProjectWithBody(targetFramework, $"""
                        <ItemGroup>
                          <Compile Include="{side}.fs" />
                          <ProjectReference Include="../Shared/Shared.fsproj" />
                        </ItemGroup>
                        """));
                WriteProject(root, $"{side}/{side}.fs",
                    $"module {side}\nlet value = Shared.value\n");
            }
            WriteProject(root, "Shared/Shared.fsproj",
                SdkProject("netstandard2.0;netstandard2.1", "Shared.fs"));
            WriteProject(root, "Shared/Shared.fs", "module Shared\nlet value = 42\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_project_options_conflict",
                response.GetProperty("error").GetString());
            Assert.Contains("project/TFM contexts",
                response.GetProperty("detail").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PlatformQualifiedNetRootFailsClosedWithoutBasePackSubstitution()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-netstandard-platform").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net8.0-windows", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet observed = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("netstandard2.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs",
                "module Dependency\nlet value = 42\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Definition(
                path: "App/App.fs", line: 2, column: 28, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_framework_references_unavailable",
                response.GetProperty("error").GetString());
            string detail = response.GetProperty("detail").GetString()!;
            Assert.Contains("net8.0-windows", detail);
            Assert.Contains("failed closed", detail);
            Assert.Contains("did not substitute the base .NET reference pack", detail);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("netstandard2.0/../../outside-reference-pack")]
    [InlineData("netstandard2.2")]
    [InlineData("netstandard2.0-windows")]
    public void FrameworkReferencePathsRejectNonCanonicalNetStandardTfms(
        string targetFramework)
    {
        IReadOnlyList<string> paths = ReferenceAssemblyLocator.FrameworkReferencePaths(
            targetFramework, out string? sourceDirectory);

        Assert.Empty(paths);
        Assert.Null(sourceDirectory);
    }

    [Fact]
    public void NetStandardCompatibilityFailuresNameTheConsultedTableRule()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-netstandard-refusal").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net472", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("netstandard2.1", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 1\n");

            using (var frameworkFixture = Fixture.Create(root))
            {
                JsonElement response = Parse(CallSemantic(() => frameworkFixture.Tools.SymbolAt(
                    "App/App.fs", 2, 5, timeoutMs: 60_000)));
                Assert.Equal("fsharp_semantic_project_reference_target_framework_unavailable",
                    response.GetProperty("error").GetString());
                string detail = response.GetProperty("detail").GetString()!;
                Assert.Contains("net472", detail);
                Assert.Contains("netstandard2.1", detail);
                Assert.Contains("N/A", detail);
                Assert.Contains("Microsoft .NET Standard support table", detail);
            }

            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net9.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("net8.0;netstandard2.0", "Dependency.fs"));
            using var multiTargetFixture = Fixture.Create(root);
            JsonElement multiTarget = Parse(CallSemantic(() =>
                multiTargetFixture.Tools.SymbolAt("App/App.fs", 2, 5,
                    timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_project_reference_target_framework_unavailable",
                multiTarget.GetProperty("error").GetString());
            string multiTargetDetail = multiTarget.GetProperty("detail").GetString()!;
            Assert.Contains("net9.0", multiTargetDetail);
            Assert.Contains("net8.0, netstandard2.0", multiTargetDetail);
            Assert.Contains("Multi-target referenced projects remain exact-match-only",
                multiTargetDetail);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void NetStandard1CompatibilitySelectsThenFailsClosedAtCompileMaterialization()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-netstandard1-materialization").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("netstandard2.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("netstandard1.6", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs",
                "module Dependency\nlet value = 42\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_framework_references_unavailable",
                response.GetProperty("error").GetString());
            string detail = response.GetProperty("detail").GetString()!;
            Assert.Contains("Dependency/Dependency.fsproj", detail);
            Assert.Contains("netstandard1.6", detail);
            Assert.Contains("granular autoReferenced NETStandard.Library closure", detail);
            Assert.Contains("did not substitute the wider netstandard2.0 reference pack", detail);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void TransitiveProjectReferenceClosureTypeChecksChildAgainstLeafWithoutCountingChildUses()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-transitive").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Mid/Mid.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet observed = (Mid.make()).Value\n");
            WriteProject(root, "Mid/Mid.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="Mid.fs" />
                  <ProjectReference Include="../Leaf/Leaf.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "Mid/Mid.fs",
                "module Mid\nlet make () = Leaf.Widget(42)\n");
            WriteProject(root, "Leaf/Leaf.fsproj", SdkProject("net10.0", "Leaf.fs"));
            WriteProject(root, "Leaf/Leaf.fs", """
                namespace Leaf
                type Widget(value: int) =
                    member _.Value = value
                """);

            using var fixture = Fixture.Create(root);
            string definitionRaw = CallSemantic(() => fixture.Tools.Definition(
                path: "App/App.fs", line: 2, column: 31, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement definition = Parse(definitionRaw);
            Assert.True(definition.GetProperty("found").GetBoolean(), definitionRaw);
            Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() == "Leaf/Leaf.fs");
            Assert.Equal("exact",
                definition.GetProperty("meta").GetProperty("confidence").GetString());

            string referencesRaw = CallSemantic(() => fixture.Tools.References(
                path: "App/App.fs", line: 2, column: 31, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement references = Parse(referencesRaw);
            Assert.Equal(1, references.GetProperty("totalReferences").GetInt32());
            Assert.All(references.GetProperty("groups")[0].GetProperty("samples")
                .EnumerateArray(), sample => Assert.Equal("App/App.fs",
                    sample.GetProperty("path").GetString()));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureMergesChildDiagnosticsWithoutLosingRootDiagnostics()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-diagnostics").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", """
                module App
                let rootBroken : int = "root"
                let targetValue = 42
                """);
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("net10.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", """
                module Dependency
                let childBroken : int = "child"
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 3, 7, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("targetValue",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Contains("fsharp_semantic_diagnostics_present",
                response.GetProperty("partialReason").GetString());
            Assert.Equal("indexed",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            JsonElement[] errors = response.GetProperty("diagnostics").EnumerateArray()
                .Where(diagnostic => diagnostic.GetProperty("severity").GetString() == "error")
                .ToArray();
            Assert.Contains(errors, diagnostic =>
                diagnostic.GetProperty("path").GetString() == "App/App.fs");
            Assert.Contains(errors, diagnostic =>
                diagnostic.GetProperty("path").GetString() ==
                "Dependency/Dependency.fs");
            Assert.Equal(2, response.GetProperty("diagnosticCount").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceDiamondReportsLeafDiagnosticsOnce()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-diamond-diagnostics").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Left/Left.fsproj" />
                  <ProjectReference Include="../Right/Right.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet targetValue = 42\n");
            foreach (string side in new[] { "Left", "Right" })
            {
                WriteProject(root, $"{side}/{side}.fsproj", SdkProjectWithBody("net10.0", $"""
                    <ItemGroup>
                      <Compile Include="{side}.fs" />
                      <ProjectReference Include="../Leaf/Leaf.fsproj" />
                    </ItemGroup>
                    """));
                WriteProject(root, $"{side}/{side}.fs",
                    $"module {side}\nlet observed = Leaf.value\n");
            }
            WriteProject(root, "Leaf/Leaf.fsproj", SdkProject("net10.0", "Leaf.fs"));
            WriteProject(root, "Leaf/Leaf.fs",
                "module Leaf\nlet value : int = \"broken\"\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 7, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("indexed",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal(1, response.GetProperty("diagnostics").EnumerateArray().Count(
                diagnostic => diagnostic.GetProperty("severity").GetString() == "error" &&
                              diagnostic.GetProperty("path").GetString() == "Leaf/Leaf.fs"));
            Assert.Equal(1, response.GetProperty("diagnosticCount").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DisableTransitiveProjectReferencesKeepsTheRootDirectOnly()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-direct-only").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <PropertyGroup>
                  <DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>
                </PropertyGroup>
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Mid/Mid.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet observed = (Mid.make()).Value\n");
            WriteProject(root, "Mid/Mid.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="Mid.fs" />
                  <ProjectReference Include="../Leaf/Leaf.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "Mid/Mid.fs", """
                module Mid
                let make () = Leaf.Widget(42)
                let observed = (make()).Value
                """);
            WriteProject(root, "Leaf/Leaf.fsproj", SdkProject("net10.0", "Leaf.fs"));
            WriteProject(root, "Leaf/Leaf.fs", """
                namespace Leaf
                type Widget(value: int) =
                    member _.Value = value
                """);

            using var fixture = Fixture.Create(root);
            string appRaw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 31, timeoutMs: 60_000));
            JsonElement app = Parse(appRaw);
            Assert.False(app.GetProperty("found").GetBoolean(), appRaw);
            Assert.Contains("fsharp_semantic_diagnostics_present",
                app.GetProperty("partialReason").GetString());

            string midRaw = CallSemantic(() => fixture.Tools.Definition(
                path: "Mid/Mid.fs", line: 3, column: 27, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement mid = Parse(midRaw);
            Assert.True(mid.GetProperty("found").GetBoolean(), midRaw);
            Assert.Contains(mid.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() == "Leaf/Leaf.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LegacyProjectReferencesKeepEachConsumerDirectOnly()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-legacy-direct-only").FullName;
        try
        {
            string? fsharpCore = ReferenceAssemblyLocator.FSharpCoreReferencePath(
                "net472", out _);
            Assert.NotNull(fsharpCore);
            string copiedFSharpCore = Path.Combine(root, "Lib", "FSharp.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(copiedFSharpCore)!);
            File.Copy(fsharpCore!, copiedFSharpCore);

            WriteProject(root, "App/App.fsproj", LegacyProjectWithBody("App", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Mid/Mid.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet observed = (Mid.make()).Value\n");
            WriteProject(root, "Mid/Mid.fsproj", LegacyProjectWithBody("Mid", """
                <ItemGroup>
                  <Compile Include="Mid.fs" />
                  <ProjectReference Include="../Leaf/Leaf.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "Mid/Mid.fs", """
                module Mid
                let make () = Leaf.Widget(42)
                let observed = (make()).Value
                """);
            WriteProject(root, "Leaf/Leaf.fsproj", LegacyProjectWithBody("Leaf", """
                <ItemGroup><Compile Include="Leaf.fs" /></ItemGroup>
                """));
            WriteProject(root, "Leaf/Leaf.fs", """
                namespace Leaf
                type Widget(value: int) =
                    member _.Value = value
                """);

            using var fixture = Fixture.Create(root);
            string appRaw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 31, timeoutMs: 60_000));
            JsonElement app = Parse(appRaw);
            Assert.False(app.GetProperty("found").GetBoolean(), appRaw);
            Assert.Contains("fsharp_semantic_diagnostics_present",
                app.GetProperty("partialReason").GetString());

            string midRaw = CallSemantic(() => fixture.Tools.Definition(
                path: "Mid/Mid.fs", line: 3, column: 27, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement mid = Parse(midRaw);
            Assert.True(mid.GetProperty("found").GetBoolean(), midRaw);
            Assert.Contains(mid.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() == "Leaf/Leaf.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureFailsClosedForCSharpCycleAndMissingExactTfm()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-boundaries").FullName;
        try
        {
            WriteProject(root, "CSharp/CSharp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteProject(root, "CSharp/Class.cs", "namespace CSharp; public class Class;");
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../CSharp/CSharp.csproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");

            using (var csharpFixture = Fixture.Create(root))
            {
                JsonElement csharp = Parse(CallSemantic(() => csharpFixture.Tools.SymbolAt(
                    "App/App.fs", 2, 5, timeoutMs: 60_000)));
                Assert.Equal("fsharp_semantic_project_references_unsupported",
                    csharp.GetProperty("error").GetString());
                Assert.Contains("stale last-built project DLL",
                    csharp.GetProperty("detail").GetString());
            }

            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Missing/Missing.fsproj" />
                </ItemGroup>
                """));
            using (var missingFixture = Fixture.Create(root))
            {
                JsonElement missing = Parse(CallSemantic(() => missingFixture.Tools.SymbolAt(
                    "App/App.fs", 2, 5, timeoutMs: 60_000)));
                Assert.Equal("fsharp_semantic_project_reference_unavailable",
                    missing.GetProperty("error").GetString());
                Assert.Contains("missing, unindexed, unreadable",
                    missing.GetProperty("detail").GetString());
            }

            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj"
                                    Aliases="alternate" />
                </ItemGroup>
                """));
            using (var metadataFixture = Fixture.Create(root))
            {
                JsonElement metadata = Parse(CallSemantic(() => metadataFixture.Tools.SymbolAt(
                    "App/App.fs", 2, 5, timeoutMs: 60_000)));
                Assert.Equal("fsharp_semantic_project_reference_metadata_unsupported",
                    metadata.GetProperty("error").GetString());
                Assert.Contains("metadata or item operations",
                    metadata.GetProperty("detail").GetString());
            }

            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("net9.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 1\n");
            using (var tfmFixture = Fixture.Create(root))
            {
                JsonElement tfm = Parse(CallSemantic(() => tfmFixture.Tools.SymbolAt(
                    "App/App.fs", 2, 5, timeoutMs: 60_000)));
                Assert.Equal("fsharp_semantic_project_reference_target_framework_unavailable",
                    tfm.GetProperty("error").GetString());
                Assert.Contains("Dependency/Dependency.fsproj",
                    tfm.GetProperty("detail").GetString());
                Assert.Contains("net9.0", tfm.GetProperty("detail").GetString());
                Assert.Contains("no compatibility row",
                    tfm.GetProperty("detail").GetString());
            }

            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProjectWithBody("net10.0", """
                    <ItemGroup>
                      <Compile Include="Dependency.fs" />
                      <ProjectReference Include="../App/App.fsproj" />
                    </ItemGroup>
                    """));
            using var cycleFixture = Fixture.Create(root);
            JsonElement cycle = Parse(CallSemantic(() => cycleFixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_project_reference_cycle",
                cycle.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureFailsClosedForAnAmbiguousHostPath()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-case-ambiguous").FullName;
        string dbPath = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../DEPS/DEPENDENCY.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            WriteProject(root, "Deps/Dependency.fsproj",
                SdkProject("net10.0", "Dependency.fs"));
            WriteProject(root, "Deps/Dependency.fs", "module Dependency\nlet value = 1\n");

            using var fixture = Fixture.Create(root);
            using (var store = new IndexStore(dbPath, createNew: false))
            using (var transaction = store.BeginTransaction())
            {
                store.InsertProject(transaction, new ParsedProject(
                    "deps/dependency.fsproj", "dependency", "sdk", null, "net10.0",
                    false, [], [], null, [], "parsed", Language: "fs"));
                transaction.Commit();
            }

            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_project_reference_unavailable",
                response.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureRejectsDistinctProjectsWithSameAssemblyIdentity()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-assembly-conflict").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../First/First.fsproj" />
                  <ProjectReference Include="../Second/Second.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            WriteProject(root, "First/First.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>Shared.Dependency</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="First.fs" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "First/First.fs", "module First\nlet value = 1\n");
            WriteProject(root, "Second/Second.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>Shared.Dependency</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Second.fs" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "Second/Second.fs", "module Second\nlet value = 2\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_project_options_conflict",
                response.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureUsesLiteralPhysicalCompanionPath()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-physical-companion").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet result = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("net10.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs",
                "module Dependency\nlet value = 1\n");
            WriteProject(root, "Dependency/Dependency.Net.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <AssemblyName>Dependency</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Dependency.Net.fs" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "Dependency/Dependency.Net.fs",
                "module Dependency\nlet value = 2\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Definition(
                path: "App/App.fs", line: 2, column: 26, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Contains(response.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() ==
                "Dependency/Dependency.fs");
            Assert.DoesNotContain(response.GetProperty("declarations").EnumerateArray(),
                declaration => declaration.GetProperty("path").GetString() ==
                    "Dependency/Dependency.Net.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureSharesEvaluationBudgetsAcrossProjects()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-evaluation-budget").FullName;
        try
        {
            string properties = string.Join(Environment.NewLine,
                Enumerable.Range(0, 300).Select(index =>
                    $"<ClosureProperty{index}>value</ClosureProperty{index}>"));
            WriteProject(root, "App/App.fsproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    {properties}
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="App.fs" />
                    <ProjectReference Include="../Dependency/Dependency.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            WriteProject(root, "Dependency/Dependency.fsproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    {properties}
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Dependency.fs" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "Dependency/Dependency.fs",
                "module Dependency\nlet value = 1\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_property_limit",
                response.GetProperty("error").GetString());
            Assert.Contains("ProjectReference closure",
                response.GetProperty("detail").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureRefusesSourceBudgetsBeforeReadingTheRemainder()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-source-budget").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj",
                SdkProject("net10.0", "First.fs", "Use.fs"));
            WriteProject(root, "Core/First.fs", "module First\nlet value = 1\n");
            WriteProject(root, "Core/Use.fs", "module Use\nlet target = First.value\n");

            using var fixture = Fixture.Create(root);
            int sourceReads = 0;
            fixture.Semantic.BeforeFSharpSemanticSourceReadForTest = _ => sourceReads++;
            fixture.Semantic.FSharpSemanticSourceFilesLimitForTest = 1;
            JsonElement countLimited = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 2, 7, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_source_limit",
                countLimited.GetProperty("error").GetString());
            Assert.Equal(0, sourceReads);

            sourceReads = 0;
            fixture.Semantic.FSharpSemanticSourceFilesLimitForTest = null;
            fixture.Semantic.FSharpSemanticSourceBytesLimitForTest = 1;
            JsonElement bytesLimited = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 2, 7, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_source_bytes_limit",
                bytesLimited.GetProperty("error").GetString());
            Assert.Equal(1, sourceReads);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureUsesPinnedChildSourceAfterDiskMutation()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-source-snapshot").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet result = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProject("net10.0", "Dependency.fs"));
            string dependencyPath = Path.Combine(root, "Dependency", "Dependency.fs");
            WriteProject(root, "Dependency/Dependency.fs",
                "module Dependency\nlet value = 42\n");

            using var fixture = Fixture.Create(root);
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
                File.WriteAllText(dependencyPath,
                    "module ChangedOnDisk\nlet replacement = 0\n");
            string raw = CallSemantic(() => fixture.Tools.Definition(
                path: "App/App.fs", line: 2, column: 26, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("Dependency.value",
                response.GetProperty("symbol").GetProperty("fullName").GetString());
            Assert.Contains(response.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() ==
                "Dependency/Dependency.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectReferenceClosureReverifiesChildBinaryOriginsAfterFcs()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-binary-snapshot").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj" />
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs",
                "module App\nlet result = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj",
                SdkProjectWithBody("net10.0", """
                    <ItemGroup>
                      <Compile Include="Dependency.fs" />
                      <Reference Include="CodeNav.Core">
                        <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                      </Reference>
                    </ItemGroup>
                    """));
            WriteProject(root, "Dependency/Dependency.fs",
                "module Dependency\nlet value = 42\n");
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(binary);

            using var fixture = Fixture.Create(root);
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
            {
                using var stream = new FileStream(binary, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Read);
                stream.Position = 32;
                int original = stream.ReadByte();
                Assert.True(original >= 0);
                stream.Position = 32;
                stream.WriteByte((byte)(original ^ 1));
                File.SetLastWriteTimeUtc(binary, originalWriteTime);
            };
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 26, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_reference_changed",
                response.GetProperty("error").GetString());
            Assert.Contains("fsharp_binary_references_snapshotted",
                response.GetProperty("partialReason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImportedCompileFailureUsesCurrentBoundedEvaluatorDetail()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-import-detail").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                  <Import Project="../Build/Items.props" />
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Build/Items.props", """
                <Project>
                  <ItemGroup><Compile Include="../Core/Injected.fs" /></ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5)));

            Assert.Equal("fsharp_semantic_import_items_unsupported",
                response.GetProperty("error").GetString());
            string detail = response.GetProperty("detail").GetString()!;
            Assert.Equal(
                "An imported .props file contributes an active Compile, Reference, or other unsupported semantic item. Imported PackageReference and PackageVersion authority is evaluated; ProjectReference closure reports its own unsupported cause.",
                detail);
            Assert.DoesNotContain("Stage 2A", detail, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SemanticProjectCaptureEvaluatesBoundedInputsAndFailsClosedForUnsupportedOnes()
    {
        const string prefix = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            """;
        FSharpSemanticOptionsSnapshot wildcard =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"**/*.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_compile_order_unavailable", wildcard.Error);

        FSharpSemanticOptionsSnapshot excluded =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Ghost.fs;Core.fs\" Exclude=\"Ghost.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_compile_order_unavailable", excluded.Error);

        FSharpSemanticOptionsSnapshot conditioned =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\"><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Null(conditioned.Error);
        Assert.Equal(["Core/Core.fs"], conditioned.SourceFiles);

        FSharpSemanticOptionsSnapshot imported =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup><Import Project=\"Custom.targets\" /></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_import_unsupported", imported.Error);

        FSharpSemanticOptionsSnapshot package =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><PackageReference Include=\"Example\" Version=\"1.0.0\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Null(package.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Example", "1.0.0")],
            package.PackageReferences);

        FSharpSemanticOptionsSnapshot sourceEscape =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"../../Outside.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_path_outside_workspace", sourceEscape.Error);

        FSharpSemanticOptionsSnapshot rootedSource =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"/Outside.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_path_outside_workspace", rootedSource.Error);

        FSharpSemanticOptionsSnapshot hintEscape =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><Reference Include=\"Outside\"><HintPath>../../Outside.dll</HintPath></Reference></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_path_outside_workspace", hintEscape.Error);

        FSharpSemanticOptionsSnapshot conditionedAssembly =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net10.0'\"><AssemblyName>Wrong</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Null(conditionedAssembly.Error);
        Assert.Equal("Wrong", conditionedAssembly.AssemblyName);

        FSharpSemanticOptionsSnapshot unevaluatedAssembly =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<PropertyGroup><AssemblyName>$(TargetName)</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_assembly_name_unavailable", unevaluatedAssembly.Error);

        FSharpSemanticOptionsSnapshot duplicateAssembly =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<PropertyGroup><AssemblyName>First</AssemblyName><AssemblyName>Second</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Null(duplicateAssembly.Error);
        Assert.Equal("Second", duplicateAssembly.AssemblyName);

        FSharpSemanticOptionsSnapshot conditionedHint =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><Reference Include=\"Dependency\"><HintPath Condition=\"'$(TargetFramework)' == 'net10.0'\">../Lib/Dependency.dll</HintPath></Reference></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Null(conditionedHint.Error);
        Assert.Equal(["Lib/Dependency.dll"], conditionedHint.HintPathReferences);

        FSharpSemanticOptionsSnapshot duplicateHint =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><Reference Include=\"Dependency\"><HintPath>../Lib/First.dll</HintPath><HintPath>../Lib/Second.dll</HintPath></Reference></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_reference_unresolved", duplicateHint.Error);

        foreach (string unsafeDefaultMembership in new[]
                 {
                     "<PropertyGroup><EnableDefaultCompileItems>$(UseDefaults)</EnableDefaultCompileItems></PropertyGroup>",
                     "<PropertyGroup><EnableDefaultCompileItems>true</EnableDefaultCompileItems></PropertyGroup>",
                     "<PropertyGroup><EnableDefaultCompileItems>maybe</EnableDefaultCompileItems></PropertyGroup>",
                 })
        {
            FSharpSemanticOptionsSnapshot unsafeDefaults =
                ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                    prefix + unsafeDefaultMembership +
                    "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                    "net10.0", "net10.0");
            Assert.Equal("fsharp_semantic_compile_order_unavailable", unsafeDefaults.Error);
        }

        foreach (string evaluatedDefaultMembership in new[]
                 {
                     "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net10.0'\"><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>",
                     "<PropertyGroup><EnableDefaultCompileItems>true</EnableDefaultCompileItems><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>",
                 })
        {
            FSharpSemanticOptionsSnapshot evaluatedDefaults =
                ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                    prefix + evaluatedDefaultMembership +
                    "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                    "net10.0", "net10.0");
            Assert.Null(evaluatedDefaults.Error);
            Assert.Equal(["Core/Core.fs"], evaluatedDefaults.SourceFiles);
        }

        FSharpSemanticOptionsSnapshot safeDefaults =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix +
                "<PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
                "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net10.0", "net10.0");
        Assert.Null(safeDefaults.Error);
        Assert.Equal(["Core/Core.fs"], safeDefaults.SourceFiles);
    }
}
