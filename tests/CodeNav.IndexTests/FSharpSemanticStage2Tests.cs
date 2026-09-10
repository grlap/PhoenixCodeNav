using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    private static string SdkProject(string targetFrameworks, params string[] sources)
    {
        string frameworkProperty = targetFrameworks.Contains(';')
            ? $"<TargetFrameworks>{targetFrameworks}</TargetFrameworks>"
            : $"<TargetFramework>{targetFrameworks}</TargetFramework>";
        string compileItems = string.Join(Environment.NewLine,
            sources.Select(source => $"<Compile Include=\"{source}\" />"));
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {{frameworkProperty}}
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                {{compileItems}}
              </ItemGroup>
            </Project>
            """;
    }

    private static string SdkProjectWithBody(string targetFrameworks, string body)
    {
        string frameworkProperty = targetFrameworks.Contains(';')
            ? $"<TargetFrameworks>{targetFrameworks}</TargetFrameworks>"
            : $"<TargetFramework>{targetFrameworks}</TargetFramework>";
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {{frameworkProperty}}
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              {{body}}
            </Project>
            """;
    }

    private static string LegacyProjectWithBody(string assemblyName, string body) => $$"""
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <Import Project="$(MSBuildToolsPath)\Microsoft.Common.props" Condition="Exists('$(MSBuildToolsPath)\Microsoft.Common.props')" />
          <PropertyGroup>
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
            <AssemblyName>{{assemblyName}}</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="FSharp.Core">
              <HintPath>..\Lib\FSharp.Core.dll</HintPath>
            </Reference>
          </ItemGroup>
          {{body}}
          <Import Project="$(FSharpTargetsPath)" />
        </Project>
        """;

    private static void WriteProject(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WritePackageAssets(string root, string projectPath,
        string targetFramework, string packageId, string packageVersion, string packagesRoot,
        string compileAsset,
        params (string Id, string Version, string CompileAsset)[] transitivePackages)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", transitivePackages, []);
    }

    private static void WritePackageAssets(string root, string projectPath,
        string targetFramework, string packageId, string packageVersion, string packagesRoot,
        string compileAsset, string requestedMinimumVersion)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{requestedMinimumVersion}, )", [], []);
    }

    private static void WritePackageAssetsForConstraint(string root, string projectPath,
        string targetFramework, string assetsTargetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        string dependencyVersion)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, assetsTargetFramework,
            dependencyVersion, [], []);
    }

    private static void WritePackageAssetsWithRoots(string root, string projectPath,
        string targetFramework, string packageId, string packageVersion,
        IReadOnlyList<string> packagesRoots, string compileAsset)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, packagesRoots, compileAsset, targetFramework,
            $"[{packageVersion}, )", [], []);
    }

    private static void WritePackageAssetsWithUnreachablePackage(string root,
        string projectPath, string targetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        (string Id, string Version, string CompileAsset) unreachablePackage)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", [], [unreachablePackage]);
    }

    private static void WritePackageAssetsWithLibraryPath(string root,
        string projectPath, string targetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        string libraryPath)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", [], [], libraryPath);
    }

    private static void WritePackageAssetsWithExtraDirectPackage(string root,
        string projectPath, string targetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        (string Id, string Version, string CompileAsset) extraDirectPackage)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", [], [extraDirectPackage],
            extraDirectPackages: [(extraDirectPackage.Id,
                $"[{extraDirectPackage.Version}, )", null)]);
    }

    private static void WritePackageAssetsWithAdditionalDirectDependencyEntry(string root,
        string projectPath, string targetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        string additionalDependencyId)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", [], [],
            extraDirectPackages: [(additionalDependencyId,
                $"[{packageVersion}, )", null)]);
    }

    private static void WritePackageAssetsWithDirectDependencyIdentity(string root,
        string projectPath, string targetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        string directDependencyId)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", [], [],
            directDependencyIdOverride: directDependencyId);
    }

    private static void WritePackageAssetsWithAutoReferencedPackage(string root,
        string projectPath, string targetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        (string Id, string Version, string CompileAsset) autoReferencedPackage)
    {
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", [], [autoReferencedPackage],
            extraDirectPackages: [(autoReferencedPackage.Id,
                $"[{autoReferencedPackage.Version}, )", "true")]);
    }

    private static void WritePackageAssetsWithRawAutoReferencedPackage(string root,
        string projectPath, string targetFramework, string packageId,
        string packageVersion, string packagesRoot, string compileAsset,
        string autoReferencedPackageId, string? autoReferencedDependencyVersion,
        string autoReferencedJson, bool includeSelectedTarget,
        string selectedVersion, string selectedCompileAsset)
    {
        IReadOnlyList<(string Id, string Version, string CompileAsset)> selectedPackages =
            includeSelectedTarget
                ? [(autoReferencedPackageId, selectedVersion, selectedCompileAsset)]
                : [];
        WritePackageAssetsCore(root, projectPath, targetFramework, packageId,
            packageVersion, [packagesRoot], compileAsset, targetFramework,
            $"[{packageVersion}, )", [], selectedPackages,
            extraDirectPackages: [(autoReferencedPackageId,
                autoReferencedDependencyVersion, autoReferencedJson)]);
    }

    private static void WritePackageAssetsCore(string root, string projectPath,
        string targetFramework, string packageId, string packageVersion,
        IReadOnlyList<string> packagesRoots,
        string compileAsset, string assetsTargetFramework, string dependencyVersion,
        IReadOnlyList<(string Id, string Version, string CompileAsset)> transitivePackages,
        IReadOnlyList<(string Id, string Version, string CompileAsset)> unreachablePackages,
        string? directLibraryPathOverride = null,
        IReadOnlyList<(string Id, string? Version, string? AutoReferencedJson)>?
            extraDirectPackages = null,
        string? directDependencyIdOverride = null)
    {
        string absoluteProjectPath = Path.Combine(root,
            projectPath.Replace('/', Path.DirectorySeparatorChar));
        string[] normalizedPackagesRoots = packagesRoots.Select(packagesRoot =>
                Path.TrimEndingDirectorySeparator(packagesRoot) + Path.DirectorySeparatorChar)
            .ToArray();
        string libraryKey = $"{packageId}/{packageVersion}";
        string libraryPath = directLibraryPathOverride ??
                             $"{packageId.ToLowerInvariant()}/{packageVersion}";
        string directDependencies = string.Join("," + Environment.NewLine,
            transitivePackages.Select(package =>
                $"\"{package.Id}\": \"{package.Version}\""));
        var additionalPackages = transitivePackages.Concat(unreachablePackages).ToArray();
        string transitiveTargets = string.Join("," + Environment.NewLine,
            additionalPackages.Select(package => $$"""
                  "{{package.Id}}/{{package.Version}}": {
                    "type": "package",
                    "compile": { "{{package.CompileAsset}}": {} }
                  }
                """));
        string transitiveLibraries = string.Join("," + Environment.NewLine,
            additionalPackages.Select(package => $$"""
                "{{package.Id}}/{{package.Version}}": {
                  "type": "package",
                  "path": "{{package.Id.ToLowerInvariant()}}/{{package.Version}}"
                }
                """));
        string packageFolders = string.Join("," + Environment.NewLine,
            normalizedPackagesRoots.Select(packageRoot =>
                $"{JsonSerializer.Serialize(packageRoot)}: {{}}"));
        string projectDependencies = string.Join("," + Environment.NewLine,
            new[] { (Id: directDependencyIdOverride ?? packageId,
                    Version: (string?)dependencyVersion,
                    AutoReferencedJson: (string?)null) }
                .Concat(extraDirectPackages ?? [])
                .Select(package =>
                {
                    string version = package.Version is not null
                        ? "," + Environment.NewLine +
                          $"        \"version\": {JsonSerializer.Serialize(package.Version)}"
                        : "";
                    string autoReferenced = package.AutoReferencedJson is not null
                        ? "," + Environment.NewLine +
                          $"        \"autoReferenced\": {package.AutoReferencedJson}"
                        : "";
                    return $$"""
                          "{{package.Id}}": {
                            "target": "Package"{{version}}{{autoReferenced}}
                          }
                        """;
                }));
        string json = $$"""
            {
              "version": 3,
              "targets": {
                "{{assetsTargetFramework}}": {
                  "{{libraryKey}}": {
                    "type": "package",
                    "dependencies": { {{directDependencies}} },
                    "compile": { "{{compileAsset}}": {} }
                  }{{(transitiveTargets.Length == 0 ? "" : "," + Environment.NewLine + transitiveTargets)}}
                }
              },
              "libraries": {
                "{{libraryKey}}": {
                  "type": "package",
                  "path": "{{libraryPath}}"
                }{{(transitiveLibraries.Length == 0 ? "" : "," + Environment.NewLine + transitiveLibraries)}}
              },
              "projectFileDependencyGroups": {
                "{{targetFramework}}": [ "{{packageId}} >= {{packageVersion}}" ]
              },
              "packageFolders": {
                {{packageFolders}}
              },
              "project": {
                "restore": {
                  "projectPath": {{JsonSerializer.Serialize(absoluteProjectPath)}},
                  "packagesPath": {{JsonSerializer.Serialize(normalizedPackagesRoots[0])}},
                  "originalTargetFrameworks": [ "{{targetFramework}}" ],
                  "frameworks": {
                    "{{targetFramework}}": {
                      "targetAlias": "{{targetFramework}}",
                      "projectReferences": {}
                    }
                  }
                },
                "frameworks": {
                  "{{targetFramework}}": {
                    "dependencies": {
                      {{projectDependencies}}
                    }
                  }
                }
              }
            }
            """;
        string assetsRelativePath = Path.Combine(
                Path.GetDirectoryName(projectPath) ?? "", "obj", "project.assets.json")
            .Replace(Path.DirectorySeparatorChar, '/');
        WriteProject(root, assetsRelativePath, json);

        // A restored assets file is newer than every input used to produce it. File writes can
        // otherwise receive the same timestamp under suite load, which correctly trips the
        // product's stale-assets guard but does not represent the fixture being modeled here.
        string assetsPath = Path.Combine(root,
            assetsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        DateTime restoredAt = new[]
        {
            DateTime.UtcNow,
            File.GetLastWriteTimeUtc(absoluteProjectPath),
        }.Max().AddSeconds(1);
        File.SetLastWriteTimeUtc(assetsPath, restoredAt);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string CallSemantic(Func<string> call, int attempts = 3)
    {
        string last = "";
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(250);
            last = call();
            JsonElement response = Parse(last);
            if (!response.TryGetProperty("error", out JsonElement error) ||
                error.GetString() != "index_snapshot_unavailable")
                return last;
        }
        Assert.Fail($"F# semantic snapshot remained transiently unavailable: {last}");
        return last;
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction);
        start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null || !process.WaitForExit(5_000) || process.ExitCode != 0)
            return false;
        return Directory.Exists(junction) &&
               (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0;
    }

    private static void RemoveJunction(string junction)
    {
        try
        {
            if (Directory.Exists(junction) &&
                (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(junction);
        }
        catch { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IndexManager _manager;
        private readonly SemanticService _semantic;

        private Fixture(IndexManager manager, SemanticService semantic)
        {
            _manager = manager;
            _semantic = semantic;
            Tools = new NavigationTools(manager, semantic);
        }

        public NavigationTools Tools { get; }
        public SemanticService Semantic => _semantic;
        public IndexManager Manager => _manager;

        public static Fixture Create(string root, FSharpProjectModel? projectModel = null)
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            return Start(root, dbPath, projectModel);
        }

        public static Fixture Start(string root, string dbPath, FSharpProjectModel? projectModel = null)
        {
            var manager = new IndexManager(root, dbPath);
            manager.Start();
            // Every fixture operation opens a protected semantic snapshot. Queryability precedes
            // completion of the mandatory startup sweep, so do not expose tests to the transient
            // index_snapshot_unavailable window.
            Assert.True(WaitUntil(() => manager.State == "ready", 30_000),
                manager.Health().Error);
            var semantic = new SemanticService(manager, enableRoslynPersistence: false, fsharpProjectModel: projectModel);
            return new Fixture(manager, semantic);
        }

        public void Dispose()
        {
            _semantic.Dispose();
            _manager.Dispose();
        }
    }

    private sealed class ReportedLengthStream(long reportedLength, byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => reportedLength;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, content.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(content, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }
}
