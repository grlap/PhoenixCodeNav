using System.Diagnostics;
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

/// <summary>
/// Cross-process ownership for one writable Phoenix index. Two named mutexes protect both the
/// stable lexical destination and the destination-directory object plus leaf. The pair prevents
/// a directory replacement from splitting ownership while also making directory symlink/junction
/// aliases converge. A dedicated thread owns every mutex for the lease lifetime because a Mutex
/// must be released by its acquiring thread; process death abandons them for the next owner.
/// </summary>
internal sealed class IndexOwnershipLease : IDisposable
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex[] _mutexes;
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _exited = new(false);
    private readonly Thread _ownerThread;
    private int _disposed;

    private IndexOwnershipLease(Mutex[] mutexes, ManualResetEventSlim ready,
        Action<IndexLeaseAcquireResult> publishResult)
    {
        _mutexes = mutexes;
        _ownerThread = new Thread(() => OwnMutexes(ready, publishResult))
        {
            IsBackground = true,
            Name = "PhoenixCodeNav index lease",
        };
    }

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
        _ = ownershipRoot; // retained for source compatibility; identity is the actual db target.
        lease = null;
        string[] names;
        try
        {
            names = BuildMutexNames(dbPath, anchoredIdentity);
        }
        catch
        {
            return IndexLeaseAcquireResult.Failed;
        }

        var mutexes = new Mutex[names.Length];
        try
        {
            for (int i = 0; i < names.Length; i++)
                mutexes[i] = new Mutex(initiallyOwned: false, names[i]);
        }
        catch
        {
            foreach (Mutex? mutex in mutexes) mutex?.Dispose();
            return IndexLeaseAcquireResult.Failed;
        }

        using var ready = new ManualResetEventSlim(false);
        IndexLeaseAcquireResult result = IndexLeaseAcquireResult.Failed;
        var candidate = new IndexOwnershipLease(mutexes, ready, value => result = value);
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
                foreach (Mutex mutex in mutexes) mutex.Dispose();
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

    /// <summary>Probes writable ownership for follower liveness. The probe lease is always
    /// released before returning. Cross-process probe gates serialize probes that share any
    /// ownership identity, so one follower's temporary lease cannot masquerade as a writer to
    /// another follower.</summary>
    internal static IndexLeaseAcquireResult ProbeOwnerDetailed(string ownershipRoot,
        string dbPath, Action<IndexLeaseAcquireResult>? afterAcquisitionForTest = null)
    {
        if (!TryAcquireProbeGates(dbPath, out Mutex[] probeGates,
                out int acquiredGateCount))
            return IndexLeaseAcquireResult.Failed;
        try
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
        finally
        {
            ReleaseProbeGates(probeGates, acquiredGateCount);
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

    private void OwnMutexes(ManualResetEventSlim ready,
        Action<IndexLeaseAcquireResult> publishResult)
    {
        int acquiredCount = 0;
        bool published = false;
        try
        {
            for (; acquiredCount < _mutexes.Length; acquiredCount++)
            {
                bool acquired;
                try { acquired = _mutexes[acquiredCount].WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) break;
            }

            bool allAcquired = acquiredCount == _mutexes.Length;
            publishResult(allAcquired
                ? IndexLeaseAcquireResult.Acquired
                : IndexLeaseAcquireResult.Contended);
            published = true;
            ready.Set();
            if (allAcquired) _release.Wait();
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
            for (int i = acquiredCount - 1; i >= 0; i--)
            {
                try { _mutexes[i].ReleaseMutex(); } catch { }
            }
            foreach (Mutex mutex in _mutexes)
            {
                try { mutex.Dispose(); } catch { }
            }
            try { _exited.Set(); } catch { }
        }
    }

    private static string[] BuildMutexNames(string dbPath,
        IndexLeaseIdentity? anchoredIdentity)
    {
        string database = Path.GetFullPath(dbPath);
        string? directory = Path.GetDirectoryName(database);
        string directoryIdentity;
        string? existingDatabaseIdentity;
        if (anchoredIdentity is null)
        {
            if (directory is null || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("index destination directory does not exist");
            directoryIdentity = GetDirectoryIdentity(directory);
            existingDatabaseIdentity = GetExistingDatabaseIdentity(database);
        }
        else
        {
            directoryIdentity = anchoredIdentity.DirectoryIdentity;
            existingDatabaseIdentity = anchoredIdentity.DatabaseIdentity;
        }

        string lexical = NormalizeForHost(database);
        string leaf = NormalizeForHost(Path.GetFileName(database));
        string objectIdentity = directoryIdentity + "|" + leaf;
        string prefix = OperatingSystem.IsWindows() ? "Global\\" : "";
        var names = new List<string>
        {
            prefix + "PhoenixCodeNav.Index.Path." + Hash(lexical),
            prefix + "PhoenixCodeNav.Index.Object." + Hash(objectIdentity),
        };
        if (existingDatabaseIdentity is not null)
        {
            names.Add(prefix + "PhoenixCodeNav.Index.File." +
                Hash(existingDatabaseIdentity));
        }
        return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static bool TryAcquireProbeGates(string dbPath, out Mutex[] gates,
        out int acquiredCount)
    {
        gates = [];
        acquiredCount = 0;
        string[] names;
        try
        {
            names = BuildMutexNames(dbPath, anchoredIdentity: null)
                .Select(name =>
                {
                    string prefix = name.StartsWith("Global\\", StringComparison.Ordinal)
                        ? "Global\\"
                        : "";
                    return prefix + "PhoenixCodeNav.Index.Probe." + Hash(name);
                })
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            gates = new Mutex[names.Length];
            for (int i = 0; i < names.Length; i++)
                gates[i] = new Mutex(initiallyOwned: false, names[i]);
        }
        catch
        {
            ReleaseProbeGates(gates, acquiredCount);
            gates = [];
            acquiredCount = 0;
            return false;
        }

        long started = Stopwatch.GetTimestamp();
        for (; acquiredCount < gates.Length; acquiredCount++)
        {
            TimeSpan remaining = CoordinationTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                ReleaseProbeGates(gates, acquiredCount);
                gates = [];
                acquiredCount = 0;
                return false;
            }

            bool acquired;
            try { acquired = gates[acquiredCount].WaitOne(remaining); }
            catch (AbandonedMutexException) { acquired = true; }
            catch
            {
                ReleaseProbeGates(gates, acquiredCount);
                gates = [];
                acquiredCount = 0;
                return false;
            }
            if (acquired) continue;
            ReleaseProbeGates(gates, acquiredCount);
            gates = [];
            acquiredCount = 0;
            return false;
        }
        return true;
    }

    private static void ReleaseProbeGates(Mutex[] gates, int acquiredCount)
    {
        for (int i = acquiredCount - 1; i >= 0; i--)
        {
            try { gates[i].ReleaseMutex(); } catch { }
        }
        foreach (Mutex? gate in gates)
        {
            try { gate?.Dispose(); } catch { }
        }
    }

    private static string NormalizeForHost(string value) =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? value.Normalize(NormalizationForm.FormC).ToUpperInvariant()
            : value;

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
            (ulong device, ulong inode, ulong links, bool regular) =
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
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out WinFileInfo info) ||
                (info.FileAttributes & 0x00000010) == 0)
                throw new IOException("could not identify index directory");
            return $"W:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        }

        (ulong device, ulong inode, _, _) = GetUnixIdentity(path, followLinks: true);
        return $"U:{device:X16}:{inode:X16}";
    }

    private static (ulong Device, ulong Inode, ulong Links, bool Regular) GetUnixIdentity(
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
                    if (Marshal.GetLastPInvokeError() == 2) throw new FileNotFoundException();
                    throw new IOException("could not identify filesystem object");
                }
                uint device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                ushort mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
                ushort links = unchecked((ushort)Marshal.ReadInt16(buffer, 6));
                ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
                return (device, inode, links, (mode & 0xF000) == 0x8000);
            }

            int flags = followLinks ? 0 : 0x100; // AT_SYMLINK_NOFOLLOW
            const uint requested = 0x00000001 | 0x00000004 | 0x00000100;
            if (statx(-100, path, flags, requested, buffer) != 0)
            {
                if (Marshal.GetLastPInvokeError() == 2) throw new FileNotFoundException();
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
                (modeLinux & 0xF000) == 0x8000);
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
