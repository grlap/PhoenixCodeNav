using System.Security.Cryptography;
using System.Text;

namespace CodeNav.Core.Telemetry;

/// <summary>
/// Owns: the salted opaque identity derivation of telemetry API v1 (docs/telemetry-api.md,
/// x5ls.1) — workspaceId/indexId as HMAC-SHA256 over canonical local values with the
/// portal-session salt, base64url-encoded. The canonical paths NEVER cross IPC; the portal can
/// group same-workspace/same-index instances only because every producer in one portal session
/// computes the same HMAC from the same salt.
/// Does not own: transport, framing, or what counts as the canonical value (callers pass it).
/// </summary>
internal static class TelemetryIdentity
{
    /// <summary>Canonicalizes a local path for identity purposes: full, trailing-separator
    /// trimmed, and case-folded on Windows (two spellings of one directory must group).</summary>
    public static string CanonicalPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    public static string WorkspaceId(byte[] salt, string canonicalWorkspaceRoot)
        => "wa_" + Hmac(salt, "workspace\0" + canonicalWorkspaceRoot);

    public static string IndexId(byte[] salt, string canonicalDatabaseIdentity)
        => "ix_" + Hmac(salt, "index\0" + canonicalDatabaseIdentity);

    private static string Hmac(byte[] salt, string value)
    {
        byte[] hash = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
