using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeNav.Core.Indexing;

internal sealed record IndexLeaseIdentity(
    string DirectoryIdentity, string? DatabaseIdentity);

internal enum IndexLeaseAcquireResult
{
    Acquired,
    Contended,
    Failed,
}

internal enum WorkspaceIdentityProbeResult
{
    Found,
    Missing,
    Failed,
}

/// <summary>
/// Cross-process ownership for one writable Phoenix workspace/worktree index. One named mutex is
/// derived from the physical workspace-directory identity, so path aliases converge without
/// coupling ownership to the replaceable database file. A dedicated thread owns the mutex for the
/// lease lifetime because a Mutex must be released by its acquiring thread; process death abandons
/// it for the next owner.
/// </summary>
internal sealed class IndexOwnershipLease : IDisposable
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _exited = new(false);
    private readonly Thread _ownerThread;
    private int _disposed;

    private IndexOwnershipLease(Mutex mutex, string workspaceIdentity,
        ManualResetEventSlim ready,
        Action<IndexLeaseAcquireResult> publishResult)
    {
        _mutex = mutex;
        WorkspaceIdentity = workspaceIdentity;
        _ownerThread = new Thread(() => OwnMutex(ready, publishResult))
        {
            IsBackground = true,
            Name = "PhoenixCodeNav index lease",
        };
    }

    internal string WorkspaceIdentity { get; }
    internal bool IsActive => Volatile.Read(ref _disposed) == 0 && _ownerThread.IsAlive;

    internal static bool TryAcquire(string ownershipRoot, string dbPath,
        out IndexOwnershipLease? lease)
        => TryAcquire(ownershipRoot, dbPath, anchoredIdentity: null, out lease);

    internal static bool TryAcquire(string ownershipRoot, string dbPath,
        IndexLeaseIdentity? anchoredIdentity, out IndexOwnershipLease? lease)
        => TryAcquireDetailed(ownershipRoot, dbPath, anchoredIdentity, out lease) ==
           IndexLeaseAcquireResult.Acquired;

    /// <summary>Acquires writable ownership while preserving the distinction between a healthy
    /// competing owner and an inability to construct or coordinate the lease. Only
    /// <see cref="IndexLeaseAcquireResult.Contended"/> is safe for IndexManager to interpret as
    /// evidence that another Phoenix can serve as the writer.</summary>
    internal static IndexLeaseAcquireResult TryAcquireDetailed(string ownershipRoot, string dbPath,
        IndexLeaseIdentity? anchoredIdentity, out IndexOwnershipLease? lease)
    {
        _ = dbPath;
        _ = anchoredIdentity;
        lease = null;
        string name;
        string workspaceIdentity;
        try
        {
            workspaceIdentity = GetWorkspaceIdentity(ownershipRoot);
            name = BuildMutexNameFromIdentity(workspaceIdentity);
        }
        catch
        {
            return IndexLeaseAcquireResult.Failed;
        }

        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch
        {
            return IndexLeaseAcquireResult.Failed;
        }

        using var ready = new ManualResetEventSlim(false);
        IndexLeaseAcquireResult result = IndexLeaseAcquireResult.Failed;
        var candidate = new IndexOwnershipLease(
            mutex, workspaceIdentity, ready, value => result = value);
        try
        {
            candidate._ownerThread.Start();
            if (!ready.Wait(CoordinationTimeout))
            {
                candidate._release.Set();
                candidate.CleanupFailedAcquisition();
                return IndexLeaseAcquireResult.Failed;
            }
        }
        catch
        {
            candidate._release.Set();
            if (!candidate._ownerThread.IsAlive)
            {
                mutex.Dispose();
                candidate._release.Dispose();
                candidate._exited.Dispose();
            }
            return IndexLeaseAcquireResult.Failed;
        }

        if (result != IndexLeaseAcquireResult.Acquired)
        {
            candidate.CleanupFailedAcquisition();
            return result;
        }
        lease = candidate;
        return IndexLeaseAcquireResult.Acquired;
    }

    /// <summary>Probes writable ownership without waiting. The transient lease is always released
    /// before returning and never promotes a running follower; this is used only for directional
    /// worktree-seeding checks and tests.</summary>
    internal static IndexLeaseAcquireResult ProbeOwnerDetailed(string ownershipRoot,
        string dbPath, Action<IndexLeaseAcquireResult>? afterAcquisitionForTest = null)
    {
        IndexLeaseAcquireResult result = TryAcquireDetailed(ownershipRoot, dbPath,
            anchoredIdentity: null, out IndexOwnershipLease? probe);
        try
        {
            afterAcquisitionForTest?.Invoke(result);
            return result;
        }
        finally
        {
            probe?.Dispose();
        }
    }

    internal static bool IsHeld(string ownershipRoot, string dbPath)
    {
        if (!TryAcquire(ownershipRoot, dbPath, out IndexOwnershipLease? probe)) return true;
        probe!.Dispose();
        return false;
    }

    internal static bool IsSafeDestination(string dbPath)
    {
        try
        {
            string database = Path.GetFullPath(dbPath);
            string? directory = Path.GetDirectoryName(database);
            if (directory is null || !Directory.Exists(directory)) return false;
            _ = GetExistingDatabaseIdentity(database);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _release.Set(); }
        catch { return; }
        try
        {
            if (_exited.Wait(CoordinationTimeout))
            {
                _release.Dispose();
                _exited.Dispose();
            }
        }
        catch
        {
            // The owner thread releases the kernel mutexes in its finally block. A bounded
            // disposal must never block shutdown indefinitely merely to reclaim wait handles.
        }
    }

    private void CleanupFailedAcquisition()
    {
        try
        {
            if (_exited.Wait(CoordinationTimeout))
            {
                Interlocked.Exchange(ref _disposed, 1);
                _release.Dispose();
                _exited.Dispose();
            }
        }
        catch { }
    }

    private void OwnMutex(ManualResetEventSlim ready,
        Action<IndexLeaseAcquireResult> publishResult)
    {
        bool acquired = false;
        bool published = false;
        try
        {
            try { acquired = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            publishResult(acquired
                ? IndexLeaseAcquireResult.Acquired
                : IndexLeaseAcquireResult.Contended);
            published = true;
            ready.Set();
            if (acquired) _release.Wait();
        }
        catch
        {
            if (!published)
            {
                try { publishResult(IndexLeaseAcquireResult.Failed); } catch { }
                try { ready.Set(); } catch { }
            }
        }
        finally
        {
            if (acquired)
                try { _mutex.ReleaseMutex(); } catch { }
            try { _mutex.Dispose(); } catch { }
            try { _exited.Set(); } catch { }
        }
    }

    private static string BuildMutexNameFromIdentity(string directoryIdentity)
    {
        string prefix = OperatingSystem.IsWindows() ? "Global\\" : "";
        return prefix + "PhoenixCodeNav.WorkspaceWriter." + Hash(directoryIdentity);
    }

    internal static string GetWorkspaceIdentity(string ownershipRoot)
    {
        string root = Path.GetFullPath(ownershipRoot);
        return GetDirectoryIdentity(root);
    }

    internal static WorkspaceIdentityProbeResult ProbeWorkspaceIdentity(
        string ownershipRoot, out string? identity)
    {
        identity = null;
        try
        {
            identity = GetWorkspaceIdentity(ownershipRoot);
            return WorkspaceIdentityProbeResult.Found;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or
                                   FileNotFoundException)
        {
            return WorkspaceIdentityProbeResult.Missing;
        }
        catch
        {
            return WorkspaceIdentityProbeResult.Failed;
        }
    }

    internal static bool SameWorkspaceIdentity(string firstRoot, string secondRoot)
    {
        try
        {
            return string.Equals(GetWorkspaceIdentity(firstRoot),
                GetWorkspaceIdentity(secondRoot), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? GetExistingDatabaseIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = CreateFileW(path, 0x80000000,
                0x00000001 | 0x00000002 | 0x00000004, IntPtr.Zero, 3,
                0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is 2 or 3) return null;
                throw new IOException("could not inspect index database");
            }
            if (!GetFileInformationByHandle(handle, out WinFileInfo info) ||
                (info.FileAttributes & (0x00000400 | 0x00000010)) != 0 ||
                info.NumberOfLinks != 1)
            {
                throw new IOException("index database is linked or not a single regular file");
            }
            return $"W:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        }

        try
        {
            (ulong device, ulong inode, ulong links, bool regular, _) =
                GetUnixIdentity(path, followLinks: false);
            if (!regular || links != 1)
                throw new IOException("index database is linked or not a single regular file");
            return $"U:{device:X16}:{inode:X16}";
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static string GetDirectoryIdentity(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = CreateFileW(path, 0x80000000,
                0x00000001 | 0x00000002 | 0x00000004, IntPtr.Zero, 3,
                0x02000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is 2 or 3)
                    throw new DirectoryNotFoundException("workspace root does not exist");
                throw new IOException("could not identify index directory");
            }
            if (!GetFileInformationByHandle(handle, out WinFileInfo info))
                throw new IOException("could not identify index directory");
            if ((info.FileAttributes & 0x00000010) == 0)
                throw new IOException("workspace root is not a directory");
            return $"W:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        }

        (ulong device, ulong inode, _, _, bool directory) =
            GetUnixIdentity(path, followLinks: true);
        if (!directory)
            throw new IOException("workspace root is not a directory");
        return $"U:{device:X16}:{inode:X16}";
    }

    private static (ulong Device, ulong Inode, ulong Links, bool Regular, bool Directory)
        GetUnixIdentity(
        string path, bool followLinks)
    {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (int offset = 0; offset < 256; offset += sizeof(long))
                Marshal.WriteInt64(buffer, offset, 0);
            if (OperatingSystem.IsMacOS())
            {
                int rc = followLinks ? stat_macos(path, buffer) : lstat_macos(path, buffer);
                if (rc != 0)
                {
                    if (Marshal.GetLastPInvokeError() is 2 or 20)
                        throw new FileNotFoundException();
                    throw new IOException("could not identify filesystem object");
                }
                uint device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                ushort mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
                ushort links = unchecked((ushort)Marshal.ReadInt16(buffer, 6));
                ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
                return (device, inode, links,
                    (mode & 0xF000) == 0x8000,
                    (mode & 0xF000) == 0x4000);
            }

            int flags = followLinks ? 0 : 0x100; // AT_SYMLINK_NOFOLLOW
            const uint requested = 0x00000001 | 0x00000004 | 0x00000100;
            if (statx(-100, path, flags, requested, buffer) != 0)
            {
                if (Marshal.GetLastPInvokeError() is 2 or 20)
                    throw new FileNotFoundException();
                throw new IOException("could not identify filesystem object");
            }
            uint mask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            if ((mask & requested) != requested)
                throw new IOException("filesystem identity is incomplete");
            uint linksLinux = unchecked((uint)Marshal.ReadInt32(buffer, 16));
            ushort modeLinux = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            ulong inodeLinux = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            uint devMajor = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            uint devMinor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            ulong deviceLinux = ((ulong)devMajor << 32) | devMinor;
            return (deviceLinux, inodeLinux, linksLinux,
                (modeLinux & 0xF000) == 0x8000,
                (modeLinux & 0xF000) == 0x4000);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

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
        out WinFileInfo fileInformation);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat_macos(string path, IntPtr info);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat_macos(string path, IntPtr info);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directoryFd, string path, int flags,
        uint mask, IntPtr info);
}
