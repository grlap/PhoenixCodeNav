using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public class ProjectFileParserTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codenav-parser").FullName;

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
