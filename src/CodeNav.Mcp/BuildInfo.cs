using System.Reflection;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: the server's own build identity — semantic version, the git commit phoenix was built from,
/// and the index schema version. This is the deploy-verification signal a caller needs to answer
/// "WHICH phoenix build is running, and does it have feature X?" — something the silent/additive
/// per-response fields (orphaned, omittedImplementers) cannot provide on their own.
/// Does not own: the indexed WORKSPACE's git state (that is Health.IndexedCommit / repo_overview.git),
/// nor the index schema-migration logic (that lives on IndexBuilder.SchemaVersion).
/// Split out of: NavigationTools.ServerCapabilities, which previously hardcoded version "0.1.0".
/// The commit comes from the .NET SDK's source-control integration, which appends "+&lt;sha&gt;" to
/// AssemblyInformationalVersion at build time — preferred over a custom git shell-out, which has
/// stderr-capture and literal-quoting pitfalls in git-less builds.
/// </summary>
public static class BuildInfo
{
    /// <summary>Bump when the tool surface or a user-visible capability changes. Pair with the
    /// features manifest in server_capabilities so a caller can confirm capabilities, not just a number.</summary>
    public const string Version = "0.7.1";

    /// <summary>"version+shortsha" — the inline deploy check every result's meta carries (ddp),
    /// so a caller never has to guess which build produced a result. Expression-bodied ON PURPOSE:
    /// an initialized property here ran BEFORE Commit's initializer (static init is textual order)
    /// and shipped "0.5.0+" with the commit silently dropped (review-caught in the built DLL).</summary>
    public static string Stamp => $"{Version}+{Commit}";

    /// <summary>Short git commit phoenix was built from ("unknown" when the build could not read git —
    /// e.g. source copied without .git, or no git in the build image).</summary>
    public static string Commit { get; } = ParseCommit(
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The on-disk index schema version this build expects (drives rebuild-on-mismatch).</summary>
    public static string IndexSchema => IndexBuilder.SchemaVersion;

    /// <summary>Extracts the git sha the SDK appends to AssemblyInformationalVersion as "&lt;version&gt;+&lt;sha&gt;",
    /// shortened for display. Returns "unknown" when the suffix is absent (a git-less build) or empty —
    /// never a partial/garbage value.</summary>
    internal static string ParseCommit(string? informationalVersion)
    {
        if (informationalVersion is null) return "unknown";
        int plus = informationalVersion.IndexOf('+');
        if (plus < 0 || plus + 1 >= informationalVersion.Length) return "unknown";
        string sha = informationalVersion[(plus + 1)..];
        return sha.Length > 12 ? sha[..12] : sha;
    }
}
