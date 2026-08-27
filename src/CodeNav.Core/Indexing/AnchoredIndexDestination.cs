using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeNav.Core.Indexing;

internal enum PublicationArtifactReapFailure
{
    None,
    OwnershipChanged,
    CandidateLimitExceeded,
    ScanBudgetExceeded,
    UnsafeArtifactSet,
}

/// <summary>
/// Pins a worktree workspace and its .codenav directory without following links. Linux reads use
/// the held workspace descriptor through /proc/&lt;pid&gt;/fd; publishes use directory-relative native
/// operations. Windows pins the complete volume-root-to-destination chain without delete sharing
/// and publishes the exact staged file handle relative to the held destination handle.
/// </summary>
internal sealed class AnchoredIndexDestination : IDisposable
{
    internal const int MaxAbandonedPublicationArtifacts = 256;
    internal static readonly TimeSpan AbandonedPublicationScanBudget =
        TimeSpan.FromSeconds(5);
    internal static Action<string>? BeforeStageSidecarReservationForTest { get; set; }
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint FileShareDelete = 4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;

    private readonly List<SafeFileHandle> _handles;
    private readonly SafeFileHandle _workspaceHandle;
    private readonly SafeFileHandle _directoryHandle;
    private readonly string _workspacePath;
    private readonly string _dbPath;
    private SafeFileHandle? _databaseHandle;
    private long _databaseLength;
    private string? _stageName;
    private SafeFileHandle? _stageHandle;
    private WinFileIdentity? _stageWindowsIdentity;
    private StatxIdentity? _stageLinuxIdentity;
    private bool _stageGuardReleased;
    private readonly List<SidecarGuard> _stageSidecarGuards = new();
    private readonly List<SidecarGuard> _publishSidecarGuards = new();
    internal Action<string>? TestOnlyAfterLiveSidecarRemoval;

    private enum InstallAttemptResult
    {
        Installed,
        RetryableContention,
        Failed,
    }

    private enum SidecarReservationResult
    {
        Reserved,
        RetryableContention,
        Failed,
    }

    private AnchoredIndexDestination(List<SafeFileHandle> handles,
        SafeFileHandle workspaceHandle, SafeFileHandle directoryHandle,
        string workspacePath, string dbPath)
    {
        _handles = handles;
        _workspaceHandle = workspaceHandle;
        _directoryHandle = directoryHandle;
        _workspacePath = workspacePath;
        _dbPath = dbPath;
    }

    internal bool DatabaseExists { get; private set; }
    internal bool HasRecoverySidecars { get; private set; }
    internal long DatabaseLength => _databaseLength;

    internal string? DatabaseReadPath => !DatabaseExists || _databaseHandle is null
        ? null
        : OperatingSystem.IsWindows()
            ? _dbPath
            : $"/proc/{Environment.ProcessId}/fd/{_databaseHandle.DangerousGetHandle()}";

    /// <summary>The database name resolved through the retained destination-directory handle.
    /// Adjacent coordination artifacts use this path so a Linux lexical-path swap cannot redirect
    /// them after the destination was validated and pinned.</summary>
    internal string DatabaseAuthorityPath => OperatingSystem.IsWindows()
        ? _dbPath
        : $"/proc/{Environment.ProcessId}/fd/{_directoryHandle.DangerousGetHandle()}/" +
          Path.GetFileName(_dbPath);

    internal bool TryGetLeaseIdentity(out IndexLeaseIdentity? identity)
    {
        identity = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!TryGetWindowsInfo(_directoryHandle, out WinFileInfo directory))
                    return false;
                string directoryId = $"W:{directory.VolumeSerialNumber:X8}:" +
                    $"{directory.FileIndexHigh:X8}{directory.FileIndexLow:X8}";
                string? databaseId = null;
                if (DatabaseExists)
                {
                    if (_databaseHandle is null ||
                        !TryGetWindowsInfo(_databaseHandle, out WinFileInfo database))
                        return false;
                    databaseId = $"W:{database.VolumeSerialNumber:X8}:" +
                        $"{database.FileIndexHigh:X8}{database.FileIndexLow:X8}";
                }
                identity = new IndexLeaseIdentity(directoryId, databaseId);
                return true;
            }

            int directoryFd = _directoryHandle.DangerousGetHandle().ToInt32();
            if (!TryStatxFd(directoryFd, out StatxIdentity directoryIdentity)) return false;
            string directoryIdLinux = $"U:{directoryIdentity.Device:X16}:" +
                $"{directoryIdentity.Inode:X16}";
            string? databaseIdLinux = null;
            if (DatabaseExists)
            {
                if (_databaseHandle is null ||
                    !TryStatxFd(_databaseHandle.DangerousGetHandle().ToInt32(),
                        out StatxIdentity databaseIdentity)) return false;
                databaseIdLinux = $"U:{databaseIdentity.Device:X16}:" +
                    $"{databaseIdentity.Inode:X16}";
            }
            identity = new IndexLeaseIdentity(directoryIdLinux, databaseIdLinux);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the retained destination-directory identity plus the identity of the
    /// installed stage at the live database name. Unlike <see cref="TryGetLeaseIdentity"/>, this
    /// deliberately does not consult the pre-install database handle: a successful replacement
    /// makes that handle stale on Linux and closes it on Windows.</summary>
    internal bool TryGetInstalledLeaseIdentity(out IndexLeaseIdentity? identity)
    {
        identity = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (_stageWindowsIdentity is not { } expected ||
                    !TryGetWindowsInfo(_directoryHandle, out WinFileInfo directory))
                    return false;
                using SafeFileHandle installed = OpenWindowsFile(
                    _dbPath, GenericRead, FileShareRead | FileShareWrite);
                if (installed.IsInvalid ||
                    !TryGetWindowsInfo(installed, out WinFileInfo database) ||
                    (database.FileAttributes & (FileAttributeReparsePoint | 0x10)) != 0 ||
                    database.NumberOfLinks != 1 ||
                    WinFileIdentity.From(database) != expected)
                    return false;
                string directoryId = $"W:{directory.VolumeSerialNumber:X8}:" +
                    $"{directory.FileIndexHigh:X8}{directory.FileIndexLow:X8}";
                string databaseId = $"W:{database.VolumeSerialNumber:X8}:" +
                    $"{database.FileIndexHigh:X8}{database.FileIndexLow:X8}";
                identity = new IndexLeaseIdentity(directoryId, databaseId);
                return true;
            }

            if (_stageLinuxIdentity is not { } expectedLinux)
                return false;
            int directoryFd = _directoryHandle.DangerousGetHandle().ToInt32();
            if (!TryStatxFd(directoryFd, out StatxIdentity directoryIdentity) ||
                !TryStatxAt(directoryFd, Path.GetFileName(_dbPath),
                    out StatxIdentity installedLinux) ||
                !installedLinux.IsRegular || installedLinux.Links != 1 ||
                installedLinux.Device != expectedLinux.Device ||
                installedLinux.Inode != expectedLinux.Inode)
                return false;
            string directoryIdLinux = $"U:{directoryIdentity.Device:X16}:" +
                $"{directoryIdentity.Inode:X16}";
            string databaseIdLinux = $"U:{installedLinux.Device:X16}:" +
                $"{installedLinux.Inode:X16}";
            identity = new IndexLeaseIdentity(directoryIdLinux, databaseIdLinux);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool TryGetWorkspaceIdentity(out string? identity)
    {
        identity = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!TryGetWindowsInfo(_workspaceHandle, out WinFileInfo workspace) ||
                    (workspace.FileAttributes &
                        (FileAttributeReparsePoint | 0x00000010)) != 0x00000010)
                    return false;
                identity = $"W:{workspace.VolumeSerialNumber:X8}:" +
                    $"{workspace.FileIndexHigh:X8}{workspace.FileIndexLow:X8}";
                return true;
            }

            if (!TryStatxFd(_workspaceHandle.DangerousGetHandle().ToInt32(),
                    out StatxIdentity workspaceIdentity))
                return false;
            identity = $"U:{workspaceIdentity.Device:X16}:{workspaceIdentity.Inode:X16}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal string WorkspaceReadPath => OperatingSystem.IsWindows()
        ? _workspacePath
        : $"/proc/{Environment.ProcessId}/fd/{_workspaceHandle.DangerousGetHandle()}";

    /// <summary>
    /// Removes exact Phoenix private-publication artifacts left by a crashed prior writer. Callers
    /// must hold both the physical-workspace writer lease and this destination's live claim, and
    /// must invoke this once before allocating their own stage. Enumeration on Linux resolves
    /// through the retained directory descriptor; Windows' retained no-delete directory chain
    /// prevents the lexical directory from being replaced during enumeration.
    /// </summary>
    internal bool TryReapAbandonedPublicationArtifacts(
        IndexOwnershipLease ownershipLease,
        IndexDestinationClaim destinationClaim,
        out int removed,
        out PublicationArtifactReapFailure failure,
        out int observedCandidates)
    {
        removed = 0;
        failure = PublicationArtifactReapFailure.None;
        observedCandidates = 0;
        try
        {
            if (_stageName is not null ||
                !ownershipLease.IsActive ||
                !destinationClaim.IsActiveFor(
                    ownershipLease.WorkspaceIdentity, DatabaseAuthorityPath) ||
                IndexDestinationClaim.ReadState(
                    _workspacePath, DatabaseAuthorityPath) is not
                    (IndexDestinationClaimState.Ready or
                     IndexDestinationClaimState.Rebuilding) ||
                !TryGetWorkspaceIdentity(out string? workspaceIdentity) ||
                !string.Equals(workspaceIdentity, ownershipLease.WorkspaceIdentity,
                    StringComparison.Ordinal))
            {
                failure = PublicationArtifactReapFailure.OwnershipChanged;
                return false;
            }

            string directoryPath = OperatingSystem.IsWindows()
                ? Path.GetDirectoryName(_dbPath)!
                : $"/proc/{Environment.ProcessId}/fd/" +
                  $"{_directoryHandle.DangerousGetHandle()}";
            if (!TrySelectPrivatePublicationArtifactNames(
                    Directory.EnumerateFileSystemEntries(directoryPath),
                    MaxAbandonedPublicationArtifacts,
                    AbandonedPublicationScanBudget,
                    out List<string> candidates,
                    out failure,
                    out observedCandidates))
                return false;
            if (candidates.Count == 0) return true;

            bool reaped = OperatingSystem.IsWindows()
                ? TryReapWindowsArtifacts(candidates, out removed)
                : TryReapLinuxArtifacts(candidates, out removed);
            if (!reaped)
                failure = PublicationArtifactReapFailure.UnsafeArtifactSet;
            return reaped;
        }
        catch
        {
            removed = 0;
            failure = PublicationArtifactReapFailure.UnsafeArtifactSet;
            return false;
        }
    }

    internal static string DescribePublicationArtifactReapFailure(
        PublicationArtifactReapFailure failure, int observedCandidates) =>
        failure switch
        {
            PublicationArtifactReapFailure.CandidateLimitExceeded =>
                $"abandoned publication artifact discovery exceeded the " +
                $"{MaxAbandonedPublicationArtifacts}-artifact cap " +
                $"(at least {observedCandidates} candidates observed); remove stale " +
                ".phoenix-stage-* and .phoenix-publish-* entries manually and retry",
            PublicationArtifactReapFailure.ScanBudgetExceeded =>
                $"abandoned publication artifact discovery exceeded the " +
                $"{AbandonedPublicationScanBudget.TotalSeconds:0}-second budget after " +
                $"{observedCandidates} candidate(s); inspect the index directory and retry",
            PublicationArtifactReapFailure.OwnershipChanged =>
                "index destination ownership changed before abandoned publication artifacts " +
                "could be cleaned",
            _ => "abandoned publication artifact link set could not be cleaned safely",
        };

    private bool TryReapWindowsArtifacts(IReadOnlyList<string> names, out int removed)
    {
        removed = 0;
        var candidates = new List<WindowsArtifactCandidate>(names.Count);
        try
        {
            string directory = Path.GetDirectoryName(_dbPath)!;
            foreach (string name in names)
            {
                SafeFileHandle handle = OpenWindowsFile(
                    Path.Combine(directory, name),
                    GenericRead,
                    FileShareRead | FileShareWrite);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    if (error is 2 or 3) continue;
                    return false;
                }
                if (!TryGetWindowsInfo(handle, out WinFileInfo info) ||
                    (info.FileAttributes & (FileAttributeReparsePoint | 0x10)) != 0 ||
                    info.NumberOfLinks == 0)
                {
                    handle.Dispose();
                    return false;
                }
                candidates.Add(new WindowsArtifactCandidate(
                    name, handle, WinFileIdentity.From(info), info.NumberOfLinks));
            }

            foreach (IGrouping<WinFileIdentity, WindowsArtifactCandidate> group in
                     candidates.GroupBy(candidate => candidate.Identity))
            {
                uint links = group.First().Links;
                if (group.Any(candidate => candidate.Links != links) ||
                    links != group.Count())
                    return false;
            }

            // Retained read handles prevent any link in a candidate inode from being renamed or
            // deleted while the complete set is inspected. Release them before the delete pass:
            // a DELETE-access handle cannot coexist with a handle that deliberately withheld
            // FILE_SHARE_DELETE. Each exact name is then reopened without following reparses,
            // and both identity and the remaining link count are checked immediately before its
            // delete-on-close disposition is armed.
            foreach (WindowsArtifactCandidate candidate in candidates)
                candidate.Handle.Dispose();
            var remainingLinks = candidates
                .GroupBy(candidate => candidate.Identity)
                .ToDictionary(group => group.Key, group => (uint)group.Count());
            foreach (WindowsArtifactCandidate candidate in candidates)
            {
                string path = Path.Combine(directory, candidate.Name);
                using SafeFileHandle deletionHandle = OpenWindowsFile(
                    path, GenericRead | DeleteAccess,
                    FileShareRead | FileShareWrite);
                if (deletionHandle.IsInvalid ||
                    !TryGetWindowsInfo(deletionHandle, out WinFileInfo current) ||
                    (current.FileAttributes & (FileAttributeReparsePoint | 0x10)) != 0 ||
                    WinFileIdentity.From(current) != candidate.Identity ||
                    !remainingLinks.TryGetValue(candidate.Identity, out uint expectedLinks) ||
                    current.NumberOfLinks != expectedLinks ||
                    !MarkWindowsDeleteOnClose(deletionHandle))
                    return false;
                removed++;
                remainingLinks[candidate.Identity] = expectedLinks - 1;
            }
            return true;
        }
        finally
        {
            foreach (WindowsArtifactCandidate candidate in candidates)
                candidate.Handle.Dispose();
        }
    }

    private bool TryReapLinuxArtifacts(IReadOnlyList<string> names, out int removed)
    {
        removed = 0;
        int directoryFd = _directoryHandle.DangerousGetHandle().ToInt32();
        var candidates = new List<LinuxArtifactCandidate>(names.Count);
        try
        {
            foreach (string name in names)
            {
                int fd = openat(directoryFd, name, LinuxFileInspectFlags, 0);
                if (fd < 0)
                {
                    if (Marshal.GetLastPInvokeError() == 2) continue;
                    return false;
                }
                var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
                if (!TryStatxFd(fd, out StatxIdentity identity) ||
                    !identity.IsRegular || identity.Links == 0)
                {
                    handle.Dispose();
                    return false;
                }
                candidates.Add(new LinuxArtifactCandidate(name, handle, identity));
            }

            foreach (IGrouping<(ulong Device, ulong Inode), LinuxArtifactCandidate> group in
                     candidates.GroupBy(candidate =>
                         (candidate.Identity.Device, candidate.Identity.Inode)))
            {
                uint links = group.First().Identity.Links;
                if (group.Any(candidate => candidate.Identity.Links != links) ||
                    links != group.Count())
                    return false;
            }

            foreach (LinuxArtifactCandidate candidate in candidates)
            {
                if (!TryStatxAt(directoryFd, candidate.Name, out StatxIdentity named) ||
                    named.Device != candidate.Identity.Device ||
                    named.Inode != candidate.Identity.Inode ||
                    unlinkat(directoryFd, candidate.Name, 0) != 0)
                    return false;
                removed++;
            }
            return true;
        }
        finally
        {
            foreach (LinuxArtifactCandidate candidate in candidates)
                candidate.Handle.Dispose();
        }
    }

    internal static bool TrySelectPrivatePublicationArtifactNames(
        IEnumerable<string> entries, int maxCandidates, TimeSpan scanBudget,
        out List<string> candidates,
        out PublicationArtifactReapFailure failure,
        out int observedCandidates,
        Func<TimeSpan>? elapsedForTest = null)
    {
        candidates = new List<string>(Math.Min(Math.Max(maxCandidates, 0), 16));
        failure = PublicationArtifactReapFailure.None;
        observedCandidates = 0;
        if (maxCandidates < 0)
        {
            failure = PublicationArtifactReapFailure.CandidateLimitExceeded;
            return false;
        }
        if (scanBudget <= TimeSpan.Zero)
        {
            failure = PublicationArtifactReapFailure.ScanBudgetExceeded;
            return false;
        }
        long started = Stopwatch.GetTimestamp();
        Func<TimeSpan> elapsed = elapsedForTest ??
            (() => Stopwatch.GetElapsedTime(started));
        foreach (string entry in entries)
        {
            if (elapsed() >= scanBudget)
            {
                failure = PublicationArtifactReapFailure.ScanBudgetExceeded;
                return false;
            }
            string? name = Path.GetFileName(entry);
            if (name is null || !IsPrivatePublicationArtifactName(name)) continue;
            observedCandidates++;
            if (candidates.Count == maxCandidates)
            {
                failure = PublicationArtifactReapFailure.CandidateLimitExceeded;
                return false;
            }
            candidates.Add(name);
        }
        return true;
    }

    internal static bool IsPrivatePublicationArtifactName(string name)
    {
        string stem = name;
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (!stem.EndsWith(suffix, StringComparison.Ordinal)) continue;
            stem = stem[..^suffix.Length];
            break;
        }
        if (!stem.EndsWith(".db", StringComparison.Ordinal)) return false;
        stem = stem[..^3];

        const string stagePrefix = ".phoenix-stage-";
        const string publishPrefix = ".phoenix-publish-";
        string token;
        if (stem.StartsWith(stagePrefix, StringComparison.Ordinal))
            token = stem[stagePrefix.Length..];
        else if (stem.StartsWith(publishPrefix, StringComparison.Ordinal))
            token = stem[publishPrefix.Length..];
        else
            return false;
        return token.Length == 32 && token.All(Uri.IsHexDigit);
    }

    /// <summary>Whether this platform and path layout require the handle-pinned publication path.
    /// A false result is an intentional compatibility fallback (macOS or an external database);
    /// a true result means TryOpen failure is a safety refusal, never permission to rebuild
    /// lexically.</summary>
    internal static bool IsAnchoredPublicationRequired(
        string gitRoot, string workspacePath, string dbPath)
    {
        if (OperatingSystem.IsMacOS()) return false;
        try
        {
            string root = Path.GetFullPath(gitRoot);
            string workspace = Path.GetFullPath(workspacePath);
            string database = Path.GetFullPath(dbPath);
            return WorkspacePaths.IsSameOrDescendantPath(workspace, root) &&
                   WorkspacePaths.IsSameOrDescendantPath(database, workspace);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryOpen(string gitRoot, string workspacePath, string dbPath,
        bool createIndexDirectory, out AnchoredIndexDestination? destination)
    {
        destination = null;
        if (!IsAnchoredPublicationRequired(gitRoot, workspacePath, dbPath))
            return false;
        try
        {
            string root = Path.GetFullPath(gitRoot);
            string workspace = Path.GetFullPath(workspacePath);
            string database = Path.GetFullPath(dbPath);
            string indexDirectory = Path.GetDirectoryName(database)!;

            bool opened = OperatingSystem.IsWindows()
                ? TryOpenWindows(workspace, indexDirectory, database,
                    createIndexDirectory, out destination)
                : TryOpenLinux(root, workspace, indexDirectory, database,
                    createIndexDirectory, out destination);
            if (!opened || destination is null) return false;
            if (!destination.InspectDestination())
            {
                destination.Dispose();
                destination = null;
                return false;
            }
            return true;
        }
        catch
        {
            destination?.Dispose();
            destination = null;
            return false;
        }
    }

    internal string CreateStagePath()
    {
        if (_stageName is not null) throw new InvalidOperationException("stage already allocated");
        _stageName = ".phoenix-stage-" + Guid.NewGuid().ToString("N") + ".db";
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(Path.GetDirectoryName(_dbPath)!, _stageName);
            _stageHandle = CreateFileW(path, GenericRead | GenericWrite,
                FileShareRead | FileShareWrite, IntPtr.Zero, 1,
                FileFlagOpenReparsePoint | 0x00000080, IntPtr.Zero); // CREATE_NEW
            if (_stageHandle.IsInvalid)
            {
                _stageHandle.Dispose();
                throw new IOException("could not reserve index stage");
            }
            if (!TryGetWindowsInfo(_stageHandle, out WinFileInfo stageInfo) ||
                (stageInfo.FileAttributes & (FileAttributeReparsePoint | 0x10)) != 0 ||
                stageInfo.NumberOfLinks != 1)
            {
                _stageHandle.Dispose();
                throw new IOException("could not identify index stage");
            }
            _stageWindowsIdentity = WinFileIdentity.From(stageInfo);
            _handles.Add(_stageHandle);
            BeforeStageSidecarReservationForTest?.Invoke(path);
            if (TryReserveSidecarGuards(_stageName, _stageSidecarGuards,
                    sqliteCompatible: true) != SidecarReservationResult.Reserved)
                throw new IOException("could not reserve staged SQLite sidecars");
            return path;
        }

        int dirFd = _directoryHandle.DangerousGetHandle().ToInt32();
        int stageFd = openat(dirFd, _stageName,
            LinuxOpenFlags.ExclusiveCreateReadWrite,
            Convert.ToUInt32("600", 8)); // RDWR|CREAT|EXCL|NOFOLLOW|CLOEXEC
        if (stageFd < 0) throw new IOException("could not reserve index stage");
        _stageHandle = new SafeFileHandle((IntPtr)stageFd, ownsHandle: true);
        if (!TryStatxFd(stageFd, out StatxIdentity stageIdentity) ||
            !stageIdentity.IsRegular || stageIdentity.Links != 1)
        {
            _stageHandle.Dispose();
            throw new IOException("could not identify index stage");
        }
        _stageLinuxIdentity = stageIdentity;
        _handles.Add(_stageHandle);
        BeforeStageSidecarReservationForTest?.Invoke(
            Path.Combine(Path.GetDirectoryName(_dbPath)!, _stageName));
        if (TryReserveSidecarGuards(_stageName, _stageSidecarGuards,
                sqliteCompatible: true) != SidecarReservationResult.Reserved)
            throw new IOException("could not reserve staged SQLite sidecars");
        return $"/proc/{Environment.ProcessId}/fd/{stageFd}";
    }

    internal bool InstallStage(TimeSpan? replacementWaitTimeout = null,
        Action? waitingForReaders = null)
    {
        TimeSpan timeout = replacementWaitTimeout ?? TimeSpan.FromMinutes(3);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        bool announced = false;
        while (true)
        {
            InstallAttemptResult result = TryInstallStage();
            if (result == InstallAttemptResult.Installed) return true;
            if (result != InstallAttemptResult.RetryableContention ||
                wait.Elapsed >= timeout) return false;
            if (!announced)
            {
                announced = true;
                waitingForReaders?.Invoke();
            }
            Thread.Sleep(100);
        }
    }

    private InstallAttemptResult TryInstallStage()
    {
        if (_stageName is null) return InstallAttemptResult.Failed;
        try
        {
            SidecarReservationResult sidecars =
                TryReserveSidecarGuards(Path.GetFileName(_dbPath),
                    _publishSidecarGuards, sqliteCompatible: false);
            if (sidecars != SidecarReservationResult.Reserved)
                return sidecars == SidecarReservationResult.RetryableContention
                    ? InstallAttemptResult.RetryableContention
                    : InstallAttemptResult.Failed;
            if (OperatingSystem.IsWindows()) _databaseHandle?.Dispose();
            if (!OperatingSystem.IsWindows())
                return InstallLinux()
                    ? InstallAttemptResult.Installed
                    : InstallAttemptResult.Failed;
            if (InstallWindows(out int error))
                return InstallAttemptResult.Installed;
            return IsWindowsReplacementContention(error)
                ? InstallAttemptResult.RetryableContention
                : InstallAttemptResult.Failed;
        }
        catch
        {
            return InstallAttemptResult.Failed;
        }
        finally
        {
            ReleaseSidecarGuards(_publishSidecarGuards);
        }
    }

    private SidecarReservationResult TryReserveSidecarGuards(
        string leaf, List<SidecarGuard> guards,
        bool sqliteCompatible)
    {
        if (guards.Count != 0) return SidecarReservationResult.Failed;
        foreach (string name in new[]
                 { leaf + "-wal", leaf + "-shm", leaf + "-journal" })
        {
            if (OperatingSystem.IsWindows())
            {
                string path = Path.Combine(Path.GetDirectoryName(_dbPath)!, name);
                SafeFileHandle handle = CreateFileW(path,
                    GenericRead | GenericWrite |
                    (sqliteCompatible ? 0 : DeleteAccess),
                    FileShareRead | FileShareWrite, IntPtr.Zero, 1,
                    FileFlagOpenReparsePoint | 0x00000080, IntPtr.Zero);
                if (handle.IsInvalid && !sqliteCompatible &&
                    Marshal.GetLastPInvokeError() is 80 or 183)
                {
                    handle.Dispose();
                    SidecarReservationResult removed =
                        TryRemoveCurrentWindowsSidecar(path);
                    if (removed != SidecarReservationResult.Reserved)
                    {
                        ReleaseSidecarGuards(guards);
                        return removed;
                    }
                    handle = CreateFileW(path,
                        GenericRead | GenericWrite | DeleteAccess,
                        FileShareRead | FileShareWrite, IntPtr.Zero, 1,
                        FileFlagOpenReparsePoint | 0x00000080, IntPtr.Zero);
                }
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    ReleaseSidecarGuards(guards);
                    return IsWindowsSidecarContention(error)
                        ? SidecarReservationResult.RetryableContention
                        : SidecarReservationResult.Failed;
                }
                if (!TryGetWindowsInfo(handle, out WinFileInfo info) ||
                    (info.FileAttributes & (FileAttributeReparsePoint | 0x10)) != 0 ||
                    info.NumberOfLinks != 1)
                {
                    handle.Dispose();
                    ReleaseSidecarGuards(guards);
                    return SidecarReservationResult.Failed;
                }
                guards.Add(new SidecarGuard(name, handle,
                    WinFileIdentity.From(info), null, !sqliteCompatible,
                    MayContainSqliteBytes: sqliteCompatible));
            }
            else
            {
                int dirFd = _directoryHandle.DangerousGetHandle().ToInt32();
                int fd = openat(dirFd, name,
                    LinuxOpenFlags.ExclusiveCreateReadWrite,
                    Convert.ToUInt32("600", 8));
                if (fd < 0 && !sqliteCompatible &&
                    Marshal.GetLastPInvokeError() == 17)
                {
                    if (!TryStatxAt(dirFd, name, out StatxIdentity existing,
                            out int statxError))
                    {
                        ReleaseSidecarGuards(guards);
                        return statxError == 2
                            ? SidecarReservationResult.RetryableContention
                            : SidecarReservationResult.Failed;
                    }
                    if (!existing.IsRegular || existing.Links != 1)
                    {
                        ReleaseSidecarGuards(guards);
                        return SidecarReservationResult.Failed;
                    }
                    // Inspect the then-current live name only inside the post-drain publication
                    // boundary. SQLite may create, remove, or replace this sidecar while the
                    // private stage is being built, so stage-time identity is not authority.
                    if (unlinkat(dirFd, name, 0) != 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        ReleaseSidecarGuards(guards);
                        return error == 2
                            ? SidecarReservationResult.RetryableContention
                            : SidecarReservationResult.Failed;
                    }
                    TestOnlyAfterLiveSidecarRemoval?.Invoke(
                        Path.Combine(Path.GetDirectoryName(_dbPath)!, name));
                    fd = openat(dirFd, name,
                        LinuxOpenFlags.ExclusiveCreateReadWrite,
                        Convert.ToUInt32("600", 8));
                }
                if (fd < 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    ReleaseSidecarGuards(guards);
                    return error == 17
                        ? SidecarReservationResult.RetryableContention
                        : SidecarReservationResult.Failed;
                }
                if (!TryStatxFd(fd, out StatxIdentity identity) ||
                    !identity.IsRegular || identity.Links != 1)
                {
                    close(fd);
                    ReleaseSidecarGuards(guards);
                    return SidecarReservationResult.Failed;
                }
                guards.Add(new SidecarGuard(name,
                    new SafeFileHandle((IntPtr)fd, ownsHandle: true), null, identity,
                    DeleteAccessHeld: false, MayContainSqliteBytes: sqliteCompatible));
            }
        }
        return SidecarReservationResult.Reserved;
    }

    private SidecarReservationResult TryRemoveCurrentWindowsSidecar(string path)
    {
        using SafeFileHandle existing = OpenWindowsFile(path,
            GenericRead | DeleteAccess, FileShareRead | FileShareWrite);
        if (existing.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3) return SidecarReservationResult.Reserved;
            return IsWindowsReplacementContention(error)
                ? SidecarReservationResult.RetryableContention
                : SidecarReservationResult.Failed;
        }
        if (!TryGetWindowsInfo(existing, out WinFileInfo info) ||
            (info.FileAttributes & (FileAttributeReparsePoint | 0x10)) != 0 ||
            info.NumberOfLinks != 1)
            return SidecarReservationResult.Failed;
        if (MarkWindowsDeleteOnClose(existing))
        {
            existing.Dispose();
            TestOnlyAfterLiveSidecarRemoval?.Invoke(path);
            return SidecarReservationResult.Reserved;
        }
        return IsWindowsReplacementContention(Marshal.GetLastPInvokeError())
            ? SidecarReservationResult.RetryableContention
            : SidecarReservationResult.Failed;
    }

    private void ReleaseSidecarGuards(List<SidecarGuard> guards)
    {
        for (int i = guards.Count - 1; i >= 0; i--)
        {
            SidecarGuard guard = guards[i];
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SafeFileHandle deletionHandle = guard.Handle;
                    bool reopened = false;
                    if (!guard.DeleteAccessHeld)
                    {
                        guard.Handle.Dispose();
                        string path = Path.Combine(Path.GetDirectoryName(_dbPath)!, guard.Name);
                        deletionHandle = OpenWindowsFile(path, GenericRead | DeleteAccess,
                            FileShareRead | FileShareWrite);
                        reopened = true;
                    }
                    if (guard.WindowsIdentity is { } expected &&
                        !deletionHandle.IsInvalid &&
                        TryGetWindowsInfo(deletionHandle, out WinFileInfo info) &&
                        WinFileIdentity.From(info) == expected &&
                        (guard.MayContainSqliteBytes ||
                         (info.FileSizeHigh == 0 && info.FileSizeLow == 0)))
                    {
                        MarkWindowsDeleteOnClose(deletionHandle);
                    }
                    if (reopened) deletionHandle.Dispose();
                }
                else
                {
                    int dirFd = _directoryHandle.DangerousGetHandle().ToInt32();
                    if (guard.LinuxIdentity is { } expected &&
                        TryStatxAt(dirFd, guard.Name, out StatxIdentity named) &&
                        named.Device == expected.Device && named.Inode == expected.Inode &&
                        (guard.MayContainSqliteBytes || named.Size == 0))
                    {
                        unlinkat(dirFd, guard.Name, 0);
                    }
                }
            }
            catch { }
            finally { guard.Handle.Dispose(); }
        }
        guards.Clear();
    }

    private bool InspectDestination()
    {
        string leaf = Path.GetFileName(_dbPath);
        if (OperatingSystem.IsWindows())
        {
            string directory = Path.GetDirectoryName(_dbPath)!;
            HasRecoverySidecars = EntryExistsWindows(_dbPath + "-wal") ||
                EntryExistsWindows(_dbPath + "-shm") ||
                EntryExistsWindows(_dbPath + "-journal");
            string path = Path.Combine(directory, leaf);
            if (!EntryExistsWindows(path))
            {
                DatabaseExists = false;
                return true;
            }
            SafeFileHandle file = OpenWindowsFile(path, GenericRead,
                FileShareRead | FileShareWrite);
            if (file.IsInvalid || !TryGetWindowsInfo(file, out WinFileInfo info) ||
                (info.FileAttributes & FileAttributeReparsePoint) != 0 ||
                info.NumberOfLinks != 1 || (info.FileAttributes & 0x10) != 0)
            {
                file.Dispose();
                return false;
            }
            _databaseHandle = file;
            _handles.Add(file);
            _databaseLength = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            DatabaseExists = true;
            return true;
        }

        int dirFd = _directoryHandle.DangerousGetHandle().ToInt32();
        HasRecoverySidecars = EntryExistsLinux(dirFd, leaf + "-wal") ||
            EntryExistsLinux(dirFd, leaf + "-shm") ||
            EntryExistsLinux(dirFd, leaf + "-journal");
        int fd = openat(dirFd, leaf, LinuxFileInspectFlags, 0);
        if (fd < 0)
        {
            if (Marshal.GetLastPInvokeError() == 2)
            {
                DatabaseExists = false;
                return true;
            }
            return false;
        }
        using var pathHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        if (!TryStatxFd(fd, out StatxIdentity identity) ||
            !identity.IsRegular || identity.Links != 1) return false;

        string procPath = $"/proc/{Environment.ProcessId}/fd/{fd}";
        int readFd = open(procPath, LinuxOpenFlags.ReadNonBlocking, 0);
        if (readFd < 0 || !TryStatxFd(readFd, out StatxIdentity readIdentity) ||
            readIdentity.Device != identity.Device || readIdentity.Inode != identity.Inode)
        {
            if (readFd >= 0) close(readFd);
            return false;
        }
        _databaseHandle = new SafeFileHandle((IntPtr)readFd, ownsHandle: true);
        _handles.Add(_databaseHandle);
        _databaseLength = readIdentity.Size <= long.MaxValue
            ? (long)readIdentity.Size
            : 0;
        DatabaseExists = true;
        return true;
    }

    private bool InstallWindows(out int error)
    {
        error = 0;
        using SafeFileHandle? stage = OpenReleasedWindowsStageForDelete();
        if (stage is null) return false;
        if (!TryGetWindowsInfo(stage, out WinFileInfo info) ||
            (info.FileAttributes & FileAttributeReparsePoint) != 0 ||
            info.NumberOfLinks != 1 || (info.FileAttributes & 0x10) != 0) return false;
        bool renamed = RenameWindowsHandle(stage, _dbPath);
        if (!renamed) error = Marshal.GetLastPInvokeError();
        return renamed;
    }

    private static bool IsWindowsReplacementContention(int error) =>
        error is 5 or 32 or 33;

    private static bool IsWindowsSidecarContention(int error) =>
        IsWindowsReplacementContention(error) || error is 80 or 183;

    private SafeFileHandle? OpenReleasedWindowsStageForDelete()
    {
        if (_stageName is null || _stageWindowsIdentity is null) return null;
        if (!_stageGuardReleased)
        {
            _stageHandle?.Dispose();
            _stageGuardReleased = true;
        }

        string path = Path.Combine(Path.GetDirectoryName(_dbPath)!, _stageName);
        SafeFileHandle handle = OpenWindowsFile(path, GenericRead | DeleteAccess,
            FileShareRead | FileShareWrite);
        if (handle.IsInvalid || !TryGetWindowsInfo(handle, out WinFileInfo info) ||
            WinFileIdentity.From(info) != _stageWindowsIdentity.Value)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    private bool InstallLinux()
    {
        int dirFd = _directoryHandle.DangerousGetHandle().ToInt32();
        int stageFd = _stageHandle!.DangerousGetHandle().ToInt32();
        if (!TryStatxFd(stageFd, out StatxIdentity identity) ||
            !identity.IsRegular || identity.Links != 1) return false;

        string publish = ".phoenix-publish-" + Guid.NewGuid().ToString("N") + ".db";
        string procStage = $"/proc/{Environment.ProcessId}/fd/{stageFd}";
        if (linkat(-100, procStage, dirFd, publish, 0x400) != 0) return false;

        // Drop the original stage link before publish so a crash after rename leaves the live
        // database single-linked. The publish link keeps the exact held inode alive.
        if (!TryStatxAt(dirFd, _stageName!, out StatxIdentity named) ||
            named.Device != identity.Device || named.Inode != identity.Inode ||
            unlinkat(dirFd, _stageName!, 0) != 0)
        {
            unlinkat(dirFd, publish, 0);
            return false;
        }
        if (renameat(dirFd, publish, dirFd, Path.GetFileName(_dbPath)) != 0)
        {
            unlinkat(dirFd, publish, 0);
            return false;
        }
        return true;
    }

    private static bool TryOpenWindows(string workspace, string indexDirectory,
        string dbPath, bool createIndexDirectory,
        out AnchoredIndexDestination? destination)
    {
        destination = null;
        var handles = new List<SafeFileHandle>();
        SafeFileHandle? workspaceHandle = null;
        try
        {
            string volumeRoot = Path.GetPathRoot(workspace)!;
            foreach (string path in DirectoryChain(volumeRoot, workspace))
            {
                SafeFileHandle handle = OpenWindowsDirectory(path);
                if (handle.IsInvalid || IsWindowsReparse(handle))
                {
                    handle.Dispose();
                    return false;
                }
                handles.Add(handle);
                workspaceHandle = handle;
            }
            if (!Directory.Exists(indexDirectory))
            {
                if (!createIndexDirectory) return false;
                Directory.CreateDirectory(indexDirectory);
            }
            SafeFileHandle indexHandle = OpenWindowsDirectory(indexDirectory);
            if (indexHandle.IsInvalid || IsWindowsReparse(indexHandle))
            {
                indexHandle.Dispose();
                return false;
            }
            handles.Add(indexHandle);
            destination = new AnchoredIndexDestination(handles, workspaceHandle!, indexHandle,
                workspace, dbPath);
            return true;
        }
        finally
        {
            if (destination is null)
                foreach (SafeFileHandle handle in handles) handle.Dispose();
        }
    }

    private static bool TryOpenLinux(string root, string workspace,
        string indexDirectory, string dbPath, bool createIndexDirectory,
        out AnchoredIndexDestination? destination)
    {
        destination = null;
        var handles = new List<SafeFileHandle>();
        string filesystemRoot = Path.GetPathRoot(root) ?? "/";
        int currentFd = OpenLinuxDirectory(filesystemRoot);
        if (currentFd < 0) return false;
        var current = new SafeFileHandle((IntPtr)currentFd, ownsHandle: true);
        handles.Add(current);
        try
        {
            foreach (string component in SplitRelative(
                         Path.GetRelativePath(filesystemRoot, root)))
            {
                int next = openat(currentFd, component, LinuxDirectoryFlags, 0);
                if (next < 0) return false;
                currentFd = next;
                current = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                handles.Add(current);
            }
            foreach (string component in SplitRelative(Path.GetRelativePath(root, workspace)))
            {
                int next = openat(currentFd, component, LinuxDirectoryFlags, 0);
                if (next < 0) return false;
                currentFd = next;
                current = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                handles.Add(current);
            }
            SafeFileHandle workspaceHandle = current;
            foreach (string component in SplitRelative(
                         Path.GetRelativePath(workspace, indexDirectory)))
            {
                int next = openat(currentFd, component, LinuxDirectoryFlags, 0);
                if (next < 0 && createIndexDirectory && Marshal.GetLastPInvokeError() == 2)
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
            destination = new AnchoredIndexDestination(handles, workspaceHandle, current,
                workspace, dbPath);
            return true;
        }
        finally
        {
            if (destination is null)
                foreach (SafeFileHandle handle in handles) handle.Dispose();
        }
    }

    private static IEnumerable<string> DirectoryChain(string root, string leaf)
    {
        yield return root;
        string current = root;
        foreach (string component in SplitRelative(Path.GetRelativePath(root, leaf)))
        {
            current = Path.Combine(current, component);
            yield return current;
        }
    }

    private static IEnumerable<string> SplitRelative(string relative)
    {
        if (relative == ".") yield break;
        foreach (string component in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..") throw new InvalidOperationException();
            yield return component;
        }
    }

    private static SafeFileHandle OpenWindowsDirectory(string path) =>
        CreateFileW(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

    private static SafeFileHandle OpenWindowsFile(string path, uint access, uint share) =>
        CreateFileW(path, access, share, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);

    private static bool IsWindowsReparse(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo info,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (info.FileAttributes & FileAttributeReparsePoint) != 0;
    }

    private static bool TryGetWindowsInfo(SafeFileHandle handle, out WinFileInfo info) =>
        GetFileInformationByHandle(handle, out info);

    private static bool RenameWindowsHandle(SafeFileHandle file, string destinationPath)
    {
        byte[] name = System.Text.Encoding.Unicode.GetBytes(destinationPath);
        int rootOffset = IntPtr.Size;
        int lengthOffset = IntPtr.Size * 2;
        int nameOffset = lengthOffset + sizeof(uint);
        // The buffer MUST include a zeroed terminating WCHAR beyond FileNameLength. Sized
        // exactly to the name, the kernel's rename path can consume one WCHAR past the
        // allocation: heap garbage there silently renamed installs to "index.db<χ>" (found
        // as index.dbm/index.dbl under suite load) or failed them outright on an invalid
        // character — the batch-43 worktree flake. FileNameLength still excludes the
        // terminator; only the allocation and dwBufferSize cover it.
        int bufferSize = nameOffset + name.Length + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteByte(buffer, 0, 1); // ReplaceIfExists
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            return SetFileInformationByHandle(file, 3, buffer, (uint)bufferSize);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool MarkWindowsDeleteOnClose(SafeFileHandle file)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 0, 1);
            return SetFileInformationByHandle(file, 4, buffer, 1); // FileDispositionInfo
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool EntryExistsLinux(int dirFd, string leaf) =>
        TryStatxAt(dirFd, leaf, out _);

    private static bool EntryExistsWindows(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, 0,
            FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero,
            OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return true;
        return Marshal.GetLastPInvokeError() is not (2 or 3);
    }

    private static bool TryStatxFd(int fd, out StatxIdentity identity) =>
        TryStatx(fd, "", 0x1000, out identity, out _); // AT_EMPTY_PATH

    private static bool TryStatxAt(int dirFd, string leaf, out StatxIdentity identity) =>
        TryStatx(dirFd, leaf, 0x100, out identity, out _); // AT_SYMLINK_NOFOLLOW

    private static bool TryStatxAt(int dirFd, string leaf, out StatxIdentity identity,
        out int error) =>
        TryStatx(dirFd, leaf, 0x100, out identity, out error); // AT_SYMLINK_NOFOLLOW

    internal const uint StatxIdentityMask = 0x00000001 | 0x00000002 | 0x00000004 |
        0x00000100 | 0x00000200; // TYPE|MODE|NLINK|INO|SIZE

    internal static bool TryValidateStatxIdentity(bool syscallSucceeded, uint returnedMask,
        int lastError, out int error)
    {
        error = syscallSucceeded ? 0 : lastError;
        return syscallSucceeded && (returnedMask & StatxIdentityMask) == StatxIdentityMask;
    }

    private static bool TryStatx(int dirFd, string path, int flags,
        out StatxIdentity identity, out int error)
    {
        identity = default;
        error = 0;
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (int offset = 0; offset < 256; offset += sizeof(long))
                Marshal.WriteInt64(buffer, offset, 0);
            // STATX_TYPE (0x1) owns the S_IFMT bits this identity trusts; STATX_MODE (0x2) owns
            // the permission bits. Request and require BOTH so a mask-honoring filesystem can
            // never satisfy the gate while leaving S_IFMT unfilled (review PhoenixCodeNav-0ce1).
            int statxResult = statx(dirFd, path, flags, StatxIdentityMask, buffer);
            bool syscallSucceeded = statxResult == 0;
            int lastError = syscallSucceeded ? 0 : Marshal.GetLastPInvokeError();
            uint mask = syscallSucceeded
                ? unchecked((uint)Marshal.ReadInt32(buffer, 0))
                : 0;
            // statx succeeded here, so an incomplete mask is an authority failure, not an
            // errno-bearing syscall failure. Keep error at zero rather than leaking stale errno
            // from an earlier openat into the publication contention classifier.
            if (!TryValidateStatxIdentity(syscallSucceeded, mask, lastError, out error))
                return false;
            uint links = unchecked((uint)Marshal.ReadInt32(buffer, 16));
            ushort mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            ulong size = unchecked((ulong)Marshal.ReadInt64(buffer, 40));
            uint major = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            uint minor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            identity = new StatxIdentity(((ulong)major << 32) | minor, inode, links,
                (mode & 0xF000) == 0x8000, size);
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int OpenLinuxDirectory(string path) =>
        open(path, LinuxDirectoryFlags, 0);
    private static int LinuxDirectoryFlags => LinuxOpenFlags.DirectoryOpen;
    private static int LinuxFileInspectFlags => LinuxOpenFlags.PathInspect;

    public void Dispose()
    {
        ReleaseSidecarGuards(_publishSidecarGuards);
        ReleaseSidecarGuards(_stageSidecarGuards);
        if (OperatingSystem.IsWindows() && _stageName is not null)
        {
            try
            {
                using SafeFileHandle? stage = OpenReleasedWindowsStageForDelete();
                if (stage is not null) MarkWindowsDeleteOnClose(stage);
            }
            catch { }
        }
        else if (_stageName is not null)
        {
            try
            {
                int dirFd = _directoryHandle.DangerousGetHandle().ToInt32();
                if (_stageLinuxIdentity is { } expected &&
                    TryStatxAt(dirFd, _stageName, out StatxIdentity named) &&
                    named.Device == expected.Device && named.Inode == expected.Inode)
                {
                    unlinkat(dirFd, _stageName, 0);
                }
            }
            catch { }
        }
        for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
    }

    private readonly record struct StatxIdentity(
        ulong Device, ulong Inode, uint Links, bool IsRegular, ulong Size);

    private readonly record struct WinFileIdentity(
        uint Volume, uint IndexHigh, uint IndexLow)
    {
        internal static WinFileIdentity From(WinFileInfo info) =>
            new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    private sealed record SidecarGuard(string Name, SafeFileHandle Handle,
        WinFileIdentity? WindowsIdentity, StatxIdentity? LinuxIdentity,
        bool DeleteAccessHeld, bool MayContainSqliteBytes);

    private sealed record WindowsArtifactCandidate(string Name, SafeFileHandle Handle,
        WinFileIdentity Identity, uint Links);

    private sealed record LinuxArtifactCandidate(string Name, SafeFileHandle Handle,
        StatxIdentity Identity);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { internal uint FileAttributes; internal uint ReparseTag; }

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
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,
        int infoClass, out FileAttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,
        out WinFileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file,
        int infoClass, IntPtr info, uint size);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int openat(int dirFd, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int mkdirat(int dirFd, string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int dirFd, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameat(int oldDirFd, string oldPath, int newDirFd, string newPath);
    [DllImport("libc", SetLastError = true)] private static extern int linkat(int oldDirFd, string oldPath, int newDirFd, string newPath, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirFd, string path, int flags, uint mask, IntPtr info);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
