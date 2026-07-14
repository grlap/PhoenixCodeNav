using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeNav.Tests;

/// <summary>
/// Batch 53 (xqxw): cross-project MetadataReference cache. Field telemetry showed
/// scanLoad.projectLoadMs = 12.3s cold, part of it the SAME vendor dll being re-mapped,
/// re-validated, and re-bound for EVERY project in the scan set. Pins:
/// (1) identity — one dll path yields ONE PortableExecutableReference instance across
///     projects (Roslyn then shares the lazily-decoded assembly symbols);
/// (2) invalidation — a rebuilt dll (mtime/size change) yields a FRESH instance;
/// (3) the skip semantics for unreadable paths are unchanged (null, no throw).
/// </summary>
public class Batch53MetadataRefCacheTests
{
    [Fact]
    public void CacheReturnsSameInstanceUntilTheDllChanges()
    {
        string root = Directory.CreateTempSubdirectory("codenav-53-unit").FullName;
        try
        {
            string dll = Path.Combine(root, "Vendor.dll");
            EmitAssembly(dll, "VendorLib", "namespace V { public class A { } }");
            using var ws = new SemanticWorkspace(root, Path.Combine(root, "index.db"));

            var first = ws.GetOrCreateMetadataRef(dll);
            var second = ws.GetOrCreateMetadataRef(dll);
            Assert.NotNull(first);
            Assert.Same(first, second); // one parse, shared instance

            // A rebuilt dll must invalidate: same path, new content/mtime → fresh instance.
            EmitAssembly(dll, "VendorLib", "namespace V { public class A { public int X; } }");
            File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddHours(1));
            var third = ws.GetOrCreateMetadataRef(dll);
            Assert.NotNull(third);
            Assert.NotSame(first, third);

            Assert.Null(ws.GetOrCreateMetadataRef(Path.Combine(root, "missing.dll")));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task TwoProjectsReferencingOneDllShareOneMetadataReference()
    {
        string root = Directory.CreateTempSubdirectory("codenav-53-share").FullName;
        try
        {
            string common = Path.Combine(root, "Common");
            Directory.CreateDirectory(common);
            EmitAssembly(Path.Combine(common, "Shared.Contracts.dll"), "Shared.Contracts",
                """
                [assembly: System.Reflection.AssemblyVersion("2.1.0.0")]
                namespace Shared { public interface IThing { void Run(); } }
                """);

            foreach (string proj in new[] { "P1", "P2" })
            {
                string dir = Path.Combine(root, proj);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{proj}.csproj"),
                    $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net472</TargetFramework></PropertyGroup>
                      <ItemGroup>
                        <Reference Include="Shared.Contracts">
                          <HintPath>..\Common\Shared.Contracts.dll</HintPath>
                        </Reference>
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(dir, "Impl.cs"),
                    $"namespace {proj} {{ public class Impl : Shared.IThing {{ public void Run() {{ }} }} }}");
            }

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var ws = new SemanticWorkspace(root, dbPath);
            var (solution, coverage) = await ws.EnsureLoadedAsync(
                new[] { "P1", "P2" }, CancellationToken.None);
            Assert.Equal(2, coverage.LoadedProjects);

            var perProject = new List<PortableExecutableReference>();
            foreach (var project in solution.Projects)
            {
                var shared = project.MetadataReferences
                    .OfType<PortableExecutableReference>()
                    .SingleOrDefault(r => r.FilePath?.EndsWith(
                        "Shared.Contracts.dll", StringComparison.OrdinalIgnoreCase) == true);
                Assert.NotNull(shared);
                perProject.Add(shared!);
            }
            Assert.Equal(2, perProject.Count);
            // The whole bead: ONE instance across projects, not one per project.
            Assert.Same(perProject[0], perProject[1]);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private static void EmitAssembly(string path, string name, string source)
    {
        var comp = CSharpCompilation.Create(
            name,
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = comp.Emit(path);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics.Take(3)));
    }
}
