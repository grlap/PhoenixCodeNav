using Microsoft.CodeAnalysis;

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
}
