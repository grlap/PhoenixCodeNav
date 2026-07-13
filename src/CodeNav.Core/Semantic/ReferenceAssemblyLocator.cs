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
    private static IReadOnlyList<MetadataReference>? _runtimeCache;
    private static readonly Dictionary<string, (IReadOnlyList<MetadataReference> References,
        string Directory)> TargetFrameworkCaches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Best available target-framework reference set without invoking MSBuild. Classic
    /// .NET Framework variants use the targeting pack; modern variants use the current runtime's
    /// trusted platform assemblies as an explicitly navigation-grade fallback.</summary>
    public static IReadOnlyList<MetadataReference> ReferencesForTargetFramework(
        string targetFramework, out string? sourceDir)
    {
        if (targetFramework.StartsWith("net4", StringComparison.OrdinalIgnoreCase))
            return Net472References(out sourceDir);
        lock (Gate)
        {
            if (TargetFrameworkCaches.TryGetValue(targetFramework, out var cached))
            {
                sourceDir = cached.Directory;
                return cached.References;
            }
            string? targetingDirectory = ProbeTargetingPack(targetFramework);
            if (targetingDirectory is not null)
            {
                IReadOnlyList<MetadataReference> references = EnumerateRefDlls(targetingDirectory)
                    .Select(path =>
                    {
                        try { return MetadataReference.CreateFromFile(path); }
                        catch (Exception) { return null; }
                    })
                    .Where(reference => reference is not null).Cast<MetadataReference>().ToList();
                TargetFrameworkCaches[targetFramework] = (references, targetingDirectory);
                sourceDir = targetingDirectory;
                return references;
            }
            if (_runtimeCache is null)
            {
                var references = new List<MetadataReference>();
                string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
                foreach (string path in (trusted ?? "").Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    try { references.Add(MetadataReference.CreateFromFile(path)); }
                    catch (Exception) { /* skip unreadable runtime assembly */ }
                }
                _runtimeCache = references;
            }
            // Runtime implementation assemblies are a navigation-grade fallback, not the requested
            // target framework's reference contract. Keep the references useful but report the
            // missing source directory so coverage does not claim full framework fidelity.
            sourceDir = null;
            return _runtimeCache;
        }
    }

    private static string? ProbeTargetingPack(string targetFramework)
    {
        string packName = targetFramework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
            ? "NETStandard.Library.Ref" : "Microsoft.NETCore.App.Ref";
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
            roots.Add(dotnetRoot);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        string runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        DirectoryInfo? cursor = Directory.GetParent(runtimeDirectory);
        while (cursor is not null && !cursor.Name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            cursor = cursor.Parent;
        if (cursor is not null) roots.Add(cursor.FullName);
        foreach (string root in roots)
        {
            string versions = Path.Combine(root, "packs", packName);
            if (!Directory.Exists(versions)) continue;
            foreach (string version in Directory.EnumerateDirectories(versions)
                         .OrderByDescending(path => ParseVersion(Path.GetFileName(path))))
            {
                string candidate = Path.Combine(version, "ref", targetFramework);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value.Split('-')[0], out Version? version) ? version : new Version(0, 0);

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
    public static string? ResolvePackageDll(string packageId, string version) =>
        ResolvePackageDll(packageId, version, "net472");

    public static string? ResolvePackageDll(string packageId, string version, string targetFramework)
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

        foreach (string tfm in PackageFrameworkPreference(targetFramework))
        {
            string dir = Path.Combine(lib, tfm);
            if (!Directory.Exists(dir)) continue;
            var dll = Directory.EnumerateFiles(dir, "*.dll").FirstOrDefault();
            if (dll is not null) return dll;
        }
        return null;
    }

    private static IEnumerable<string> PackageFrameworkPreference(string targetFramework)
    {
        if (!string.IsNullOrWhiteSpace(targetFramework)) yield return targetFramework;
        if (targetFramework.StartsWith("net4", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string tfm in new[] { "net472", "net471", "net47", "net462", "net461", "net46", "net45" })
                if (!string.Equals(tfm, targetFramework, StringComparison.OrdinalIgnoreCase)) yield return tfm;
        }
        else
        {
            System.Text.RegularExpressions.Match modern = System.Text.RegularExpressions.Regex.Match(targetFramework,
                "^net(?<major>[5-9]|[1-9][0-9])(?:\\.(?<minor>[0-9]+))?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int major = modern.Success ? int.Parse(modern.Groups["major"].Value) : 9;
            for (int candidate = major - 1; candidate >= 5; candidate--)
                yield return $"net{candidate}.0";
        }
        yield return "netstandard2.1";
        yield return "netstandard2.0";
        yield return "net40";
        yield return "net35";
        yield return "net20";
    }
}
