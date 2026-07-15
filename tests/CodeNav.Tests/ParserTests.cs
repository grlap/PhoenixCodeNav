using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeNav.Tests;

public class ProjectFileParserTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codenav-parser").FullName;

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteFile(string relPath, string content)
    {
        string full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return relPath;
    }

    [Fact]
    public void ParsesLegacyCsproj()
    {
        WriteFile("src/App/packages.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="13.0.3" targetFramework="net472" />
            </packages>
            """);
        string rel = WriteFile("src/App/App.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <ProjectGuid>{11111111-2222-3333-4444-555555555555}</ProjectGuid>
                <AssemblyName>Acme.App</AssemblyName>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Service.cs" />
                <Compile Include="Properties\AssemblyInfo.cs" />
                <Compile Include="..\Shared\Linked.cs" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj">
                  <Project>{99999999-8888-7777-6666-555555555555}</Project>
                  <Name>Lib</Name>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """);

        var p = ProjectFileParser.Parse(_root, rel);

        Assert.Equal("legacy", p.Style);
        Assert.Equal("Acme.App", p.Name);
        Assert.Equal("11111111-2222-3333-4444-555555555555", p.Guid);
        Assert.Equal("net472", p.TargetFrameworks);
        Assert.False(p.IsTest);
        Assert.Equal(new[] { "src/Lib/Lib.csproj" }, p.ProjectRefRelPaths);
        Assert.NotNull(p.ExplicitCompileItems);
        Assert.Contains("src/App/Service.cs", p.ExplicitCompileItems!);
        Assert.Contains("src/App/Properties/AssemblyInfo.cs", p.ExplicitCompileItems!);
        Assert.Contains("src/Shared/Linked.cs", p.ExplicitCompileItems!); // linked file resolved across dirs
        Assert.Contains(p.PackageRefs, x => x.Package == "Newtonsoft.Json" && x.Version == "13.0.3");
    }

    [Fact]
    public void ParsesSdkCsprojAndDetectsTests()
    {
        string rel = WriteFile("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.4.2" />
                <ProjectReference Include="..\..\src\App\App.csproj" />
              </ItemGroup>
            </Project>
            """);

        var p = ProjectFileParser.Parse(_root, rel);

        Assert.Equal("sdk", p.Style);
        Assert.True(p.IsTest); // both name suffix and xunit package
        Assert.Null(p.ExplicitCompileItems); // SDK globbing
        Assert.Equal(new[] { "src/App/App.csproj" }, p.ProjectRefRelPaths);
    }

    [Fact]
    public async Task SdkPathParserDoesNotProbeAdjacentLegacyPackagesConfig()
    {
        if (!OperatingSystem.IsLinux()) return;

        string rel = WriteFile("src/Sdk/Sdk.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        string fifo = Path.Combine(_root, "src", "Sdk", "packages.config");
        Assert.Equal(0, mkfifo(fifo, Convert.ToUInt32("600", 8)));

        Task<ParsedProject> parse = Task.Run(() => ProjectFileParser.Parse(_root, rel));
        Task first = await Task.WhenAny(parse, Task.Delay(TimeSpan.FromSeconds(1)));
        bool completedWithoutOpeningFifo = ReferenceEquals(first, parse);
        if (!completedWithoutOpeningFifo)
        {
            // Release a regressed reader so the test process and fixture cleanup cannot hang.
            await Task.Run(() =>
            {
                using (File.Open(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
            });
            Task released = await Task.WhenAny(parse, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(parse, released);
            Assert.True(parse.IsCompleted,
                "the parser remained blocked after the FIFO writer released it");
        }

        Assert.True(completedWithoutOpeningFifo,
            "SDK parsing must not inspect the legacy packages.config sibling");
        ParsedProject parsed = await parse;
        Assert.Equal("sdk", parsed.Style);
    }

    [Fact]
    public void ByteSnapshotPreservesUtf16CustomProjectResolutionFacts()
    {
        const string projectPath = "src/Custom/Custom.csproj";
        const string packagesPath = "src/Custom/packages.config";
        string projectFull = Path.Combine(_root,
            projectPath.Replace('/', Path.DirectorySeparatorChar));
        string packagesFull = Path.Combine(_root,
            packagesPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(projectFull)!);

        File.WriteAllText(packagesFull, """
            <?xml version="1.0" encoding="utf-16"?>
            <packages>
              <package id="Custom.Package" version="7.4.1" targetFramework="net472" />
            </packages>
            """, Encoding.Unicode);
        File.WriteAllText(projectFull, """
            <?xml version="1.0" encoding="utf-16"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <AssemblyName>Custom.Runtime</AssemblyName>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <InternalsVisibleTo Include="Custom.Friend" />
                <Compile Include="Generated\Client.cs" />
                <ProjectReference Include="..\Library\Library.csproj" />
                <Reference Include="Custom.Dependency, Version=4.0.0.0">
                  <HintPath>..\..\artifacts\Custom.Dependency.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """, Encoding.Unicode);

        ParsedProject pathParsed = ProjectFileParser.Parse(_root, projectPath);
        ParsedProject snapshotParsed = ProjectFileParser.ParseSnapshot(projectPath,
            File.ReadAllBytes(projectFull), File.ReadAllBytes(packagesFull));

        Assert.Equal(pathParsed.Name, snapshotParsed.Name);
        Assert.Equal(pathParsed.Style, snapshotParsed.Style);
        Assert.Equal(pathParsed.TargetFrameworks, snapshotParsed.TargetFrameworks);
        Assert.Equal(pathParsed.IsTest, snapshotParsed.IsTest);
        Assert.Equal(pathParsed.ProjectRefRelPaths, snapshotParsed.ProjectRefRelPaths);
        Assert.Equal(pathParsed.PackageRefs, snapshotParsed.PackageRefs);
        Assert.Equal(pathParsed.AssemblyRefs, snapshotParsed.AssemblyRefs);
        Assert.Equal(pathParsed.ExplicitCompileItems, snapshotParsed.ExplicitCompileItems);
        Assert.Equal(pathParsed.CompileOperations, snapshotParsed.CompileOperations);
        Assert.Equal(pathParsed.DefaultCompileItems, snapshotParsed.DefaultCompileItems);
        Assert.Equal(pathParsed.InternalsVisibleTo, snapshotParsed.InternalsVisibleTo);
        Assert.Equal(pathParsed.LoadStatus, snapshotParsed.LoadStatus);

        Assert.Contains(snapshotParsed.AssemblyRefs, reference =>
            reference.Assembly == "Custom.Dependency" &&
            reference.HintPath == "artifacts/Custom.Dependency.dll");
        Assert.Equal(new[] { "src/Library/Library.csproj" },
            snapshotParsed.ProjectRefRelPaths);
        Assert.Null(snapshotParsed.InternalsVisibleTo); // legacy items are not SDK assembly-info facts
    }

    [Fact]
    public void InternalsVisibleToRequiresAnUnconditionalLiteralSimpleName()
    {
        string rel = WriteFile("src/Friends/Friends.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <InternalsVisibleTo Include="Friend.One; Friend.Two" />
                <InternalsVisibleTo Include="friend.one" />
                <InternalsVisibleTo Include="Excluded.Friend" Exclude="Excluded.Friend" />
                <InternalsVisibleTo Include="Removed.Friend" />
                <InternalsVisibleTo Remove="Removed.Friend" />
                <InternalsVisibleTo Include="Updated.Friend" />
                <InternalsVisibleTo Update="Updated.Friend" PublicKey="001122" />
                <InternalsVisibleTo Include="$(DynamicFriend)" />
                <InternalsVisibleTo Include="Keyed.Friend" Key="$(FriendKey)" />
                <InternalsVisibleTo Include="PublicKey.Friend"><PublicKey>001122</PublicKey></InternalsVisibleTo>
                <InternalsVisibleTo Include="LowerPublicKey.Friend"><publickey>001122</publickey></InternalsVisibleTo>
                <InternalsVisibleTo Include="LowerPublicKeyAttribute.Friend" publickey="001122" />
                <InternalsVisibleTo Include="LiteralKey.Friend" Key="001122" />
                <InternalsVisibleTo Include="Strong.Friend, PublicKey=001122" />
                <internalsvisibletO Include="Friend.Three" />
              </ItemGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <InternalsVisibleTo Include="Conditional.Friend" />
              </ItemGroup>
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'">
                  <ItemGroup><InternalsVisibleTo Include="Chosen.Friend" /></ItemGroup>
                </When>
              </Choose>
              <Target Name="AddFriend">
                <ItemGroup><InternalsVisibleTo Include="Target.Friend" /></ItemGroup>
              </Target>
            </Project>
            """);

        ParsedProject parsed = ProjectFileParser.Parse(_root, rel);

        Assert.Equal(new[] { "Friend.One", "Friend.Three", "Friend.Two" },
            parsed.InternalsVisibleTo);
    }

    [Theory]
    [InlineData("GenerateAssemblyInfo")]
    [InlineData("GenerateInternalsVisibleToAttributes")]
    [InlineData("PublicKey")]
    [InlineData("SignAssembly")]
    [InlineData("PublicSign")]
    [InlineData("AssemblyOriginatorKeyFile")]
    [InlineData("generateassemblyinfo")]
    [InlineData("signassembly")]
    public void InternalsVisibleToFailsClosedWhenSdkGenerationIsDisabledOrKeyed(string property)
    {
        string value = property.ToUpperInvariant() switch
        {
            "GENERATEASSEMBLYINFO" or "GENERATEINTERNALSVISIBLETOATTRIBUTES" => "false",
            "SIGNASSEMBLY" or "PUBLICSIGN" => "true",
            "ASSEMBLYORIGINATORKEYFILE" => "friend.snk",
            _ => "001122",
        };
        string rel = WriteFile($"src/{property}/{property}.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <{{property}}>{{value}}</{{property}}>
              </PropertyGroup>
              <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
            </Project>
            """);

        ParsedProject parsed = ProjectFileParser.Parse(_root, rel);
        Assert.Null(parsed.InternalsVisibleTo);
    }

    [Theory]
    [InlineData("Key")]
    [InlineData("PublicKey")]
    [InlineData("key")]
    [InlineData("publickey")]
    public void InternalsVisibleToFailsClosedForItemDefinitionKeyMetadata(string metadata)
    {
        string rel = WriteFile($"src/ItemDefinition{metadata}/ItemDefinition{metadata}.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemDefinitionGroup>
                <InternalsVisibleTo><{{metadata}}>001122</{{metadata}}></InternalsVisibleTo>
              </ItemDefinitionGroup>
              <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
            </Project>
            """);

        ParsedProject parsed = ProjectFileParser.Parse(_root, rel);
        Assert.Null(parsed.InternalsVisibleTo);
    }

    [Fact]
    public void LegacyInternalsVisibleToItemIsNotAnSdkGeneratedAttribute()
    {
        string rel = WriteFile("src/LegacyFriend/LegacyFriend.csproj", """
            <Project ToolsVersion="15.0"
                     xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion></PropertyGroup>
              <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
            </Project>
            """);

        ParsedProject parsed = ProjectFileParser.Parse(_root, rel);
        Assert.Null(parsed.InternalsVisibleTo);
    }

    [Fact]
    public void ParsesClassicSolution()
    {
        string rel = WriteFile("All.sln", """

            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-2222-3333-4444-555555555555}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "SolutionFolder", "SolutionFolder", "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"
            EndProject
            Global
            EndGlobal
            """);

        var s = SolutionParser.Parse(_root, rel);

        Assert.Equal("All", s.Name);
        Assert.Equal(new[] { "src/App/App.csproj" }, s.ProjectRelPaths); // solution folders skipped
    }

    [Fact]
    public void ParsesSolutionFilter()
    {
        string rel = WriteFile("Slice.slnf", """
            {
              "solution": {
                "path": "All.sln",
                "projects": [ "src\\App\\App.csproj" ]
              }
            }
            """);

        var s = SolutionParser.Parse(_root, rel);
        Assert.Equal(new[] { "src/App/App.csproj" }, s.ProjectRelPaths);
    }
}

public class SyntaxIndexerTests
{
    private const string Sample = """
        using System;

        namespace Acme.Sample
        {
            public enum InvoiceStatus { None, Open, Closed }

            /// <summary>A service.</summary>
            public partial class InvoiceService : IInvoiceService
            {
                private readonly IClock _clock;

                public InvoiceService(IClock clock) { _clock = clock; }

                public decimal Total { get; set; }

                public void Submit(int id) { }

                public void Submit(int id, string reason) { }

                public T Map<T>(string input) { return default(T); }
            }

            public class InvoiceTests
            {
                [Fact]
                public void Should_Work() { }
            }
        }
        """;

    [Fact]
    public void ExtractsSymbolsWithSpansAndKinds()
    {
        var parsed = SyntaxIndexer.Parse("src/InvoiceService.cs", Sample);

        Assert.False(parsed.LooksGenerated);
        Assert.True(parsed.HasTestAttributes);

        var ns = Assert.Single(parsed.Symbols, s => s.Kind == "namespace");
        Assert.Equal("Acme.Sample", ns.Name);

        var cls = Assert.Single(parsed.Symbols, s => s.Kind == "class" && s.Name == "InvoiceService");
        Assert.True(cls.IsPartial);
        Assert.Contains("IInvoiceService", cls.Signature);
        Assert.Equal("Acme.Sample", cls.Namespace);

        var overloads = parsed.Symbols.Where(s => s.Kind == "method" && s.Name == "Submit").ToList();
        Assert.Equal(2, overloads.Count);
        Assert.NotEqual(overloads[0].Signature, overloads[1].Signature);

        var generic = Assert.Single(parsed.Symbols, s => s.Name == "Map");
        Assert.Equal(1, generic.Arity);

        var enumMembers = parsed.Symbols.Where(s => s.Kind == "enum_member").Select(s => s.Name).ToList();
        Assert.Equal(new[] { "None", "Open", "Closed" }, enumMembers);

        var ctor = Assert.Single(parsed.Symbols, s => s.Kind == "constructor");
        Assert.Equal("InvoiceService", ctor.Name);

        var testMethod = Assert.Single(parsed.Symbols, s => s.Name == "Should_Work");
        Assert.Equal("Fact", testMethod.AttrMarkers);

        // Parent chain: method's parent is the class, class's parent is the namespace.
        Assert.Equal(cls.OrdinalInFile, overloads[0].ParentOrdinal);
        Assert.Equal(ns.OrdinalInFile, cls.ParentOrdinal);

        // Spans are 1-based and sane.
        Assert.True(cls.StartLine > ns.StartLine);
        Assert.True(cls.EndLine <= ns.EndLine);
    }

    [Fact]
    public void DetectsGeneratedFiles()
    {
        Assert.True(FileClassifier.LooksGenerated("a/b/Thing.Designer.cs", "namespace X {}"));
        Assert.True(FileClassifier.LooksGenerated("a/b/Client.g.cs", "namespace X {}"));
        Assert.True(FileClassifier.LooksGenerated("a/b/Normal.cs", "//------\n// <auto-generated>\n//------\nnamespace X {}"));
        Assert.False(FileClassifier.LooksGenerated("a/b/Normal.cs", "namespace X {}"));
    }

    [Fact]
    public void FtsQueryQuotesTokens()
    {
        Assert.Equal("\"Guard\" \"NotNull\"", CodeNav.Core.Indexing.IndexQueries.FtsQuery("Guard.NotNull"));
        Assert.Equal("\"snake_case_name\"", CodeNav.Core.Indexing.IndexQueries.FtsQuery("snake_case_name"));
        Assert.Equal("", CodeNav.Core.Indexing.IndexQueries.FtsQuery("!!!"));
    }
}
