using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.CodeAnalysis;

namespace CodeNav.Tests;

public sealed class SemanticProjectFingerprintTests
{
    [Fact]
    public async Task PackagesConfigOnlyRefreshReloadsOnlyTheAffectedWarmProject()
    {
        string root = Directory.CreateTempSubdirectory("codenav-package-fingerprint").FullName;
        try
        {
            WriteLegacyProject(root, "Package", "Package.Consumer");
            WriteLegacyProject(root, "Other", "Other.Consumer");
            WritePackagesConfig(root, "microsoft.codeanalysis.csharp");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var managerLog = new List<string>();
            using var manager = new IndexManager(root, dbPath, managerLog.Add);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20000));

            var log = new List<string>();
            using var workspace = new SemanticWorkspace(root, dbPath, log.Add);
            var (beforeSolution, beforeCoverage) = await workspace.EnsureLoadedAsync(
                new[] { "Package.Consumer", "Other.Consumer" }, CancellationToken.None);
            Assert.Equal(2, beforeCoverage.LoadedProjects);
            AssertPackageReference(beforeSolution, "Package.Consumer",
                "Microsoft.CodeAnalysis.CSharp.dll", present: true);
            AssertPackageReference(beforeSolution, "Package.Consumer",
                "Microsoft.CodeAnalysis.dll", present: false);

            (long FileCount, long HashSum) affectedBefore;
            (long FileCount, long HashSum) unrelatedBefore;
            using (var queries = manager.OpenQueries())
            {
                affectedBefore = queries.ProjectFingerprint("package.consumer");
                unrelatedBefore = queries.ProjectFingerprint("Other.Consumer");
                Assert.Equal(3, affectedBefore.FileCount);
                Assert.Equal(2, unrelatedBefore.FileCount);
                var batch = queries.ProjectFingerprints(
                    new[] { "PACKAGE.CONSUMER", "other.consumer" });
                Assert.Equal(affectedBefore, batch["Package.Consumer"]);
                Assert.Equal(unrelatedBefore, batch["Other.Consumer"]);
            }

            WritePackagesConfig(root, "microsoft.codeanalysis.common");
            Assert.True(manager.RequestRefresh(new[] { "Package/packages.config" }));
            Assert.True(WaitUntil(() =>
            {
                using var queries = manager.OpenQueries();
                return queries.ContentByPath("Package/packages.config")?.Contains(
                    "microsoft.codeanalysis.common", StringComparison.Ordinal) == true;
            }, 20000), "packages.config-only refresh did not publish the new package content. " +
                string.Join(" | ", managerLog));

            (long FileCount, long HashSum) affectedAfter;
            using (var queries = manager.OpenQueries())
            {
                affectedAfter = queries.ProjectFingerprint("Package.Consumer");
                Assert.True(affectedBefore != affectedAfter,
                    $"packages.config hash was omitted from the semantic fingerprint: " +
                    $"before={affectedBefore}, after={affectedAfter}");
                Assert.Equal(unrelatedBefore, queries.ProjectFingerprint("Other.Consumer"));
                var batch = queries.ProjectFingerprints(
                    new[] { "package.consumer", "OTHER.CONSUMER" });
                Assert.Equal(affectedAfter, batch["Package.Consumer"]);
                Assert.Equal(unrelatedBefore, batch["Other.Consumer"]);
            }

            var (afterSolution, afterCoverage) = await workspace.EnsureLoadedAsync(
                new[] { "Package.Consumer", "Other.Consumer" }, CancellationToken.None);
            Assert.Equal(2, afterCoverage.LoadedProjects);
            AssertPackageReference(afterSolution, "Package.Consumer",
                "Microsoft.CodeAnalysis.CSharp.dll", present: false);
            AssertPackageReference(afterSolution, "Package.Consumer",
                "Microsoft.CodeAnalysis.dll", present: true);
            Assert.Contains(log, message => message.Contains(
                "Semantic reload (files changed): Package.Consumer", StringComparison.Ordinal));
            Assert.DoesNotContain(log, message => message.Contains(
                "Semantic reload (files changed): Other.Consumer", StringComparison.Ordinal));
        }
        finally
        {
            Batch42Support.Cleanup(root);
        }
    }

    private static void AssertPackageReference(Solution solution, string projectName,
        string fileName, bool present)
    {
        bool found = solution.Projects.Single(project => project.Name == projectName)
            .MetadataReferences.OfType<PortableExecutableReference>()
            .Any(reference => string.Equals(Path.GetFileName(reference.FilePath), fileName,
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(present, found);
    }

    private static void WriteLegacyProject(string root, string directory, string assemblyName)
    {
        string projectRoot = Path.Combine(root, directory);
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, $"{directory}.csproj"), $$"""
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <AssemblyName>{{assemblyName}}</AssemblyName>
              </PropertyGroup>
              <ItemGroup><Compile Include="Marker.cs" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectRoot, "Marker.cs"),
            $"namespace {assemblyName.Replace('.', '_')}; public sealed class Marker {{ }}");
    }

    private static void WritePackagesConfig(string root, string packageId)
    {
        File.WriteAllText(Path.Combine(root, "Package", "packages.config"), $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="{{packageId}}" version="5.6.0" targetFramework="net472" />
            </packages>
            """);
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
