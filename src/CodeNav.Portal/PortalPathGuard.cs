using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeNav.Portal;

/// <summary>
/// Opens workspace files through an anchored, no-follow authority. Unix walks from an open
/// workspace directory with openat(2); Windows verifies every component with
/// FILE_FLAG_OPEN_REPARSE_POINT and checks the opened leaf's final handle path against the
/// separately opened workspace-root handle. Only regular disk files are returned.
/// </summary>
internal static class PortalPathGuard
{
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsFileAttributeNormal = 0x00000080;
    private const uint WindowsFileFlagOpenReparsePoint = 0x00200000;
    private const uint WindowsFileFlagBackupSemantics = 0x02000000;
    private const uint WindowsFileFlagSequentialScan = 0x08000000;
    private const uint WindowsFileTypeDisk = 0x0001;
    private const int WindowsFileIdBothDirectoryInfo = 10;
    private const int WindowsFileIdBothDirectoryRestartInfo = 11;
    private const int WindowsNoMoreFiles = 18;
    private const int WindowsDirectoryBufferBytes = 64 * 1024;
    private const int WindowsDirectoryNameLengthOffset = 60;
    private const int WindowsDirectoryNameOffset = 104;
    private const int UnixReadOnly = 0;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixFileTypeMask = 0xF000;

    internal enum EntryState
    {
        Missing,
        Safe,
        Unsafe
    }

    internal static EntryState InspectDirectory(
        string workspaceRoot,
        string relativePath,
        out string fullPath)
    {
        if (!TryNormalize(workspaceRoot, relativePath, out _, out fullPath))
            return EntryState.Unsafe;

        EntryState state = Open(
            workspaceRoot,
            relativePath,
            expectDirectory: true,
            out PortalRegularFile? opened);
        opened?.Dispose();
        return state;
    }

    internal static EntryState OpenDirectory(
        string workspaceRoot,
        string relativePath,
        out PortalRegularFile? directory) =>
        Open(workspaceRoot, relativePath, expectDirectory: true, out directory);

    /// <summary>Gets the physical directory identity while deliberately following path aliases.
    /// Coordination uses this only for an already selected workspace root, so symlink and
    /// case aliases converge without turning the path into read authority.</summary>
    internal static bool TryGetDirectoryIdentity(
        string path,
        out PortalFileIdentity identity)
    {
        identity = default;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using SafeFileHandle handle = CreateFileW(
                    path,
                    WindowsGenericRead,
                    WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                    IntPtr.Zero,
                    WindowsOpenExisting,
                    WindowsFileAttributeNormal | WindowsFileFlagBackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid
                    || !TryReadWindowsMetadata(handle, out PortalFileMetadata metadata)
                    || !metadata.IsDirectory)
                {
                    return false;
                }
                identity = metadata.Identity;
                return true;
            }

            int flags = UnixReadOnly
                | UnixCloseOnExec
                | UnixNonBlocking
                | UnixDirectoryFlag;
            int descriptor = UnixOpen(path, flags);
            if (descriptor < 0)
                return false;
            using var directory = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            if (!TryReadUnixMetadata(directory, out PortalFileMetadata unixMetadata)
                || !unixMetadata.IsDirectory)
            {
                return false;
            }
            identity = unixMetadata.Identity;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Enumerates names from the already-opened directory authority. The caller controls
    /// the bound; the path is never reopened, so a replacement link cannot redirect discovery
    /// between validation and enumeration.</summary>
    internal static bool TryEnumerateDirectoryNames(
        PortalRegularFile directory,
        int maximum,
        out string[] names)
    {
        names = [];
        if (maximum < 1 || !directory.Metadata.IsDirectory)
            return false;

        try
        {
            return OperatingSystem.IsWindows()
                ? TryEnumerateWindows(directory.Handle, maximum, out names)
                : TryEnumerateUnix(directory.Handle, maximum, out names);
        }
        catch
        {
            names = [];
            return false;
        }
    }

    internal static EntryState InspectRegularFile(
        string workspaceRoot,
        string relativePath,
        out string fullPath)
    {
        if (!TryNormalize(workspaceRoot, relativePath, out _, out fullPath))
            return EntryState.Unsafe;

        EntryState state = OpenRegularFile(workspaceRoot, relativePath, out PortalRegularFile? file);
        file?.Dispose();
        return state;
    }

    internal static EntryState OpenRegularFile(
        string workspaceRoot,
        string relativePath,
        out PortalRegularFile? file) =>
        Open(workspaceRoot, relativePath, expectDirectory: false, out file);

    private static EntryState Open(
        string workspaceRoot,
        string relativePath,
        bool expectDirectory,
        out PortalRegularFile? opened)
    {
        opened = null;
        if (!TryNormalize(
                workspaceRoot,
                relativePath,
                out string root,
                out string fullPath))
        {
            return EntryState.Unsafe;
        }

        return OperatingSystem.IsWindows()
            ? OpenWindows(root, relativePath, fullPath, expectDirectory, out opened)
            : OpenUnix(root, relativePath, expectDirectory, out opened);
    }

    private static EntryState OpenUnix(
        string root,
        string relativePath,
        bool expectDirectory,
        out PortalRegularFile? opened)
    {
        opened = null;
        int directoryFlags = UnixReadOnly
            | UnixCloseOnExec
            | UnixNoFollow
            | UnixNonBlocking
            | UnixDirectoryFlag;
        int rootDescriptor = UnixOpen(root, directoryFlags);
        if (rootDescriptor < 0)
            return UnixErrorState();

        SafeFileHandle current = new((IntPtr)rootDescriptor, ownsHandle: true);
        try
        {
            string[] components = SplitRelativePath(relativePath);
            for (int i = 0; i < components.Length; i++)
            {
                bool directory = i < components.Length - 1 || expectDirectory;
                int flags = UnixReadOnly
                    | UnixCloseOnExec
                    | UnixNoFollow
                    | UnixNonBlocking
                    | (directory ? UnixDirectoryFlag : 0);
                int descriptor = UnixOpenAt(
                    current.DangerousGetHandle().ToInt32(),
                    components[i],
                    flags);
                if (descriptor < 0)
                    return UnixErrorState();

                var next = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                current.Dispose();
                current = next;
            }

            if (!TryReadUnixMetadata(current, out PortalFileMetadata metadata)
                || metadata.IsDirectory != expectDirectory)
            {
                return EntryState.Unsafe;
            }

            if (expectDirectory)
            {
                opened = new PortalRegularFile(current, metadata, createStream: false);
                current = null!;
                return EntryState.Safe;
            }

            if (!metadata.IsRegularFile)
                return EntryState.Unsafe;

            opened = new PortalRegularFile(current, metadata, createStream: true);
            current = null!;
            return EntryState.Safe;
        }
        catch
        {
            return EntryState.Unsafe;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static EntryState OpenWindows(
        string root,
        string relativePath,
        string fullPath,
        bool expectDirectory,
        out PortalRegularFile? opened)
    {
        opened = null;
        using SafeFileHandle rootHandle = WindowsOpen(root, directory: true);
        if (rootHandle.IsInvalid)
            return WindowsErrorState();
        if (!TryReadWindowsMetadata(rootHandle, out PortalFileMetadata rootMetadata)
            || !rootMetadata.IsDirectory
            || rootMetadata.IsReparsePoint
            || !TryGetFinalPath(rootHandle, out string rootFinalPath))
        {
            return EntryState.Unsafe;
        }

        string currentPath = root;
        string[] components = SplitRelativePath(relativePath);
        SafeFileHandle? leaf = null;
        try
        {
            for (int i = 0; i < components.Length; i++)
            {
                bool directory = i < components.Length - 1 || expectDirectory;
                currentPath = Path.Combine(currentPath, components[i]);
                SafeFileHandle component = WindowsOpen(currentPath, directory);
                if (component.IsInvalid)
                {
                    component.Dispose();
                    return WindowsErrorState();
                }

                if (!TryReadWindowsMetadata(component, out PortalFileMetadata metadata)
                    || metadata.IsDirectory != directory
                    || metadata.IsReparsePoint)
                {
                    component.Dispose();
                    return EntryState.Unsafe;
                }

                leaf?.Dispose();
                leaf = component;
            }

            if (leaf is null
                || !TryGetFinalPath(leaf, out string leafFinalPath)
                || !IsContained(rootFinalPath, leafFinalPath))
            {
                return EntryState.Unsafe;
            }

            if (!TryReadWindowsMetadata(leaf, out PortalFileMetadata leafMetadata)
                || leafMetadata.IsDirectory != expectDirectory
                || (!expectDirectory && !leafMetadata.IsRegularFile))
            {
                return EntryState.Unsafe;
            }

            opened = new PortalRegularFile(
                leaf,
                leafMetadata,
                createStream: !expectDirectory);
            leaf = null;
            return EntryState.Safe;
        }
        catch
        {
            return EntryState.Unsafe;
        }
        finally
        {
            leaf?.Dispose();
        }
    }

    private static SafeFileHandle WindowsOpen(string path, bool directory)
    {
        uint flags = WindowsFileAttributeNormal
            | WindowsFileFlagOpenReparsePoint
            | (directory
                ? WindowsFileFlagBackupSemantics
                : WindowsFileFlagSequentialScan);
        return CreateFileW(
            path,
            WindowsGenericRead,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            IntPtr.Zero,
            WindowsOpenExisting,
            flags,
            IntPtr.Zero);
    }

    private static bool TryReadWindowsMetadata(
        SafeFileHandle handle,
        out PortalFileMetadata metadata)
    {
        metadata = default;
        if (GetFileType(handle) != WindowsFileTypeDisk
            || !GetFileInformationByHandle(handle, out WindowsFileInformation info))
        {
            return false;
        }

        FileAttributes attributes = (FileAttributes)info.FileAttributes;
        bool directory = (attributes & FileAttributes.Directory) != 0;
        bool reparse = (attributes & FileAttributes.ReparsePoint) != 0;
        ulong length = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
        ulong fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        long fileTime = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
        DateTimeOffset lastWriteUtc;
        try
        {
            lastWriteUtc = DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch
        {
            lastWriteUtc = DateTimeOffset.MinValue;
        }

        metadata = new PortalFileMetadata(
            new PortalFileIdentity(info.VolumeSerialNumber, fileIndex),
            checked((long)Math.Min(length, long.MaxValue)),
            lastWriteUtc,
            IsRegularFile: !directory && !reparse,
            IsDirectory: directory,
            IsReparsePoint: reparse);
        return true;
    }

    private static bool TryReadUnixMetadata(
        SafeFileHandle handle,
        out PortalFileMetadata metadata)
    {
        int descriptor = handle.DangerousGetHandle().ToInt32();
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (UnixFStat(descriptor, buffer) != 0)
            {
                metadata = default;
                return false;
            }

            if (OperatingSystem.IsMacOS())
            {
                int type = Marshal.ReadInt16(buffer, 4) & UnixFileTypeMask;
                metadata = new PortalFileMetadata(
                    new PortalFileIdentity(
                        unchecked((uint)Marshal.ReadInt32(buffer, 0)),
                        unchecked((ulong)Marshal.ReadInt64(buffer, 8))),
                    Math.Max(0, Marshal.ReadInt64(buffer, 96)),
                    UnixTimestamp(
                        Marshal.ReadInt64(buffer, 48),
                        Marshal.ReadInt64(buffer, 56)),
                    type == UnixRegularFile,
                    type == UnixDirectory,
                    IsReparsePoint: false);
                return true;
            }

            int modeOffset = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => 24,
                Architecture.Arm64 => 16,
                _ => -1
            };
            if (modeOffset < 0)
            {
                metadata = default;
                return false;
            }
            int linuxType = Marshal.ReadInt32(buffer, modeOffset) & UnixFileTypeMask;
            metadata = new PortalFileMetadata(
                new PortalFileIdentity(
                    unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                    unchecked((ulong)Marshal.ReadInt64(buffer, 8))),
                Math.Max(0, Marshal.ReadInt64(buffer, 48)),
                UnixTimestamp(
                    Marshal.ReadInt64(buffer, 88),
                    Marshal.ReadInt64(buffer, 96)),
                linuxType == UnixRegularFile,
                linuxType == UnixDirectory,
                IsReparsePoint: false);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryEnumerateUnix(
        SafeFileHandle directory,
        int maximum,
        out string[] names)
    {
        names = [];
        int duplicate = UnixDup(directory.DangerousGetHandle().ToInt32());
        if (duplicate < 0)
            return false;
        IntPtr stream = UnixFdOpenDirectory(duplicate);
        if (stream == IntPtr.Zero)
        {
            UnixClose(duplicate);
            return false;
        }

        var result = new List<string>(Math.Min(maximum, 256));
        try
        {
            while (result.Count < maximum)
            {
                Marshal.SetLastPInvokeError(0);
                IntPtr entry = UnixReadDirectory(stream);
                if (entry == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                        return false;
                    names = result.ToArray();
                    return true;
                }

                int nameLength;
                int nameOffset;
                if (OperatingSystem.IsMacOS())
                {
                    nameLength = unchecked((ushort)Marshal.ReadInt16(entry, 18));
                    nameOffset = 21;
                }
                else
                {
                    int recordLength = unchecked((ushort)Marshal.ReadInt16(entry, 16));
                    nameOffset = 19;
                    nameLength = Math.Max(0, recordLength - nameOffset);
                }

                string? name = ReadNullTerminatedUtf8(entry + nameOffset, nameLength);
                if (!string.IsNullOrEmpty(name) && name is not "." and not "..")
                    result.Add(name);
            }
            names = result.ToArray();
            return true;
        }
        finally
        {
            UnixCloseDirectory(stream);
        }
    }

    private static bool TryEnumerateWindows(
        SafeFileHandle directory,
        int maximum,
        out string[] names)
    {
        names = [];
        IntPtr buffer = Marshal.AllocHGlobal(WindowsDirectoryBufferBytes);
        var result = new List<string>(Math.Min(maximum, 256));
        bool restart = true;
        try
        {
            while (result.Count < maximum)
            {
                bool read = GetFileInformationByHandleEx(
                    directory,
                    restart
                        ? WindowsFileIdBothDirectoryRestartInfo
                        : WindowsFileIdBothDirectoryInfo,
                    buffer,
                    WindowsDirectoryBufferBytes);
                restart = false;
                if (!read)
                {
                    if (Marshal.GetLastPInvokeError() == WindowsNoMoreFiles)
                    {
                        names = result.ToArray();
                        return true;
                    }
                    return false;
                }

                int offset = 0;
                while (result.Count < maximum)
                {
                    int nameBytes = Marshal.ReadInt32(
                        buffer,
                        offset + WindowsDirectoryNameLengthOffset);
                    if (nameBytes < 0
                        || nameBytes > WindowsDirectoryBufferBytes
                        || offset + WindowsDirectoryNameOffset + nameBytes
                            > WindowsDirectoryBufferBytes)
                    {
                        return false;
                    }
                    string? name = Marshal.PtrToStringUni(
                        buffer + offset + WindowsDirectoryNameOffset,
                        nameBytes / 2);
                    if (!string.IsNullOrEmpty(name) && name is not "." and not "..")
                        result.Add(name);

                    int next = Marshal.ReadInt32(buffer, offset);
                    if (next == 0)
                        break;
                    if (next < WindowsDirectoryNameOffset
                        || offset + next >= WindowsDirectoryBufferBytes)
                    {
                        return false;
                    }
                    offset += next;
                }
            }
            names = result.ToArray();
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DateTimeOffset UnixTimestamp(long seconds, long nanoseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds)
                .AddTicks(Math.Clamp(nanoseconds / 100, 0, TimeSpan.TicksPerSecond - 1));
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    internal static string? ReadNullTerminatedUtf8(IntPtr address, int maximumBytes)
    {
        int length = 0;
        while (length < maximumBytes && Marshal.ReadByte(address, length) != 0)
            length++;
        return length == 0 ? string.Empty : Marshal.PtrToStringUTF8(address, length);
    }

    private static bool TryGetFinalPath(SafeFileHandle handle, out string path)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (length == 0 || length >= buffer.Capacity)
        {
            path = string.Empty;
            return false;
        }

        path = buffer.ToString();
        return true;
    }

    private static EntryState UnixErrorState()
    {
        int error = Marshal.GetLastPInvokeError();
        return error == 2
            ? EntryState.Missing
            : EntryState.Unsafe;
    }

    private static EntryState WindowsErrorState()
    {
        int error = Marshal.GetLastPInvokeError();
        return error is 2 or 3
            ? EntryState.Missing
            : EntryState.Unsafe;
    }

    private static bool TryNormalize(
        string workspaceRoot,
        string relativePath,
        out string root,
        out string fullPath)
    {
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            fullPath = Path.GetFullPath(relativePath, root);
        }
        catch
        {
            root = string.Empty;
            fullPath = string.Empty;
            return false;
        }

        string[] components = SplitRelativePath(relativePath);
        return components.Length > 0
            && !Path.IsPathFullyQualified(relativePath)
            && components.All(component => component is not "." and not "..")
            && IsContained(root, fullPath);
    }

    private static string[] SplitRelativePath(string relativePath) =>
        relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static bool IsContained(string root, string candidate)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(normalizedRoot, normalizedCandidate, comparison)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    private static int UnixCloseOnExec =>
        OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    private static int UnixNoFollow =>
        OperatingSystem.IsMacOS()
            ? 0x00000100
            : LinuxNoFollowForArchitecture(RuntimeInformation.ProcessArchitecture);

    private static int UnixNonBlocking =>
        OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;

    private static int UnixDirectoryFlag =>
        OperatingSystem.IsMacOS()
            ? 0x00100000
            : LinuxDirectoryForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal static int LinuxDirectoryForArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X86 or
            Architecture.X64 or
            Architecture.S390x or
            Architecture.LoongArch64 or
            Architecture.RiscV64 => 0x00010000,
            Architecture.Arm or
            Architecture.Armv6 or
            Architecture.Arm64 or
            Architecture.Ppc64le => 0x00004000,
            _ => throw UnsupportedLinuxArchitecture(architecture)
        };

    internal static int LinuxNoFollowForArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X86 or
            Architecture.X64 or
            Architecture.S390x or
            Architecture.LoongArch64 or
            Architecture.RiscV64 => 0x00020000,
            Architecture.Arm or
            Architecture.Armv6 or
            Architecture.Arm64 or
            Architecture.Ppc64le => 0x00008000,
            _ => throw UnsupportedLinuxArchitecture(architecture)
        };

    private static PlatformNotSupportedException UnsupportedLinuxArchitecture(
        Architecture architecture) =>
        new($"Linux open(2) flag mapping is not defined for {architecture}.");

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int UnixOpenAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int UnixFStat(int descriptor, IntPtr stat);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int UnixDup(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr UnixFdOpenDirectory(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr UnixReadDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int UnixCloseDirectory(IntPtr directory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

internal readonly record struct PortalFileIdentity(ulong Authority, ulong File);

internal readonly record struct PortalFileMetadata(
    PortalFileIdentity Identity,
    long Length,
    DateTimeOffset LastWriteUtc,
    bool IsRegularFile,
    bool IsDirectory,
    bool IsReparsePoint);

internal sealed class PortalRegularFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    private FileStream? _stream;

    internal PortalRegularFile(
        SafeFileHandle handle,
        PortalFileMetadata metadata,
        bool createStream)
    {
        _handle = handle;
        Metadata = metadata;
        if (createStream)
        {
            _stream = new FileStream(
                _handle,
                FileAccess.Read,
                64 * 1024,
                isAsync: false);
        }
    }

    internal PortalFileMetadata Metadata { get; }

    internal SafeFileHandle Handle => _handle;

    internal FileStream Stream =>
        _stream ?? throw new InvalidOperationException("The opened entry is a directory.");

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (!_handle.IsClosed)
            _handle.Dispose();
    }
}
