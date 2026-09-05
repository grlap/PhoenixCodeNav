using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>Returns the host-canonical path of an existing physical workspace root.</summary>
    /// <remarks>The workspace itself must exist, but no child or index destination is opened.</remarks>
    public static string GetCanonicalPath(string workspaceRoot) =>
        GetCanonicalExistingPath(workspaceRoot);

    /// <summary>
    /// Returns the host-canonical path of a destination that may not exist yet. The nearest
    /// existing ancestor is resolved physically and the missing suffix is appended lexically,
    /// so first-start identity does not require or create the destination's parent directory.
    /// </summary>
    public static string GetCanonicalDestinationPath(string path)
    {
        string candidate = WorkspacePaths.NormalizeFullForComparison(path);
        var missingSegments = new Stack<string>();
        while (!Path.Exists(candidate))
        {
            string? parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrWhiteSpace(parent) ||
                WorkspacePaths.FileSystemPathComparer.Equals(parent, candidate))
            {
                throw new IOException("destination path could not be canonicalized");
            }

            string segment = Path.GetFileName(candidate);
            if (string.IsNullOrEmpty(segment))
                throw new IOException("destination path could not be canonicalized");
            missingSegments.Push(segment);
            candidate = parent;
        }

        string canonical = GetCanonicalExistingPath(candidate);
        while (missingSegments.TryPop(out string? segment))
            canonical = Path.Combine(canonical, segment);
        return WorkspacePaths.NormalizeFullForComparison(canonical);
    }

    private static string GetCanonicalExistingPath(string path)
    {
        string root = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = CreateFileW(
                root,
                desiredAccess: 0,
                shareMode: 0x00000001 | 0x00000002 | 0x00000004,
                IntPtr.Zero,
                creationDisposition: 3,
                flagsAndAttributes: 0x02000000,
                IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException("workspace root could not be canonicalized");

            var buffer = new StringBuilder(512);
            while (true)
            {
                uint length = GetFinalPathNameByHandleW(
                    handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0)
                    throw new IOException("workspace root could not be canonicalized");
                if (length < buffer.Capacity)
                {
                    string value = buffer.ToString();
                    if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                        value = @"\\" + value[8..];
                    else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                        value = value[4..];
                    return WorkspacePaths.NormalizeFullForComparison(value);
                }

                buffer.EnsureCapacity(checked((int)length + 1));
                buffer.Clear();
            }
        }

        IntPtr resolved = realpath(root, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw new IOException("workspace root could not be canonicalized");
        try
        {
            string? value = Marshal.PtrToStringUTF8(resolved);
            if (string.IsNullOrWhiteSpace(value))
                throw new IOException("workspace root could not be canonicalized");
            return WorkspacePaths.NormalizeFullForComparison(value);
        }
        finally
        {
            free(resolved);
        }
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolvedPath);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);
}
