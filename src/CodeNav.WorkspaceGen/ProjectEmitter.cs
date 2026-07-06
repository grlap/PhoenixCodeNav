using System.Text;

namespace CodeNav.WorkspaceGen;

/// <summary>
/// Owns: writing a planned workspace to disk — source files, csproj (legacy + SDK),
/// packages.config, solutions, solution filters, and root config files.
/// Does not own: shape decisions (WorkspacePlanner) or C# text (CodeEmitter).
/// </summary>
internal sealed class ProjectEmitter
{
    private const string CsprojTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    private readonly WorkspaceSpec _ws;
    private readonly CodeEmitter _code;
    private readonly string _root;

    private long _files;
    private long _lines;
    private long _bytes;

    public ProjectEmitter(WorkspaceSpec ws, string rootDir)
    {
        _ws = ws;
        _code = new CodeEmitter(ws);
        _root = rootDir;
    }

    public (long Files, long Lines, long Bytes) EmitAll()
    {
        Directory.CreateDirectory(_root);

        Parallel.ForEach(_ws.Projects, project => EmitProject(project));

        foreach (var sln in _ws.Solutions)
        {
            WriteFile(sln.RelPath, EmitSolution(sln));
        }
        EmitSolutionFilter();
        EmitRootFiles();

        return (Interlocked.Read(ref _files), Interlocked.Read(ref _lines), Interlocked.Read(ref _bytes));
    }

    // ---------------------------------------------------------------- projects

    private void EmitProject(ProjectSpec p)
    {
        var sources = _code.EmitProjectSources(p);
        foreach (var (relPath, content) in sources)
        {
            WriteFile($"{p.RelDir}/{relPath}", content);
        }

        if (p.Layer == Layer.Api)
        {
            WriteFile($"{p.RelDir}/web.config", EmitWebConfig(p));
            WriteFile($"{p.RelDir}/appsettings.json", EmitAppSettings(p));
        }

        bool hasPackagesConfig = p.Style == ProjectStyle.Legacy && p.Packages.Count > 0;
        if (hasPackagesConfig)
        {
            WriteFile($"{p.RelDir}/packages.config", EmitPackagesConfig(p));
        }

        string csproj = p.Style == ProjectStyle.Legacy
            ? EmitLegacyCsproj(p, sources.Select(s => s.RelPath).ToList(), hasPackagesConfig)
            : EmitSdkCsproj(p);
        WriteFile(p.CsprojRelPath, csproj);
    }

    private string EmitLegacyCsproj(ProjectSpec p, List<string> sourceFiles, bool hasPackagesConfig)
    {
        int depth = p.RelDir.Count(c => c == '/') + 1;
        string packagesPrefix = string.Concat(Enumerable.Repeat(@"..\", depth)) + @"packages\";

        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">""");
        sb.AppendLine("""  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />""");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("""    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>""");
        sb.AppendLine("""    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>""");
        sb.AppendLine($"    <ProjectGuid>{{{p.ProjectGuid.ToString().ToUpperInvariant()}}}</ProjectGuid>");
        sb.AppendLine("    <OutputType>Library</OutputType>");
        sb.AppendLine($"    <RootNamespace>{p.Ns}</RootNamespace>");
        sb.AppendLine($"    <AssemblyName>{p.Name}</AssemblyName>");
        sb.AppendLine("    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>");
        sb.AppendLine("    <FileAlignment>512</FileAlignment>");
        sb.AppendLine("    <Deterministic>true</Deterministic>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("""  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">""");
        sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
        sb.AppendLine("    <DebugType>full</DebugType>");
        sb.AppendLine("    <Optimize>false</Optimize>");
        sb.AppendLine(@"    <OutputPath>bin\Debug\</OutputPath>");
        sb.AppendLine("    <DefineConstants>DEBUG;TRACE</DefineConstants>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("""  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">""");
        sb.AppendLine("    <DebugType>pdbonly</DebugType>");
        sb.AppendLine("    <Optimize>true</Optimize>");
        sb.AppendLine(@"    <OutputPath>bin\Release\</OutputPath>");
        sb.AppendLine("    <DefineConstants>TRACE</DefineConstants>");
        sb.AppendLine("  </PropertyGroup>");

        sb.AppendLine("  <ItemGroup>");
        foreach (var assembly in new[] { "System", "System.Core", "System.Data", "System.Xml" })
        {
            sb.AppendLine($"""    <Reference Include="{assembly}" />""");
        }
        foreach (var pkg in p.Packages)
        {
            sb.AppendLine($"""    <Reference Include="{pkg.AssemblyName}">""");
            sb.AppendLine($@"      <HintPath>{packagesPrefix}{pkg.Id}.{pkg.Version}\lib\net45\{pkg.AssemblyName}.dll</HintPath>");
            sb.AppendLine("    </Reference>");
        }
        sb.AppendLine("  </ItemGroup>");

        sb.AppendLine("  <ItemGroup>");
        foreach (var file in sourceFiles)
        {
            sb.AppendLine($"""    <Compile Include="{file.Replace('/', '\\')}" />""");
        }
        sb.AppendLine("  </ItemGroup>");

        if (hasPackagesConfig || p.Layer == Layer.Api)
        {
            sb.AppendLine("  <ItemGroup>");
            if (hasPackagesConfig) sb.AppendLine("""    <None Include="packages.config" />""");
            if (p.Layer == Layer.Api)
            {
                sb.AppendLine("""    <Content Include="web.config" />""");
                sb.AppendLine("""    <None Include="appsettings.json" />""");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        if (p.Refs.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var r in p.Refs)
            {
                sb.AppendLine($"""    <ProjectReference Include="{RelativePath(p.RelDir, r.CsprojRelPath)}">""");
                sb.AppendLine($"      <Project>{{{r.ProjectGuid.ToString().ToUpperInvariant()}}}</Project>");
                sb.AppendLine($"      <Name>{r.Name}</Name>");
                sb.AppendLine("    </ProjectReference>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine(@"  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />");
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private string EmitSdkCsproj(ProjectSpec p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<Project Sdk="Microsoft.NET.Sdk">""");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net472</TargetFramework>");
        sb.AppendLine("    <LangVersion>7.3</LangVersion>");
        sb.AppendLine("  </PropertyGroup>");

        if (p.Packages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var pkg in p.Packages)
            {
                sb.AppendLine($"""    <PackageReference Include="{pkg.Id}" Version="{pkg.Version}" />""");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        if (p.Refs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            foreach (var r in p.Refs)
            {
                sb.AppendLine($"""    <ProjectReference Include="{RelativePath(p.RelDir, r.CsprojRelPath)}" />""");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine();
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private string EmitPackagesConfig(ProjectSpec p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("<packages>");
        foreach (var pkg in p.Packages)
        {
            sb.AppendLine($"""  <package id="{pkg.Id}" version="{pkg.Version}" targetFramework="net472" />""");
        }
        sb.AppendLine("</packages>");
        return sb.ToString();
    }

    private string EmitWebConfig(ProjectSpec p) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <appSettings>
            <add key="{p.Product}:{p.Subsystem}:BaseUrl" value="https://{p.Subsystem.ToLowerInvariant()}.{p.Product.ToLowerInvariant()}.acme.internal" />
            <add key="{p.Product}:{p.Subsystem}:TimeoutSeconds" value="30" />
          </appSettings>
          <system.web>
            <compilation targetFramework="4.7.2" />
            <httpRuntime targetFramework="4.7.2" />
          </system.web>
        </configuration>
        """;

    private string EmitAppSettings(ProjectSpec p) =>
        $$"""
        {
          "{{p.Product}}": {
            "{{p.Subsystem}}": {
              "ConnectionStringName": "{{p.Product}}Db",
              "EnableDiagnostics": false,
              "RetryCount": 3
            }
          }
        }
        """;

    // ---------------------------------------------------------------- solutions

    private string EmitSolution(SolutionSpec sln)
    {
        string slnDir = Path.GetDirectoryName(sln.RelPath)?.Replace('\\', '/') ?? "";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        foreach (var p in sln.Projects)
        {
            string rel = RelativePath(slnDir, p.CsprojRelPath);
            string guid = "{" + p.ProjectGuid.ToString().ToUpperInvariant() + "}";
            sb.AppendLine($"Project(\"{CsprojTypeGuid}\") = \"{p.Name}\", \"{rel}\", \"{guid}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var p in sln.Projects)
        {
            string guid = "{" + p.ProjectGuid.ToString().ToUpperInvariant() + "}";
            sb.AppendLine($"\t\t{guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{guid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{guid}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{guid}.Release|Any CPU.Build.0 = Release|Any CPU");
        }
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        sb.AppendLine("\t\tHideSolutionNode = FALSE");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");
        return sb.ToString();
    }

    private void EmitSolutionFilter()
    {
        // A realistic .slnf: the Billing slice of the enterprise solution.
        var billing = _ws.Projects
            .Where(p => p.Product == "Billing" || p == _ws.PlatformCommon)
            .Select(p => p.CsprojRelPath.Replace("/", "\\\\"))
            .ToList();
        if (billing.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"solution\": {");
        sb.AppendLine("    \"path\": \"Acme.Enterprise.sln\",");
        sb.AppendLine("    \"projects\": [");
        for (int i = 0; i < billing.Count; i++)
        {
            sb.AppendLine($"      \"{billing[i]}\"{(i < billing.Count - 1 ? "," : "")}");
        }
        sb.AppendLine("    ]");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        WriteFile("Acme.Billing.slnf", sb.ToString());
    }

    private void EmitRootFiles()
    {
        WriteFile("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <Company>Acme Corporation</Company>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <WarningLevel>4</WarningLevel>
              </PropertyGroup>
            </Project>
            """);

        WriteFile("NuGet.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <config>
                <add key="repositoryPath" value="packages" />
              </config>
            </configuration>
            """);

        WriteFile("README.md",
            $"# Acme Enterprise Monorepo (synthetic)\n\n" +
            $"Generated by CodeNav.WorkspaceGen (seed {_ws.Seed}) for code-navigation benchmarks.\n" +
            $"{_ws.Projects.Count} projects, {_ws.Solutions.Count} solutions, net472, mixed legacy/SDK styles.\n");

        WriteFile(".gitignore", "bin/\nobj/\npackages/\n.vs/\n*.user\n");
    }

    // ---------------------------------------------------------------- io helpers

    private void WriteFile(string relPath, string content)
    {
        string full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, content.Length);
        Interlocked.Add(ref _lines, CountLines(content));
    }

    private static long CountLines(string s)
    {
        long n = 0;
        foreach (char c in s)
        {
            if (c == '\n') n++;
        }
        return n;
    }

    /// <summary>Relative path from a workspace-relative dir to a workspace-relative file, backslashed.</summary>
    private static string RelativePath(string fromDir, string toFile)
    {
        var from = fromDir.Length == 0 ? Array.Empty<string>() : fromDir.Split('/');
        var to = toFile.Split('/');
        int common = 0;
        while (common < from.Length && common < to.Length - 1 && from[common] == to[common]) common++;
        var parts = Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common));
        return string.Join("\\", parts);
    }
}
