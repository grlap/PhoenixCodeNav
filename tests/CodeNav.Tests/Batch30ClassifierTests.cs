using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// isTest classification (field: HubServiceTests carried [TestFixture] types yet filtered as
/// production, skewing includeTests counts). Policy per user direction: REFERENCES are the
/// classifier — nunit/mstest/xunit via PackageReference/packages.config OR via binary
/// &lt;Reference&gt;+HintPath (how this monolith actually consumes NUnit). Name shapes are a
/// narrow dotted-suffix fallback only: "*Test*" is a bad predictor — TestRoute.csproj is
/// production routing, not a unit test project.
/// </summary>
public class Batch30ClassifierTests
{
    private static ParsedProject Parse(string root, string relDir, string csprojName, string content)
    {
        string dir = Path.Combine(root, relDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, csprojName), content);
        return ProjectFileParser.Parse(root, $"{relDir}/{csprojName}");
    }

    [Fact]
    public void ClassifiesByTestFrameworkReferences()
    {
        string root = Directory.CreateTempSubdirectory("codenav-istest").FullName;
        try
        {
            // The field bug: no-dot name, NUnit consumed as a BINARY reference — must be a test
            // project via the REFERENCE, with the name contributing nothing.
            var hub = Parse(root, "HubServiceTests", "HubServiceTests.csproj",
                """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemGroup>
                    <Reference Include="nunit.framework, Version=3.13.0.0, Culture=neutral">
                      <HintPath>../3rdparty/nunit/nunit.framework.dll</HintPath>
                    </Reference>
                    <Compile Include="DispatchTests.cs" />
                  </ItemGroup>
                </Project>
                """);
            Assert.True(hub.IsTest, "binary nunit.framework reference must classify as test");

            // The user's counterexample, pinned: production code with 'Test' in the name.
            var route = Parse(root, "TestRoute", "TestRoute.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            Assert.False(route.IsTest, "TestRoute is production routing — name shapes must not classify it");

            // xunit v2 binary and MSTest v1 binary variants.
            var xu = Parse(root, "HubXunit", "HubXunit.csproj",
                """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemGroup>
                    <Reference Include="xunit.core"><HintPath>../3rdparty/xunit/xunit.core.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);
            Assert.True(xu.IsTest);
            var ms = Parse(root, "HubMs", "HubMs.csproj",
                """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemGroup>
                    <Reference Include="Microsoft.VisualStudio.QualityTools.UnitTestFramework" />
                  </ItemGroup>
                </Project>
                """);
            Assert.True(ms.IsTest);

            // A non-test binary reference alone must NOT classify (marker is equality, not contains).
            var plain = Parse(root, "HubPlain", "HubPlain.csproj",
                """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemGroup>
                    <Reference Include="ET.Api.Generated"><HintPath>../Common/ET.Api.Generated.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);
            Assert.False(plain.IsTest);

            // Dotted-suffix fallback unchanged.
            var dotted = Parse(root, "Acme.Billing.Tests", "Acme.Billing.Tests.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            Assert.True(dotted.IsTest);

            // User's custom-resolve shape: the reference name does NOT reduce to the bare simple
            // name — the contract is Contains("nunit.framework").
            var custom = Parse(root, "HubCustom", "HubCustom.csproj",
                """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <ItemGroup>
                    <Reference Include="ET.Vendored.nunit.framework.v2, Version=2.6.4.0" />
                  </ItemGroup>
                </Project>
                """);
            Assert.True(custom.IsTest, "Contains(\"nunit.framework\") must catch the custom-resolve shape");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // R3 — the custom-resolve-PROOF rule: when references are injected outside the csproj
    // entirely, the test attributes still sit in the compiled sources. Promotion requires the
    // project to also be a graph LEAF, so a production lib with one stray [Fact] file (mixed
    // projects are real) stays production.
    [Fact]
    public void CompiledTestAttributesPromoteLeafProjects()
    {
        string root = Directory.CreateTempSubdirectory("codenav-istest-r3").FullName;
        try
        {
            // Field shape: no dotted suffix, NO test-framework reference anywhere in the csproj
            // (custom resolve injects it at build time) — only the [TestFixture] in source.
            string hub = Path.Combine(root, "HubServiceTests");
            Directory.CreateDirectory(hub);
            File.WriteAllText(Path.Combine(hub, "HubServiceTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(hub, "DispatchTests.cs"),
                "namespace Hub { [NUnit.Framework.TestFixture] public class DispatchTests { } }");

            // Leaf-guard negative: CoreLib has an attributed file but App DEPENDS on it.
            string core = Path.Combine(root, "CoreLib");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "CoreLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(core, "InlineSmoke.cs"),
                "namespace CoreLib { public class InlineSmoke { [Xunit.Fact] public void Ping() { } } }");
            File.WriteAllText(Path.Combine(core, "Widget.cs"),
                "namespace CoreLib { public class Widget { } }");
            string app = Path.Combine(root, "App");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../CoreLib/CoreLib.csproj" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(app, "Program.cs"),
                "namespace App { public class Program { public CoreLib.Widget? W; } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new CodeNav.Core.Indexing.IndexQueries(dbPath);
            var flags = q.AllProjectTestFlags();
            Assert.True(flags["HubServiceTests"], "attributed leaf project must be promoted (R3)");
            Assert.False(flags["CoreLib"], "a DEPENDED-ON project with a stray attributed file must stay production (leaf guard)");
            Assert.False(flags["App"]);
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // Review (deferred pass, F3): R3 promoted per ROW, so a same-AssemblyName PAIR with an
    // incoming ProjectReference to one twin was HALF-promoted — and the name-keyed flags map
    // (last-row-wins) made the NAME's classification depend on scan order vs which twin carried
    // the edge (review-reproduced: identical workspaces, opposite answers). is_test is now
    // NAME-uniform: if any row of a name classifies, every row does.
    [Fact]
    public void SameNamePairClassifiesUniformlyRegardlessOfWhichTwinIsReferenced()
    {
        string root = Directory.CreateTempSubdirectory("codenav-istest-pair").FullName;
        try
        {
            foreach (var proj in new[] { "TeamHub", "TeamHub.NetNew" })
            {
                string dir = Path.Combine(root, proj);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{proj}.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net9.0</TargetFramework>
                        <AssemblyName>TeamHubFixtures</AssemblyName>
                      </PropertyGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(dir, "Fixtures.cs"),
                    $"namespace {proj.Replace('.', '_')} {{ [NUnit.Framework.TestFixture] public class Fixtures {{ }} }}");
            }
            // The consumer references the LAST-inserted twin (TeamHub.NetNew sorts after TeamHub),
            // the direction the review reproduced as flipping the name-level answer to FALSE.
            string consumer = Path.Combine(root, "OtherTests");
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(consumer, "OtherTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../TeamHub.NetNew/TeamHub.NetNew.csproj" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(consumer, "C.cs"), "namespace OtherTests { class C { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new CodeNav.Core.Indexing.IndexQueries(dbPath);
            Assert.True(q.AllProjectTestFlags()["TeamHubFixtures"],
                "the pair is ONE assembly — classification must not depend on which twin carries the edge");
            // ROW-level uniformity, order-independent (the name-collapse assert above can pass by
            // last-row-wins luck): each twin owns its own Fixtures.cs, so per-row flags are
            // observable through ProjectsContaining — BOTH rows must be test.
            Assert.True(q.ProjectsContaining("TeamHub/Fixtures.cs").Single().IsTest,
                "leaf twin row must be test");
            Assert.True(q.ProjectsContaining("TeamHub.NetNew/Fixtures.cs").Single().IsTest,
                "REFERENCED twin row must be test too (name-uniform is_test)");
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
