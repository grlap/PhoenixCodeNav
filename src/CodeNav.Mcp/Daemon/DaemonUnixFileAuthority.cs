using System.Runtime.InteropServices;

namespace CodeNav.Mcp.Daemon;

internal readonly record struct DaemonUnixFileInfo(uint UserId, ushort Mode)
{
    internal bool IsDirectory => (Mode & 0xF000) == 0x4000;
    internal bool IsRegular => (Mode & 0xF000) == 0x8000;
    internal bool IsSocket => (Mode & 0xF000) == 0xC000;
}

/// <summary>Small owner/no-follow boundary for the per-user Unix runtime directory and socket.</summary>
internal static class DaemonUnixFileAuthority
{
    private const uint StatxBasicStats = 0x000007ff;
    private const int AtSymlinkNoFollow = 0x100;

    internal static string ResolveExistingDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        IntPtr resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw new IOException("Phoenix daemon runtime parent could not be canonicalized.");
        try
        {
            string? value = Marshal.PtrToStringUTF8(resolved);
            if (string.IsNullOrWhiteSpace(value) || !Directory.Exists(value))
                throw new IOException("Phoenix daemon runtime parent is unavailable.");
            return value;
        }
        finally
        {
            free(resolved);
        }
    }

    internal static void EnsureOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint uid = geteuid();
        if (!Directory.Exists(path))
        {
            string? parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent) || !CanCreateChildDirectory(parent))
                throw new IOException(
                    "Phoenix daemon runtime parent cannot create the product directory.");
            Directory.CreateDirectory(path, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (!TryLStat(path, out DaemonUnixFileInfo info) ||
            !info.IsDirectory || info.UserId != uid)
            throw new IOException("Phoenix daemon runtime directory ownership is unsafe.");

        const UnixFileMode ownerOnly = UnixFileMode.UserRead |
                                       UnixFileMode.UserWrite |
                                       UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, ownerOnly);
        if (!TryLStat(path, out info) || !info.IsDirectory || info.UserId != uid ||
            ((UnixFileMode)info.Mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Phoenix daemon runtime directory permissions are unsafe.");
    }

    private static bool CanCreateChildDirectory(string parent)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        UnixFileMode mode = File.GetUnixFileMode(parent);
        return HasWriteAndExecute(mode, UnixFileMode.UserWrite, UnixFileMode.UserExecute) ||
               HasWriteAndExecute(mode, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute) ||
               HasWriteAndExecute(mode, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute);
    }

    private static bool HasWriteAndExecute(
        UnixFileMode mode,
        UnixFileMode write,
        UnixFileMode execute) =>
        (mode & write) != 0 && (mode & execute) != 0;

    internal static void VerifyOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!TryLStat(path, out DaemonUnixFileInfo info) ||
            !info.IsDirectory || info.UserId != geteuid() ||
            ((UnixFileMode)info.Mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Phoenix daemon runtime directory authority is unsafe.");
    }

    internal static bool TryRemoveOwnedSocket(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            // File.Exists is false for a Unix socket on some runtimes, so always lstat below.
            if (!TryLStat(path, out _)) return true;
        }

        if (!TryLStat(path, out DaemonUnixFileInfo info) ||
            !info.IsSocket || info.UserId != geteuid())
            return false;
        File.Delete(path);
        return !TryLStat(path, out _);
    }

    internal static void VerifyOwnedSocket(string path)
    {
        if (!TryLStat(path, out DaemonUnixFileInfo info) ||
            !info.IsSocket || info.UserId != geteuid())
            throw new IOException("Phoenix daemon socket ownership is unsafe.");
    }

    internal static bool TryLStat(string path, out DaemonUnixFileInfo info)
    {
        info = default;
        if (OperatingSystem.IsWindows()) return false;
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (int offset = 0; offset < 256; offset += sizeof(long))
                Marshal.WriteInt64(buffer, offset, 0);
            if (OperatingSystem.IsMacOS())
            {
                if (lstat_macos(path, buffer) != 0) return false;
                ushort mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
                uint uid = unchecked((uint)Marshal.ReadInt32(buffer, 16));
                info = new DaemonUnixFileInfo(uid, mode);
                return true;
            }

            if (statx(-100, path, AtSymlinkNoFollow, StatxBasicStats, buffer) != 0)
                return false;
            uint linuxUid = unchecked((uint)Marshal.ReadInt32(buffer, 20));
            ushort linuxMode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            info = new DaemonUnixFileInfo(linuxUid, linuxMode);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolvedPath);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat_macos(string path, IntPtr info);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directoryFd, string path, int flags,
        uint mask, IntPtr info);
}
