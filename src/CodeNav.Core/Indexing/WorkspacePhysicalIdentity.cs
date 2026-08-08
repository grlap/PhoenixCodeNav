namespace CodeNav.Core.Indexing;

/// <summary>
/// Exposes the physical directory identity already used by the cross-process index-writer lease.
/// Transport discovery must use this identity rather than a lexical workspace path so aliases of
/// one worktree converge on the same daemon without duplicating the platform identity logic.
/// </summary>
public static class WorkspacePhysicalIdentity
{
    /// <summary>Returns the stable host-local physical identity of an existing workspace root.</summary>
    /// <exception cref="DirectoryNotFoundException">The workspace root does not exist.</exception>
    /// <exception cref="IOException">The root is not a directory or its identity cannot be proven.</exception>
    public static string Get(string workspaceRoot) =>
        IndexOwnershipLease.GetWorkspaceIdentity(workspaceRoot);

    /// <summary>Attempts to identify an existing workspace without throwing across a host boundary.</summary>
    public static bool TryGet(string workspaceRoot, out string identity)
    {
        identity = "";
        if (IndexOwnershipLease.ProbeWorkspaceIdentity(workspaceRoot, out string? candidate) !=
                WorkspaceIdentityProbeResult.Found ||
            string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        identity = candidate;
        return true;
    }
}
