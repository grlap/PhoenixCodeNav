using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CodeNav.Core.Semantic;

/// <summary>
/// Owns: locating .NET Framework reference assemblies for semantic compilation and
/// resolving NuGet package assemblies from hint paths or the global package cache.
/// Does not own: project/cluster loading (SemanticWorkspace).
/// </summary>
public static class ReferenceAssemblyLocator
{
    private static readonly object Gate = new();
    private static IReadOnlyList<MetadataReference>? _net472Cache;
    private static string? _net472Dir;
    private static readonly Dictionary<string, (IReadOnlyList<string> Paths, string? Directory)>
        FrameworkPathCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Framework reference assemblies for net472 (+ facades). Cached process-wide.</summary>
    public static IReadOnlyList<MetadataReference> Net472References(out string? sourceDir)
    {
        lock (Gate)
        {
            if (_net472Cache is not null)
            {
                sourceDir = _net472Dir;
                return _net472Cache;
            }

            string? dir = ProbeNet472Dir();
            var refs = new List<MetadataReference>();
            if (dir is not null)
            {
                foreach (var dll in EnumerateRefDlls(dir))
                {
                    try { refs.Add(MetadataReference.CreateFromFile(dll)); }
                    catch (Exception) { /* skip unreadable assembly */ }
                }
            }
            _net472Cache = refs;
            _net472Dir = dir;
            sourceDir = dir;
            return refs;
        }
    }

    private static IEnumerable<string> EnumerateRefDlls(string dir)
    {
        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
        {
            yield return dll;
        }
        string facades = Path.Combine(dir, "Facades");
        if (Directory.Exists(facades))
        {
            foreach (var dll in Directory.EnumerateFiles(facades, "*.dll"))
            {
                yield return dll;
            }
        }
    }

    private static string? ProbeNet472Dir()
    {
        // 1. Explicit override.
        if (Environment.GetEnvironmentVariable("CODENAV_NET472_REFS") is { Length: > 0 } env && Directory.Exists(env))
        {
            return env;
        }

        // 2. Installed targeting pack (present with VS / Build Tools).
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrEmpty(programFiles)) continue;
            string dir = Path.Combine(programFiles, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.7.2");
            if (Directory.Exists(dir)) return dir;
        }

        // 3. NuGet cache (auto-restored by SDK builds targeting net472 without a pack).
        string nuget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "microsoft.netframework.referenceassemblies.net472");
        if (Directory.Exists(nuget))
        {
            var candidate = Directory.EnumerateDirectories(nuget)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "build", ".NETFramework", "v4.7.2"))
                .FirstOrDefault(Directory.Exists);
            if (candidate is not null) return candidate;
        }

        // 4. Runtime assemblies (navigation-grade fallback; no facades).
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var fw in new[] { "Framework64", "Framework" })
        {
            string dir = Path.Combine(windir, "Microsoft.NET", fw, "v4.0.30319");
            if (File.Exists(Path.Combine(dir, "mscorlib.dll"))) return dir;
        }
        return null;
    }

    /// <summary>Resolves a NuGet package assembly for net472-ish targets from the global cache.</summary>
    public static string? ResolvePackageDll(string packageId, string version)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", packageId.ToLowerInvariant());
        if (!Directory.Exists(root)) return null;

        string? versionDir = Directory.Exists(Path.Combine(root, version))
            ? Path.Combine(root, version)
            : Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (versionDir is null) return null;

        string lib = Path.Combine(versionDir, "lib");
        if (!Directory.Exists(lib)) return null;

        foreach (var tfm in new[] { "net472", "net471", "net47", "net462", "net461", "net46", "net45", "netstandard2.0", "net40", "net35", "net20" })
        {
            string dir = Path.Combine(lib, tfm);
            if (!Directory.Exists(dir)) continue;
            var dll = Directory.EnumerateFiles(dir, "*.dll").FirstOrDefault();
            if (dll is not null) return dll;
        }
        return null;
    }

    /// <summary>Exact compiler reference paths for the bounded F# semantic targets supported by
    /// Stage 2A. Unlike Roslyn MetadataReference objects, FCS consumes command-line paths.</summary>
    public static IReadOnlyList<string> FrameworkReferencePaths(string targetFramework,
        out string? sourceDir)
    {
        lock (Gate)
        {
            if (FrameworkPathCache.TryGetValue(targetFramework, out var cached))
            {
                sourceDir = cached.Directory;
                return cached.Paths;
            }

            string? dir = targetFramework.Equals("net472", StringComparison.OrdinalIgnoreCase)
                ? ProbeStrictNet472Dir()
                : ProbeNetCoreReferenceDir(targetFramework);
            IReadOnlyList<string> paths = dir is null
                ? []
                : EnumerateRefDlls(dir)
                    .Where(IsManagedAssemblyPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            FrameworkPathCache[targetFramework] = (paths, dir);
            sourceDir = dir;
            return paths;
        }
    }

    /// <summary>Resolve the target-compatible asset for the same pinned FSharp.Core package loaded
    /// by CodeNav.FSharp. ProductVersion carries the package version even when the assembly has been
    /// copied to the application output directory.</summary>
    public static string? FSharpCoreReferencePath(string targetFramework, out bool targetAssetExact)
    {
        targetAssetExact = false;
        string runtimePath = Path.Combine(AppContext.BaseDirectory, "FSharp.Core.dll");
        if (!File.Exists(runtimePath)) return null;

        string? productVersion = FileVersionInfo.GetVersionInfo(runtimePath).ProductVersion;
        string packageVersion = (productVersion ?? "")
            .Split(['-', '+'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        string targetAsset = targetFramework.Equals("net472", StringComparison.OrdinalIgnoreCase)
            ? "netstandard2.0"
            : "netstandard2.1";
        if (packageVersion.Length > 0)
        {
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            string candidate = Path.Combine(packagesRoot, "fsharp.core", packageVersion,
                "lib", targetAsset, "FSharp.Core.dll");
            if (File.Exists(candidate))
            {
                targetAssetExact = true;
                return candidate;
            }
        }

        return runtimePath;
    }

    private static string? ProbeNetCoreReferenceDir(string targetFramework)
    {
        if (targetFramework is not ("net8.0" or "net9.0" or "net10.0")) return null;
        int major = targetFramework switch
        {
            "net8.0" => 8,
            "net9.0" => 9,
            _ => 10,
        };
        foreach (string root in DotNetRoots())
        {
            string pack = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(pack)) continue;
            foreach (var versionDir in Directory.EnumerateDirectories(pack)
                         .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
                         .Where(item => item.Version?.Major == major)
                         .OrderByDescending(item => item.Version))
            {
                string candidate = Path.Combine(versionDir.Path, "ref", targetFramework);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? ProbeStrictNet472Dir()
    {
        if (Environment.GetEnvironmentVariable("CODENAV_NET472_REFS") is { Length: > 0 } env &&
            Directory.Exists(env))
            return env;

        foreach (string programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrEmpty(programFiles)) continue;
            string candidate = Path.Combine(programFiles, "Reference Assemblies", "Microsoft",
                "Framework", ".NETFramework", "v4.7.2");
            if (Directory.Exists(candidate)) return candidate;
        }

        string packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "microsoft.netframework.referenceassemblies.net472");
        if (!Directory.Exists(packageRoot)) return null;
        return Directory.EnumerateDirectories(packageRoot)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, "build", ".NETFramework", "v4.7.2"))
            .FirstOrDefault(Directory.Exists);
    }

    private static Version? ParseVersion(string value) =>
        Version.TryParse(value.Split('-', 2)[0], out Version? version) ? version : null;

    internal static bool IsManagedAssemblyPath(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> DotNetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in new[]
                 {
                     Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                     Path.GetDirectoryName(Environment.ProcessPath),
                 })
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root) && seen.Add(root))
                yield return root;
        }
    }
}
