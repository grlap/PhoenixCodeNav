using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Pins the directory that owns a normal Phoenix index. Windows holds every volume-root-to-leaf
/// directory handle without delete sharing; Linux traverses and creates each component with
/// openat/mkdirat + O_NOFOLLOW and exposes SQLite through the held directory descriptor so WAL
/// sidecars stay in that exact directory. macOS retains its historical absolute SQLite path but
/// performs componentwise link validation and identity rechecks; it is not advertised as a
/// retained destination-authority boundary because Darwin exposes no proc-fd directory alias.
/// </summary>
internal sealed class IndexDirectoryAuthority : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const int LinuxDirectoryFlags = 0x010000 | 0x020000 | 0x080000;

    private readonly List<SafeFileHandle> _handles;
    private readonly SafeFileHandle? _directoryHandle;
    private readonly string _displayDatabasePath;
    private readonly object _reviewCoordinationGate = new();
    private SafeFileHandle? _reviewCoordinationAnchor;
    private string? _reviewCoordinationIdentity;

    private IndexDirectoryAuthority(List<SafeFileHandle> handles,
        SafeFileHandle? directoryHandle, string displayDatabasePath, string databasePath)
    {
        _handles = handles;
        _directoryHandle = directoryHandle;
        _displayDatabasePath = displayDatabasePath;
        DatabasePath = databasePath;
    }

    internal string DatabasePath { get; }

    /// <summary>
    /// Pins the regular coordination leaf without read/write/delete access. The zero-access anchor
    /// prevents path replacement while remaining compatible with both shared-reader and exclusive
    /// rebuild handles. Only the writer may create the leaf; followers must open an existing one.
    /// </summary>
    internal bool TryAnchorReviewCoordinationFile(bool create)
    {
        if (!OperatingSystem.IsWindows()) return true;
        lock (_reviewCoordinationGate)
        {
            return TryAnchorReviewLeaf(ReviewCoordinationPath(), create,
                ref _reviewCoordinationAnchor, ref _reviewCoordinationIdentity);
        }
    }

    internal bool TryGetReviewCoordinationKey(out string? key)
    {
        lock (_reviewCoordinationGate)
        {
            key = _reviewCoordinationIdentity;
            return key is { Length: > 0 };
        }
    }

    private static bool TryAnchorReviewLeaf(string path, bool create,
        ref SafeFileHandle? anchor, ref string? identity)
    {
        if (anchor is { IsInvalid: false, IsClosed: false }) return true;
        SafeFileHandle handle = CreateFileW(path, 0,
            FileShareRead | FileShareWrite, IntPtr.Zero,
            create ? OpenAlways : OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }
        if (!TryValidateWindowsRegularFile(handle, out string? openedIdentity))
        {
            handle.Dispose();
            return false;
        }

        anchor = handle;
        identity = openedIdentity;
        return true;
    }

    /// <summary>Attempts one OS-level reader/exclusive open against the pinned coordination leaf.
    /// Sharing violations are ordinary contention; every other failure is a coordination fault.</summary>
    internal IndexReviewCoordinationAcquireResult TryOpenReviewCoordinationHandle(
        bool exclusive, out SafeFileHandle? handle)
    {
        handle = null;
        if (!OperatingSystem.IsWindows()) return IndexReviewCoordinationAcquireResult.Acquired;
        if (!TryAnchorReviewCoordinationFile(create: false))
            return IndexReviewCoordinationAcquireResult.Failed;

        SafeFileHandle candidate = CreateFileW(ReviewCoordinationPath(), GenericRead,
            exclusive ? 0 : FileShareRead, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);
        if (candidate.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            candidate.Dispose();
            return error is 32 or 33
                ? IndexReviewCoordinationAcquireResult.Contended
                : IndexReviewCoordinationAcquireResult.Failed;
        }
        if (!TryValidateWindowsRegularFile(candidate, out string? identity) ||
            !string.Equals(identity, _reviewCoordinationIdentity, StringComparison.Ordinal))
        {
            candidate.Dispose();
            return IndexReviewCoordinationAcquireResult.Failed;
        }

        handle = candidate;
        return IndexReviewCoordinationAcquireResult.Acquired;
    }

    private string ReviewCoordinationPath() => _displayDatabasePath + ".readers";

    private static bool TryValidateWindowsRegularFile(SafeFileHandle handle, out string? identity)
    {
        identity = null;
        if (!GetFileInformationByHandle(handle, out WinFileInfo info) ||
            (info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
            info.NumberOfLinks != 1)
            return false;
        identity = WindowsIdentity(info);
        return true;
    }

    internal static bool TryOpen(string dbPath, bool createDirectory,
        out IndexDirectoryAuthority? authority)
    {
        authority = null;
        try
        {
            string database = Path.GetFullPath(dbPath);
            string? directory = Path.GetDirectoryName(database);
            if (directory is null) return false;
            if (OperatingSystem.IsWindows())
                return TryOpenWindows(database, directory, createDirectory, out authority);
            if (OperatingSystem.IsLinux())
                return TryOpenLinux(database, directory, createDirectory, out authority);
            return TryOpenMac(database, directory, createDirectory, out authority);
        }
        catch
        {
            authority?.Dispose();
            authority = null;
            return false;
        }
    }

    internal bool TryGetLeaseIdentity(out IndexLeaseIdentity? identity) =>
        TryGetDatabaseStatus(out identity, out _);

    /// <summary>Returns identity and length from the same no-follow inspection. The length is
    /// intentionally zero on macOS: that platform has no retained directory handle suitable for
    /// a race-free path-relative metadata read, so health remains conservative.</summary>
    internal bool TryGetDatabaseStatus(out IndexLeaseIdentity? identity, out long databaseLength)
    {
        identity = null;
        databaseLength = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (_directoryHandle is null ||
                    !GetFileInformationByHandle(_directoryHandle, out WinFileInfo directoryInfo))
                    return false;
                string directoryId = WindowsIdentity(directoryInfo);
                string? databaseId = InspectWindowsDatabase(_displayDatabasePath,
                    out databaseLength);
                ValidateWindowsSidecars(_displayDatabasePath);
                identity = new IndexLeaseIdentity(directoryId, databaseId);
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                if (_directoryHandle is null ||
                    !TryStatxFd(_directoryHandle.DangerousGetHandle().ToInt32(),
                        out UnixIdentity directoryInfo)) return false;
                string? databaseId = InspectLinuxDatabase(
                    _directoryHandle.DangerousGetHandle().ToInt32(),
                    Path.GetFileName(_displayDatabasePath), out databaseLength);
                ValidateLinuxSidecars(_directoryHandle.DangerousGetHandle().ToInt32(),
                    Path.GetFileName(_displayDatabasePath));
                identity = new IndexLeaseIdentity(UnixIdentityString(directoryInfo), databaseId);
                return true;
            }

            UnixIdentity macDirectory = StatMac(Path.GetDirectoryName(_displayDatabasePath)!,
                followLinks: true);
            string? macDatabase = InspectMacDatabase(_displayDatabasePath, out _);
            ValidateMacSidecars(_displayDatabasePath);
            identity = new IndexLeaseIdentity(UnixIdentityString(macDirectory), macDatabase);
            return true;
        }
        catch
        {
            identity = null;
            databaseLength = 0;
            return false;
        }
    }

    private static bool TryOpenWindows(string database, string directory, bool create,
        out IndexDirectoryAuthority? authority)
    {
        authority = null;
        var handles = new List<SafeFileHandle>();
        try
        {
            string volumeRoot = Path.GetPathRoot(directory)!;
            string current = volumeRoot;
            foreach (string component in RelativeSegments(volumeRoot, directory,
                         includeEmpty: true))
            {
                if (component.Length != 0) current = Path.Combine(current, component);
                SafeFileHandle handle = CreateFileW(current, GenericRead,
                    FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid && Marshal.GetLastPInvokeError() is 2 or 3)
                {
                    handle.Dispose();
                    if (!create) return false;
                    Directory.CreateDirectory(current);
                    handle = CreateFileW(current, GenericRead,
                        FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
                        FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
                }
                if (handle.IsInvalid ||
                    !GetFileInformationByHandle(handle, out WinFileInfo info) ||
                    (info.FileAttributes & FileAttributeDirectory) == 0 ||
                    (info.FileAttributes & FileAttributeReparsePoint) != 0)
                {
                    handle.Dispose();
                    return false;
                }
                handles.Add(handle);
            }
            SafeFileHandle final = handles[^1];
            authority = new IndexDirectoryAuthority(handles, final, database, database);
            return true;
        }
        finally
        {
            if (authority is null)
                foreach (SafeFileHandle handle in handles) handle.Dispose();
        }
    }

    private static bool TryOpenLinux(string database, string directory, bool create,
        out IndexDirectoryAuthority? authority)
    {
        authority = null;
        var handles = new List<SafeFileHandle>();
        string root = Path.GetPathRoot(directory) ?? "/";
        int currentFd = open(root, LinuxDirectoryFlags, 0);
        if (currentFd < 0) return false;
        var current = new SafeFileHandle((IntPtr)currentFd, ownsHandle: true);
        handles.Add(current);
        try
        {
            foreach (string component in RelativeSegments(root, directory,
                         includeEmpty: false))
            {
                int next = openat(currentFd, component, LinuxDirectoryFlags, 0);
                if (next < 0 && create && Marshal.GetLastPInvokeError() == 2)
                {
                    if (mkdirat(currentFd, component, Convert.ToUInt32("700", 8)) != 0 &&
                        Marshal.GetLastPInvokeError() != 17) return false;
                    next = openat(currentFd, component, LinuxDirectoryFlags, 0);
                }
                if (next < 0) return false;
                currentFd = next;
                current = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                handles.Add(current);
            }
            string ioPath = $"/proc/{Environment.ProcessId}/fd/{currentFd}/" +
                Path.GetFileName(database);
            authority = new IndexDirectoryAuthority(handles, current, database, ioPath);
            return true;
        }
        finally
        {
            if (authority is null)
                foreach (SafeFileHandle handle in handles) handle.Dispose();
        }
    }

    private static bool TryOpenMac(string database, string directory, bool create,
        out IndexDirectoryAuthority? authority)
    {
        authority = null;
        string root = Path.GetPathRoot(directory) ?? "/";
        string current = root;
        foreach (string component in RelativeSegments(root, directory, includeEmpty: false))
        {
            current = Path.Combine(current, component);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null) return false;
            if (!info.Exists)
            {
                if (!create) return false;
                Directory.CreateDirectory(current);
                info.Refresh();
                if (!info.Exists || info.LinkTarget is not null) return false;
            }
        }
        authority = new IndexDirectoryAuthority(new List<SafeFileHandle>(), null,
            database, database);
        return true;
    }

    private static IEnumerable<string> RelativeSegments(string root, string target,
        bool includeEmpty)
    {
        if (includeEmpty) yield return "";
        string relative = Path.GetRelativePath(root, target);
        if (relative == ".") yield break;
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)) throw new IOException("index path escaped its root");
        foreach (string component in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..") throw new IOException("invalid index directory");
            yield return component;
        }
    }

    private static string? InspectWindowsDatabase(string path, out long length)
    {
        length = 0;
        using SafeFileHandle handle = CreateFileW(path, GenericRead,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3) return null;
            throw new IOException("could not inspect index database");
        }
        if (!GetFileInformationByHandle(handle, out WinFileInfo info) ||
            (info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
            info.NumberOfLinks != 1)
            throw new IOException("index database is linked or not a single regular file");
        length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        return WindowsIdentity(info);
    }

    private static void ValidateWindowsSidecars(string databasePath)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            _ = InspectWindowsDatabase(databasePath + suffix, out _);
    }

    private static string? InspectLinuxDatabase(int directoryFd, string leaf, out long length)
    {
        length = 0;
        if (!TryStatxAt(directoryFd, leaf, out UnixIdentity info))
        {
            if (Marshal.GetLastPInvokeError() == 2) return null;
            throw new IOException("could not inspect index database");
        }
        if (!info.Regular || info.Links != 1)
            throw new IOException("index database is linked or not a single regular file");
        length = info.Size <= long.MaxValue ? (long)info.Size : 0;
        return UnixIdentityString(info);
    }

    private static void ValidateLinuxSidecars(int directoryFd, string databaseLeaf)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            _ = InspectLinuxDatabase(directoryFd, databaseLeaf + suffix, out _);
    }

    private static string? InspectMacDatabase(string path, out long length)
    {
        length = 0;
        try
        {
            UnixIdentity info = StatMac(path, followLinks: false);
            if (!info.Regular || info.Links != 1)
                throw new IOException("index database is linked or not a single regular file");
            return UnixIdentityString(info);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static void ValidateMacSidecars(string databasePath)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            _ = InspectMacDatabase(databasePath + suffix, out _);
    }

    private static UnixIdentity StatMac(string path, bool followLinks)
    {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (int offset = 0; offset < 256; offset += sizeof(long))
                Marshal.WriteInt64(buffer, offset, 0);
            int rc = followLinks ? stat_macos(path, buffer) : lstat_macos(path, buffer);
            if (rc != 0)
            {
                if (Marshal.GetLastPInvokeError() == 2) throw new FileNotFoundException();
                throw new IOException("could not identify index filesystem object");
            }
            uint device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            ushort mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            ushort links = unchecked((ushort)Marshal.ReadInt16(buffer, 6));
            ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            return new UnixIdentity(device, inode, links, (mode & 0xF000) == 0x8000, 0);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryStatxFd(int fd, out UnixIdentity identity) =>
        TryStatx(fd, "", 0x1000, out identity);

    private static bool TryStatxAt(int fd, string leaf, out UnixIdentity identity) =>
        TryStatx(fd, leaf, 0x100, out identity);

    private static bool TryStatx(int fd, string path, int flags, out UnixIdentity identity)
    {
        identity = default;
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (int offset = 0; offset < 256; offset += sizeof(long))
                Marshal.WriteInt64(buffer, offset, 0);
            // STATX_TYPE|MODE|NLINK|INO|SIZE — TYPE (0x1) owns the S_IFMT bits validated below;
            // requesting MODE alone left them formally unguaranteed (review PhoenixCodeNav-0ce1).
            const uint requested = 0x00000001 | 0x00000002 | 0x00000004 | 0x00000100 | 0x00000200;
            if (statx(fd, path, flags, requested, buffer) != 0) return false;
            uint mask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            if ((mask & requested) != requested) return false;
            uint links = unchecked((uint)Marshal.ReadInt32(buffer, 16));
            ushort mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            ulong size = unchecked((ulong)Marshal.ReadInt64(buffer, 40));
            uint major = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            uint minor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            identity = new UnixIdentity(((ulong)major << 32) | minor, inode, links,
                (mode & 0xF000) == 0x8000, size);
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string WindowsIdentity(WinFileInfo info) =>
        $"W:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";

    private static string UnixIdentityString(UnixIdentity info) =>
        $"U:{info.Device:X16}:{info.Inode:X16}";

    public void Dispose()
    {
        lock (_reviewCoordinationGate)
        {
            _reviewCoordinationAnchor?.Dispose();
            _reviewCoordinationAnchor = null;
            _reviewCoordinationIdentity = null;
        }
        for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
    }

    private readonly record struct UnixIdentity(
        ulong Device, ulong Inode, ulong Links, bool Regular, ulong Size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinFileInfo
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,
        out WinFileInfo info);
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int directoryFd, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(int directoryFd, string path, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directoryFd, string path, int flags, uint mask,
        IntPtr info);
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat_macos(string path, IntPtr info);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat_macos(string path, IntPtr info);
}
